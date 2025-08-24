using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Streaming;
using JellyfinFederationPlugin.Bandwidth;

namespace JellyfinFederationPlugin.Streaming
{
    public class EnhancedFederationProxyStream : Stream
    {
        private readonly HttpClient _httpClient;
        private readonly string _remoteUrl;
        private readonly EnhancedFederationStreamingService.StreamSession _session;
        private readonly ILogger _logger;
        private readonly FederationBandwidthManager _bandwidthManager;
        private readonly Action _onDispose;
        
        private HttpResponseMessage _response;
        private Stream _remoteStream;
        private long _position;
        private bool _disposed = false;
        private DateTime _lastBandwidthUpdate = DateTime.UtcNow;
        private long _bytesReadSinceLastUpdate = 0;

        public EnhancedFederationProxyStream(
            HttpClient httpClient,
            string remoteUrl,
            EnhancedFederationStreamingService.StreamSession session,
            ILogger logger,
            FederationBandwidthManager bandwidthManager,
            Action onDispose)
        {
            _httpClient = httpClient;
            _remoteUrl = remoteUrl;
            _session = session;
            _logger = logger;
            _bandwidthManager = bandwidthManager;
            _onDispose = onDispose;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _response?.Content?.Headers?.ContentLength ?? -1;
        
        public override long Position 
        { 
            get => _position; 
            set => Seek(value, SeekOrigin.Begin); 
        }

        private async Task EnsureStreamAsync()
        {
            if (_remoteStream == null)
            {
                try
                {
                    _logger.LogDebug($"Opening enhanced remote stream: {_remoteUrl}");
                    
                    var request = new HttpRequestMessage(HttpMethod.Get, _remoteUrl);
                    
                    // Add range header for seeking if position > 0
                    if (_position > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(_position, null);
                    }

                    // Add quality adaptation headers based on current bandwidth session
                    if (_session.BandwidthSession != null)
                    {
                        var quality = _session.BandwidthSession.AdaptedQuality;
                        request.Headers.Add("X-Federation-Quality", quality.Name);
                        request.Headers.Add("X-Federation-MaxBitrate", quality.Bitrate.ToString());
                    }

                    _response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    _response.EnsureSuccessStatusCode();
                    
                    _remoteStream = await _response.Content.ReadAsStreamAsync();
                    
                    _logger.LogDebug($"Enhanced remote stream opened successfully for session: {_session.SessionId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to open enhanced remote stream: {_remoteUrl}");
                    throw;
                }
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                await EnsureStreamAsync();
                
                var bytesRead = await _remoteStream.ReadAsync(buffer, offset, count, cancellationToken);
                
                if (bytesRead > 0)
                {
                    _position += bytesRead;
                    _session.BytesStreamed += bytesRead;
                    _bytesReadSinceLastUpdate += bytesRead;
                    
                    // Update bandwidth tracking periodically
                    var now = DateTime.UtcNow;
                    if (now - _lastBandwidthUpdate >= TimeSpan.FromSeconds(1))
                    {
                        await UpdateBandwidthTracking();
                        _lastBandwidthUpdate = now;
                        _bytesReadSinceLastUpdate = 0;
                    }
                }
                
                return bytesRead;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error reading from enhanced federation stream: {_session.SessionId}");
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            try
            {
                var newPosition = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => Length + offset,
                    _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
                };

                if (newPosition < 0)
                    newPosition = 0;

                if (newPosition != _position)
                {
                    _logger.LogDebug($"Seeking enhanced federation stream to position: {newPosition}");
                    
                    // Close current stream and reopen with range header
                    CloseRemoteStream();
                    _position = newPosition;
                    
                    // The stream will be reopened with the correct position on next read
                }

                return _position;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error seeking enhanced federation stream: {_session.SessionId}");
                throw;
            }
        }

        private async Task UpdateBandwidthTracking()
        {
            try
            {
                if (_session.BandwidthSession != null && _bytesReadSinceLastUpdate > 0)
                {
                    await _bandwidthManager.UpdateSessionBandwidthAsync(
                        _session.BandwidthSession.SessionId, 
                        _bytesReadSinceLastUpdate);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error updating bandwidth tracking");
            }
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("Setting length is not supported for federation streams");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Writing is not supported for federation streams");
        }

        public override void Flush()
        {
            // No-op for read-only stream
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            // No-op for read-only stream
            await Task.CompletedTask;
        }

        private void CloseRemoteStream()
        {
            try
            {
                _remoteStream?.Dispose();
                _response?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing enhanced remote stream");
            }
            finally
            {
                _remoteStream = null;
                _response = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                try
                {
                    _logger.LogDebug($"Disposing enhanced federation stream: {_session.SessionId}");
                    
                    // Final bandwidth update
                    if (_bytesReadSinceLastUpdate > 0)
                    {
                        _ = Task.Run(() => UpdateBandwidthTracking());
                    }
                    
                    CloseRemoteStream();
                    _onDispose?.Invoke();
                    
                    var duration = DateTime.UtcNow - _session.StartTime;
                    _logger.LogInformation($"Enhanced federation stream closed: {_session.SessionId}, " +
                        $"Duration: {duration}, Bytes: {_session.BytesStreamed}, " +
                        $"Quality: {_session.Quality?.Name ?? "Unknown"}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing enhanced federation stream: {_session.SessionId}");
                }
                finally
                {
                    _disposed = true;
                }
            }
            
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                try
                {
                    _logger.LogDebug($"Disposing enhanced federation stream async: {_session.SessionId}");
                    
                    // Final bandwidth update
                    if (_bytesReadSinceLastUpdate > 0)
                    {
                        await UpdateBandwidthTracking();
                    }
                    
                    CloseRemoteStream();
                    _onDispose?.Invoke();
                    
                    var duration = DateTime.UtcNow - _session.StartTime;
                    _logger.LogInformation($"Enhanced federation stream closed async: {_session.SessionId}, " +
                        $"Duration: {duration}, Bytes: {_session.BytesStreamed}, " +
                        $"Quality: {_session.Quality?.Name ?? "Unknown"}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing enhanced federation stream async: {_session.SessionId}");
                }
                finally
                {
                    _disposed = true;
                }
            }
            
            await base.DisposeAsync();
        }
    }
}