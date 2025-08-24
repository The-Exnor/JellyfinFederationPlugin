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
using JellyfinFederationPlugin.Caching;
using JellyfinFederationPlugin.Failover;
using JellyfinFederationPlugin.Bandwidth;

namespace JellyfinFederationPlugin.Streaming
{
    public class EnhancedFederationStreamingService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<EnhancedFederationStreamingService> _logger;
        private readonly FederationCacheService _cacheService;
        private readonly FederationFailoverService _failoverService;
        private readonly FederationBandwidthManager _bandwidthManager;
        private readonly Dictionary<string, StreamSession> _activeSessions;
        private readonly SemaphoreSlim _sessionSemaphore;
        private bool _disposed = false;

        public EnhancedFederationStreamingService(
            HttpClient httpClient,
            ILogger<EnhancedFederationStreamingService> logger,
            FederationCacheService cacheService,
            FederationFailoverService failoverService,
            FederationBandwidthManager bandwidthManager)
        {
            _httpClient = httpClient;
            _logger = logger;
            _cacheService = cacheService;
            _failoverService = failoverService;
            _bandwidthManager = bandwidthManager;
            _activeSessions = new Dictionary<string, StreamSession>();
            _sessionSemaphore = new SemaphoreSlim(1, 1);
            
            // Configure HTTP client for streaming
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
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

                // Check cache first
                var config = Plugin.Instance.Configuration;
                if (config.Caching.EnableMetadataCache)
                {
                    var cachedMetadata = await _cacheService.GetCachedMetadataAsync(federationInfo.ServerId, federationInfo.ItemId);
                    if (cachedMetadata != null)
                    {
                        _logger.LogDebug($"Using cached metadata for {federationInfo.ItemId}");
                        return CreateMediaSourceFromCache(cachedMetadata, federationInfo);
                    }
                }

                // Get best available server with failover support
                var server = await _failoverService.GetBestAvailableServerAsync(federationInfo.ItemId, federationInfo.ServerId);
                if (server == null)
                {
                    _logger.LogError($"No available servers for federated item: {federationInfo.ItemId}");
                    return null;
                }

                // Get media info with failover
                var mediaInfo = await _failoverService.ExecuteWithFailoverAsync(
                    federationInfo.ItemId,
                    async (srv) => await GetRemoteMediaInfoAsync(srv, federationInfo.ItemId, cancellationToken),
                    server.ServerUrl);

                if (mediaInfo?.MediaSources?.Any() == true)
                {
                    // Convert MediaSourceInfo to BaseItemDto for caching
                    var convertedItem = await ConvertMediaSourceToBaseItemDto(mediaInfo.MediaSources.First(), federationInfo, server);
                    
                    // Cache the converted metadata if caching is enabled
                    if (config.Caching.EnableMetadataCache && convertedItem != null)
                    {
                        await _cacheService.SetCachedMetadataAsync(server.ServerUrl, federationInfo.ItemId, convertedItem);
                        _logger.LogDebug($"Cached metadata for item {federationInfo.ItemId} from server {server.ServerUrl}");
                    }
                    
                    return CreateEnhancedMediaSource(federationInfo, server, mediaInfo.MediaSources.First());
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting media source for federated item: {item.Name}");
                return null;
            }
        }

