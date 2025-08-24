using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Streaming;

namespace JellyfinFederationPlugin.Streaming
{
    public class FederationProxyStream : Stream
    {
        private readonly HttpClient _httpClient;
        private readonly string _remoteUrl;
        private readonly FederationStreamingService.StreamSession _session;
        private readonly ILogger _logger;
        private readonly Action _onDispose;
        
        private HttpResponseMessage _response;
        private Stream _remoteStream;
        private long _position;
        private bool _disposed = false;

        public FederationProxyStream(
            HttpClient httpClient,
            string remoteUrl,
            FederationStreamingService.StreamSession session,
            ILogger logger,
            Action onDispose)
        {
            _httpClient = httpClient;
            _remoteUrl = remoteUrl;
            _session = session;
            _logger = logger;
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
                    _logger.LogDebug($"Opening remote stream: {_remoteUrl}");
                    
                    var request = new HttpRequestMessage(HttpMethod.Get, _remoteUrl);
                    
                    // Add range header for seeking if position > 0
                    if (_position > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(_position, null);
                    }

                    _response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    _response.EnsureSuccessStatusCode();
                    
                    _remoteStream = await _response.Content.ReadAsStreamAsync();
                    
                    _logger.LogDebug($"Remote stream opened successfully for session: {_session.SessionId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to open remote stream: {_remoteUrl}");
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
                }
                
                return bytesRead;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error reading from federation stream: {_session.SessionId}");
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
                    _logger.LogDebug($"Seeking federation stream to position: {newPosition}");
                    
                    // Close current stream and reopen with range header
                    CloseRemoteStream();
                    _position = newPosition;
                    
                    // The stream will be reopened with the correct position on next read
                }

                return _position;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error seeking federation stream: {_session.SessionId}");
                throw;
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
                _logger.LogWarning(ex, "Error closing remote stream");
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
                    _logger.LogDebug($"Disposing federation stream: {_session.SessionId}");
                    
                    CloseRemoteStream();
                    _onDispose?.Invoke();
                    
                    var duration = DateTime.UtcNow - _session.StartTime;
                    _logger.LogInformation($"Federation stream closed: {_session.SessionId}, Duration: {duration}, Bytes: {_session.BytesStreamed}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing federation stream: {_session.SessionId}");
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
                    _logger.LogDebug($"Disposing federation stream async: {_session.SessionId}");
                    
                    CloseRemoteStream();
                    _onDispose?.Invoke();
                    
                    var duration = DateTime.UtcNow - _session.StartTime;
                    _logger.LogInformation($"Federation stream closed async: {_session.SessionId}, Duration: {duration}, Bytes: {_session.BytesStreamed}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing federation stream async: {_session.SessionId}");
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