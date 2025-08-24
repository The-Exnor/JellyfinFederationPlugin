using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Configuration;

namespace JellyfinFederationPlugin.Streaming
{
    public class FederationStreamingService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<FederationStreamingService> _logger;
        private readonly Dictionary<string, StreamSession> _activeSessions;
        private readonly SemaphoreSlim _sessionSemaphore;
        private bool _disposed = false;

        public FederationStreamingService(HttpClient httpClient, ILogger<FederationStreamingService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _activeSessions = new Dictionary<string, StreamSession>();
            _sessionSemaphore = new SemaphoreSlim(1, 1);
            
            // Configure HTTP client for streaming
            _httpClient.Timeout = TimeSpan.FromMinutes(30); // Long timeout for streaming
        }

        public async Task<MediaSourceInfo> GetMediaSourceAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            if (!IsFederatedItem(item))
            {
                return null;
            }

            try
            {
                var federationInfo = ParseFederationPath(item.Path);
                if (federationInfo == null)
                {
                    _logger.LogError($"Invalid federation path: {item.Path}");
                    return null;
                }

                var server = GetFederatedServer(federationInfo.ServerId);
                if (server == null)
                {
                    _logger.LogError($"Federated server not found: {federationInfo.ServerId}");
                    return null;
                }

                // Create media source for federation
                var mediaSource = new MediaSourceInfo
                {
                    Id = $"federation_{federationInfo.ServerId}_{federationInfo.ItemId}",
                    Name = $"Federation - {item.Name}",
                    Path = item.Path,
                    Protocol = MediaProtocol.Http,
                    IsRemote = true,
                    RequiresOpening = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    SupportsDirectPlay = false, // We'll proxy all content
                    Type = MediaSourceType.Default
                };

                return mediaSource;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting media source for federated item: {item.Name}");
                return null;
            }
        }

        public async Task<Stream> GetStreamAsync(string mediaSourceId, string streamUrl, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation($"Starting federation stream: {mediaSourceId}");
                
                var federationInfo = ParseMediaSourceId(mediaSourceId);
                if (federationInfo == null)
                {
                    throw new ArgumentException($"Invalid federation media source ID: {mediaSourceId}");
                }

                var server = GetFederatedServer(federationInfo.ServerId);
                if (server == null)
                {
                    throw new InvalidOperationException($"Federated server not found: {federationInfo.ServerId}");
                }

                // Create streaming session
                var sessionId = Guid.NewGuid().ToString();
                var session = new StreamSession
                {
                    SessionId = sessionId,
                    MediaSourceId = mediaSourceId,
                    ServerId = federationInfo.ServerId,
                    ItemId = federationInfo.ItemId,
                    StartTime = DateTime.UtcNow
                };

                await _sessionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    _activeSessions[sessionId] = session;
                }
                finally
                {
                    _sessionSemaphore.Release();
                }

                // Create stream URL for remote server
                var baseUrl = server.Port > 0 ? $"{server.ServerUrl}:{server.Port}" : server.ServerUrl;
                var remoteStreamUrl = $"{baseUrl}/Videos/{federationInfo.ItemId}/stream?api_key={server.ApiKey}";

                // Create proxy stream
                var proxyStream = new FederationProxyStream(
                    _httpClient,
                    remoteStreamUrl,
                    session,
                    _logger,
                    () => RemoveSession(sessionId));

                return proxyStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating federation stream: {mediaSourceId}");
                throw;
            }
        }

        public async Task<Stream> GetTranscodedStreamAsync(
            string mediaSourceId,
            string container,
            string videoCodec,
            string audioCodec,
            int? videoBitrate,
            int? audioBitrate,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation($"Starting federation transcoded stream: {mediaSourceId}");
                
                var federationInfo = ParseMediaSourceId(mediaSourceId);
                if (federationInfo == null)
                {
                    throw new ArgumentException($"Invalid federation media source ID: {mediaSourceId}");
                }

                var server = GetFederatedServer(federationInfo.ServerId);
                if (server == null)
                {
                    throw new InvalidOperationException($"Federated server not found: {federationInfo.ServerId}");
                }

                // Build transcoding parameters
                var parameters = new List<string>
                {
                    $"api_key={server.ApiKey}"
                };

                if (!string.IsNullOrEmpty(container))
                    parameters.Add($"Container={container}");
                
                if (!string.IsNullOrEmpty(videoCodec))
                    parameters.Add($"VideoCodec={videoCodec}");
                
                if (!string.IsNullOrEmpty(audioCodec))
                    parameters.Add($"AudioCodec={audioCodec}");
                
                if (videoBitrate.HasValue)
                    parameters.Add($"VideoBitRate={videoBitrate.Value}");
                
                if (audioBitrate.HasValue)
                    parameters.Add($"AudioBitRate={audioBitrate.Value}");

                // Create transcoding URL for remote server
                var baseUrl = server.Port > 0 ? $"{server.ServerUrl}:{server.Port}" : server.ServerUrl;
                var transcodeUrl = $"{baseUrl}/Videos/{federationInfo.ItemId}/stream.{container}?{string.Join("&", parameters)}";

                // Create streaming session
                var sessionId = Guid.NewGuid().ToString();
                var session = new StreamSession
                {
                    SessionId = sessionId,
                    MediaSourceId = mediaSourceId,
                    ServerId = federationInfo.ServerId,
                    ItemId = federationInfo.ItemId,
                    StartTime = DateTime.UtcNow,
                    IsTranscoding = true,
                    TranscodeContainer = container
                };

                await _sessionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    _activeSessions[sessionId] = session;
                }
                finally
                {
                    _sessionSemaphore.Release();
                }

                // Create proxy stream
                var proxyStream = new FederationProxyStream(
                    _httpClient,
                    transcodeUrl,
                    session,
                    _logger,
                    () => RemoveSession(sessionId));

                return proxyStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating federation transcoded stream: {mediaSourceId}");
                throw;
            }
        }

        public async Task<List<StreamSession>> GetActiveSessionsAsync()
        {
            await _sessionSemaphore.WaitAsync();
            try
            {
                return _activeSessions.Values.ToList();
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        public async Task StopSessionAsync(string sessionId)
        {
            await RemoveSession(sessionId);
        }

        private async Task RemoveSession(string sessionId)
        {
            await _sessionSemaphore.WaitAsync();
            try
            {
                if (_activeSessions.TryGetValue(sessionId, out var session))
                {
                    _activeSessions.Remove(sessionId);
                    _logger.LogInformation($"Removed streaming session: {sessionId}");
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        private bool IsFederatedItem(BaseItem item)
        {
            return item?.Path?.StartsWith("federation://") == true;
        }

        private FederationPathInfo ParseFederationPath(string path)
        {
            try
            {
                if (!path.StartsWith("federation://"))
                    return null;

                var parts = path.Substring("federation://".Length).Split('/');
                if (parts.Length < 2)
                    return null;

                return new FederationPathInfo
                {
                    ServerId = parts[0],
                    ItemId = parts[1]
                };
            }
            catch
            {
                return null;
            }
        }

        private FederationPathInfo ParseMediaSourceId(string mediaSourceId)
        {
            try
            {
                if (!mediaSourceId.StartsWith("federation_"))
                    return null;

                var parts = mediaSourceId.Substring("federation_".Length).Split('_');
                if (parts.Length < 2)
                    return null;

                return new FederationPathInfo
                {
                    ServerId = parts[0],
                    ItemId = parts[1]
                };
            }
            catch
            {
                return null;
            }
        }

        private PluginConfiguration.FederatedServer GetFederatedServer(string serverId)
        {
            var config = Plugin.Instance.Configuration;
            return config.FederatedServers?.FirstOrDefault(s => s.ServerUrl == serverId);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _sessionSemaphore?.Dispose();
                _disposed = true;
            }
        }

        private class FederationPathInfo
        {
            public string ServerId { get; set; }
            public string ItemId { get; set; }
        }

        public class StreamSession
        {
            public string SessionId { get; set; }
            public string MediaSourceId { get; set; }
            public string ServerId { get; set; }
            public string ItemId { get; set; }
            public DateTime StartTime { get; set; }
            public bool IsTranscoding { get; set; }
            public string TranscodeContainer { get; set; }
            public long BytesStreamed { get; set; }
        }
    }
}