        public async Task<Stream> GetStreamAsync(string mediaSourceId, string streamUrl, string clientId = null, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation($"Starting enhanced federation stream: {mediaSourceId}");
                
                var federationInfo = ParseMediaSourceId(mediaSourceId);
                if (federationInfo == null)
                {
                    throw new ArgumentException($"Invalid federation media source ID: {mediaSourceId}");
                }

                // Get best server with failover
                var server = await _failoverService.GetBestAvailableServerAsync(federationInfo.ItemId, federationInfo.ServerId);
                if (server == null)
                {
                    throw new InvalidOperationException($"No available servers for item: {federationInfo.ItemId}");
                }

                // Create bandwidth session with quality adaptation
                var config = Plugin.Instance.Configuration;
                var requestedQuality = GetRequestedQuality(config.Bandwidth.DefaultQuality);
                var bandwidthSession = await _bandwidthManager.CreateSessionAsync(
                    Guid.NewGuid().ToString(),
                    server.ServerUrl,
                    clientId ?? "unknown",
                    federationInfo.ItemId,
                    requestedQuality);

                if (bandwidthSession == null)
                {
                    throw new InvalidOperationException("Cannot accommodate stream due to bandwidth limits");
                }

                // Create streaming session
                var sessionId = bandwidthSession.SessionId;
                var session = new StreamSession
                {
                    SessionId = sessionId,
                    MediaSourceId = mediaSourceId,
                    ServerId = server.ServerUrl,
                    ItemId = federationInfo.ItemId,
                    StartTime = DateTime.UtcNow,
                    Quality = bandwidthSession.AdaptedQuality,
                    BandwidthSession = bandwidthSession
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

                // Create stream URL with quality parameters
                var streamParameters = BuildStreamParameters(server, federationInfo.ItemId, bandwidthSession.AdaptedQuality);
                var remoteStreamUrl = $"{GetServerBaseUrl(server)}/Videos/{federationInfo.ItemId}/stream?{streamParameters}";

                // Create enhanced proxy stream
                var proxyStream = new EnhancedFederationProxyStream(
                    _httpClient,
                    remoteStreamUrl,
                    session,
                    _logger,
                    _bandwidthManager,
                    () => RemoveSession(sessionId));

                return proxyStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating enhanced federation stream: {mediaSourceId}");
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
            string clientId = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation($"Starting enhanced federation transcoded stream: {mediaSourceId}");
                
                var federationInfo = ParseMediaSourceId(mediaSourceId);
                if (federationInfo == null)
                {
                    throw new ArgumentException($"Invalid federation media source ID: {mediaSourceId}");
                }

                var server = await _failoverService.GetBestAvailableServerAsync(federationInfo.ItemId, federationInfo.ServerId);
                if (server == null)
                {
                    throw new InvalidOperationException($"No available servers for item: {federationInfo.ItemId}");
                }

                // Create quality based on transcode parameters
                var transcodeQuality = new StreamQuality
                {
                    Name = $"{container}_{videoBitrate ?? 0}",
                    Bitrate = videoBitrate ?? 4000000,
                    RequiredBandwidth = (videoBitrate ?? 4000000) + (audioBitrate ?? 128000)
                };

                var bandwidthSession = await _bandwidthManager.CreateSessionAsync(
                    Guid.NewGuid().ToString(),
                    server.ServerUrl,
                    clientId ?? "unknown",
                    federationInfo.ItemId,
                    transcodeQuality);

                if (bandwidthSession == null)
                {
                    throw new InvalidOperationException("Cannot accommodate transcoded stream due to bandwidth limits");
                }

                // Create streaming session
                var sessionId = bandwidthSession.SessionId;
                var session = new StreamSession
                {
                    SessionId = sessionId,
                    MediaSourceId = mediaSourceId,
                    ServerId = server.ServerUrl,
                    ItemId = federationInfo.ItemId,
                    StartTime = DateTime.UtcNow,
                    Quality = bandwidthSession.AdaptedQuality,
                    IsTranscoding = true,
                    TranscodeContainer = container,
                    BandwidthSession = bandwidthSession
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

                // Build transcoding parameters
                var parameters = BuildTranscodeParameters(server, container, videoCodec, audioCodec, videoBitrate, audioBitrate);
                var transcodeUrl = $"{GetServerBaseUrl(server)}/Videos/{federationInfo.ItemId}/stream.{container}?{parameters}";

                var proxyStream = new EnhancedFederationProxyStream(
                    _httpClient,
                    transcodeUrl,
                    session,
                    _logger,
                    _bandwidthManager,
                    () => RemoveSession(sessionId));

                return proxyStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating enhanced federation transcoded stream: {mediaSourceId}");
                throw;
            }
        }

        #region Helper Methods

        private async Task<PlaybackInfoResponse> GetRemoteMediaInfoAsync(
            PluginConfiguration.FederatedServer server,
            string itemId,
            CancellationToken cancellationToken)
        {
            try
            {
                var baseUrl = GetServerBaseUrl(server);
                var infoUrl = $"{baseUrl}/Items/{itemId}/PlaybackInfo?api_key={server.ApiKey}";

                using var request = new HttpRequestMessage(HttpMethod.Get, infoUrl);
                request.Headers.Add("X-Emby-Authorization", 
                    $"MediaBrowser Client=\"JellyfinFederation\", Device=\"FederationPlugin\", DeviceId=\"jellyfin-federation\", Version=\"1.0.0\", Token=\"{server.ApiKey}\"");

                var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
                return System.Text.Json.JsonSerializer.Deserialize<PlaybackInfoResponse>(jsonContent, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting remote media info: {server.ServerUrl}/Items/{itemId}");
                throw;
            }
        }

        private MediaSourceInfo CreateMediaSourceFromCache(BaseItemDto cachedItem, FederationPathInfo federationInfo)
        {
            return new MediaSourceInfo
            {
                Id = $"federation_{federationInfo.ServerId}_{federationInfo.ItemId}",
                Name = $"Federation - {cachedItem.Name} (Cached)",
                Path = $"federation://{federationInfo.ServerId}/{federationInfo.ItemId}",
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                RequiresOpening = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Type = MediaSourceType.Default
            };
        }

        private MediaSourceInfo CreateEnhancedMediaSource(FederationPathInfo federationInfo, PluginConfiguration.FederatedServer server, MediaSourceInfo remoteSource)
        {
            return new MediaSourceInfo
            {
                Id = $"federation_{server.ServerUrl}_{federationInfo.ItemId}",
                Name = $"Federation - {remoteSource.Name}",
                Path = $"federation://{server.ServerUrl}/{federationInfo.ItemId}",
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                RequiresOpening = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Type = MediaSourceType.Default,
                Bitrate = remoteSource.Bitrate,
                Container = remoteSource.Container,
                RunTimeTicks = remoteSource.RunTimeTicks
            };
        }

        private string BuildStreamParameters(PluginConfiguration.FederatedServer server, string itemId, StreamQuality quality)
        {
            var parameters = new List<string>
            {
                $"api_key={server.ApiKey}",
                $"maxWidth={quality.Width}",
                $"maxHeight={quality.Height}",
                $"videoBitRate={quality.Bitrate}"
            };

            return string.Join("&", parameters);
        }

        private string BuildTranscodeParameters(PluginConfiguration.FederatedServer server, string container, 
            string videoCodec, string audioCodec, int? videoBitrate, int? audioBitrate)
        {
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

            return string.Join("&", parameters);
        }

        private StreamQuality GetRequestedQuality(string qualityName)
        {
            var qualities = new[]
            {
                new StreamQuality { Name = "4K", Width = 3840, Height = 2160, Bitrate = 25000000, RequiredBandwidth = 30000000 },
                new StreamQuality { Name = "1080p", Width = 1920, Height = 1080, Bitrate = 8000000, RequiredBandwidth = 10000000 },
                new StreamQuality { Name = "720p", Width = 1280, Height = 720, Bitrate = 4000000, RequiredBandwidth = 5000000 },
                new StreamQuality { Name = "480p", Width = 854, Height = 480, Bitrate = 2000000, RequiredBandwidth = 2500000 }
            };

            return qualities.FirstOrDefault(q => q.Name == qualityName) ?? qualities[1]; // Default to 1080p
        }

        private string GetServerBaseUrl(PluginConfiguration.FederatedServer server)
        {
            return server.Port > 0 ? $"{server.ServerUrl}:{server.Port}" : server.ServerUrl;
        }

        private async Task<BaseItemDto> ConvertMediaSourceToBaseItemDto(MediaSourceInfo mediaSource, FederationPathInfo federationInfo, PluginConfiguration.FederatedServer server)
        {
            try
            {
                // Create a BaseItemDto that represents the media source for caching
                var baseItemDto = new BaseItemDto
                {
                    Id = Guid.Parse(federationInfo.ItemId),
                    Name = mediaSource.Name ?? "Federated Media",
                    ServerId = server.ServerUrl,
                    Type = DetermineItemType(mediaSource),
                    MediaType = DetermineMediaType(mediaSource),
                    Container = mediaSource.Container,
                    RunTimeTicks = mediaSource.RunTimeTicks,
                    Bitrate = mediaSource.Bitrate,
                    Path = $"federation://{server.ServerUrl}/{federationInfo.ItemId}",
                    IsFolder = false,
                    DateCreated = DateTime.UtcNow,
                    DateModified = DateTime.UtcNow,
                    
                    // Copy media streams if available
                    MediaStreams = mediaSource.MediaStreams?.ToList() ?? new List<MediaStream>(),
                    
                    // Set federation-specific properties
                    ExternalUrls = new List<ExternalUrl>
                    {
                        new ExternalUrl
                        {
                            Name = "Federation Source",
                            Url = $"{GetServerBaseUrl(server)}/Items/{federationInfo.ItemId}"
                        }
                    }
                };

                // Add additional metadata if available from media source
                if (mediaSource.SupportsDirectStream)
                {
                    baseItemDto.Tags = baseItemDto.Tags?.ToList() ?? new List<string>();
                    baseItemDto.Tags.Add("DirectStream");
                }

                if (mediaSource.SupportsTranscoding)
                {
                    baseItemDto.Tags = baseItemDto.Tags?.ToList() ?? new List<string>();
                    baseItemDto.Tags.Add("Transcoding");
                }

                _logger.LogDebug($"Converted MediaSourceInfo to BaseItemDto for caching: {baseItemDto.Name} ({baseItemDto.Id})");
                return baseItemDto;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to convert MediaSourceInfo to BaseItemDto for item {federationInfo.ItemId}");
                return null;
            }
        }

        private string DetermineItemType(MediaSourceInfo mediaSource)
        {
            // Determine the item type based on media source properties
            if (mediaSource.VideoType.HasValue)
            {
                return mediaSource.VideoType == VideoType.VideoFile ? "Movie" : "Video";
            }

            // Check container type for hints
            var container = mediaSource.Container?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(container))
            {
                if (new[] { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v" }.Contains(container))
                {
                    return "Movie"; // Default video content to Movie type
                }
                if (new[] { "mp3", "flac", "wav", "m4a", "aac" }.Contains(container))
                {
                    return "Audio";
                }
            }

            // Default fallback
            return "Video";
        }

        private string DetermineMediaType(MediaSourceInfo mediaSource)
        {
            // Determine media type based on media source
            if (mediaSource.VideoType.HasValue || 
                mediaSource.MediaStreams?.Any(s => s.Type == MediaStreamType.Video) == true)
            {
                return "Video";
            }

            if (mediaSource.MediaStreams?.Any(s => s.Type == MediaStreamType.Audio) == true &&
                mediaSource.MediaStreams?.All(s => s.Type != MediaStreamType.Video) == true)
            {
                return "Audio";
            }

            return "Video"; // Default to Video
        }

        #endregion

        #region Session Management

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
                    
                    // Close bandwidth session
                    if (session.BandwidthSession != null)
                    {
                        await _bandwidthManager.CloseSessionAsync(session.BandwidthSession.SessionId);
                    }
                    
                    _logger.LogInformation($"Removed enhanced streaming session: {sessionId}");
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        #endregion

        #region Path Parsing

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

        #endregion

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
            public StreamQuality Quality { get; set; }
            public BandwidthSession BandwidthSession { get; set; }
        }
    }
}