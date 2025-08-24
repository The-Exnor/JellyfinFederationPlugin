using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Configuration;

namespace JellyfinFederationPlugin.Bandwidth
{
    public class FederationBandwidthManager : IDisposable
    {
        private readonly ILogger<FederationBandwidthManager> _logger;
        private readonly ConcurrentDictionary<string, BandwidthSession> _activeSessions;
        private readonly ConcurrentDictionary<string, ServerBandwidthInfo> _serverBandwidth;
        private readonly ConcurrentDictionary<string, ClientBandwidthInfo> _clientBandwidth;
        private readonly Timer _monitoringTimer;
        private readonly SemaphoreSlim _managementSemaphore;
        private bool _disposed = false;

        // Configuration
        private readonly TimeSpan _monitoringInterval = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _bandwidthWindowSize = TimeSpan.FromMinutes(5);
        private readonly long _defaultMaxBandwidthPerServer = 100 * 1024 * 1024; // 100 Mbps
        private readonly long _defaultMaxBandwidthPerClient = 50 * 1024 * 1024;  // 50 Mbps
        private readonly int _maxConcurrentStreamsPerServer = 10;
        private readonly int _maxConcurrentStreamsPerClient = 3;

        public FederationBandwidthManager(ILogger<FederationBandwidthManager> logger)
        {
            _logger = logger;
            _activeSessions = new ConcurrentDictionary<string, BandwidthSession>();
            _serverBandwidth = new ConcurrentDictionary<string, ServerBandwidthInfo>();
            _clientBandwidth = new ConcurrentDictionary<string, ClientBandwidthInfo>();
            _managementSemaphore = new SemaphoreSlim(1, 1);
            
            // Start monitoring timer
            _monitoringTimer = new Timer(async _ => await MonitorBandwidthUsage(), null,
                TimeSpan.FromSeconds(5), _monitoringInterval);
                
            _logger.LogInformation("Federation bandwidth manager initialized");
        }

        #region Session Management

        public async Task<BandwidthSession> CreateSessionAsync(
            string sessionId,
            string serverId,
            string clientId,
            string itemId,
            StreamQuality requestedQuality)
        {
            await _managementSemaphore.WaitAsync();
            try
            {
                // Check if we can accommodate this session
                var adaptedQuality = await DetermineOptimalQuality(serverId, clientId, requestedQuality);
                
                if (adaptedQuality == null)
                {
                    _logger.LogWarning($"Cannot accommodate new session: server {serverId}, client {clientId}");
                    return null;
                }

                var session = new BandwidthSession
                {
                    SessionId = sessionId,
                    ServerId = serverId,
                    ClientId = clientId,
                    ItemId = itemId,
                    RequestedQuality = requestedQuality,
                    AdaptedQuality = adaptedQuality,
                    StartTime = DateTime.UtcNow,
                    LastUpdate = DateTime.UtcNow,
                    IsActive = true
                };

                _activeSessions[sessionId] = session;
                
                // Update server and client tracking
                UpdateServerSessionCount(serverId, 1);
                UpdateClientSessionCount(clientId, 1);
                
                _logger.LogInformation($"Created bandwidth session: {sessionId} (Quality: {adaptedQuality.Name})");
                return session;
            }
            finally
            {
                _managementSemaphore.Release();
            }
        }

        public async Task UpdateSessionBandwidthAsync(string sessionId, long bytesTransferred)
        {
            if (_activeSessions.TryGetValue(sessionId, out var session))
            {
                var now = DateTime.UtcNow;
                session.TotalBytesTransferred += bytesTransferred;
                session.LastUpdate = now;
                
                // Update bandwidth tracking
                UpdateServerBandwidth(session.ServerId, bytesTransferred, now);
                UpdateClientBandwidth(session.ClientId, bytesTransferred, now);
                
                // Calculate current bandwidth for adaptive streaming
                var currentBandwidth = CalculateCurrentBandwidth(session);
                session.CurrentBandwidth = currentBandwidth;
                
                // Check if we need to adapt quality
                await CheckQualityAdaptation(session);
            }
        }

        public async Task CloseSessionAsync(string sessionId)
        {
            if (_activeSessions.TryRemove(sessionId, out var session))
            {
                session.IsActive = false;
                session.EndTime = DateTime.UtcNow;
                
                // Update session counts
                UpdateServerSessionCount(session.ServerId, -1);
                UpdateClientSessionCount(session.ClientId, -1);
                
                var duration = session.EndTime.Value - session.StartTime;
                var avgBandwidth = duration.TotalSeconds > 0 
                    ? session.TotalBytesTransferred / duration.TotalSeconds 
                    : 0;
                    
                _logger.LogInformation($"Closed bandwidth session: {sessionId} " +
                    $"(Duration: {duration:g}, Avg bandwidth: {FormatBandwidth((long)avgBandwidth)})");
            }
        }

        #endregion

        #region Quality Adaptation

        private async Task<StreamQuality> DetermineOptimalQuality(
            string serverId,
            string clientId,
            StreamQuality requestedQuality)
        {
            var serverInfo = GetOrCreateServerInfo(serverId);
            var clientInfo = GetOrCreateClientInfo(clientId);
            
            // Check concurrent session limits
            if (serverInfo.ActiveSessions >= _maxConcurrentStreamsPerServer)
            {
                _logger.LogWarning($"Server {serverId} has reached maximum concurrent streams");
                return null;
            }
            
            if (clientInfo.ActiveSessions >= _maxConcurrentStreamsPerClient)
            {
                _logger.LogWarning($"Client {clientId} has reached maximum concurrent streams");
                return null;
            }
            
            // Check available bandwidth
            var serverAvailableBandwidth = serverInfo.MaxBandwidth - serverInfo.CurrentBandwidth;
            var clientAvailableBandwidth = clientInfo.MaxBandwidth - clientInfo.CurrentBandwidth;
            var availableBandwidth = Math.Min(serverAvailableBandwidth, clientAvailableBandwidth);
            
            // Find the best quality that fits within available bandwidth
            var availableQualities = GetAvailableQualities()
                .Where(q => q.RequiredBandwidth <= availableBandwidth)
                .OrderByDescending(q => q.RequiredBandwidth)
                .ToList();
            
            if (!availableQualities.Any())
            {
                _logger.LogWarning($"No quality options fit within available bandwidth: {FormatBandwidth(availableBandwidth)}");
                return GetLowestQuality(); // Emergency fallback
            }
            
            // Prefer the requested quality if available
            var selectedQuality = availableQualities.FirstOrDefault(q => q.Name == requestedQuality.Name) 
                                 ?? availableQualities.First();
            
            _logger.LogDebug($"Selected quality {selectedQuality.Name} for session " +
                $"(Available bandwidth: {FormatBandwidth(availableBandwidth)})");
            
            return selectedQuality;
        }

        private async Task CheckQualityAdaptation(BandwidthSession session)
        {
            if (DateTime.UtcNow - session.LastAdaptation < TimeSpan.FromSeconds(30))
                return; // Don't adapt too frequently
            
            var serverInfo = GetOrCreateServerInfo(session.ServerId);
            var clientInfo = GetOrCreateClientInfo(session.ClientId);
            
            // Calculate available bandwidth
            var serverLoad = (double)serverInfo.CurrentBandwidth / serverInfo.MaxBandwidth;
            var clientLoad = (double)clientInfo.CurrentBandwidth / clientInfo.MaxBandwidth;
            var maxLoad = Math.Max(serverLoad, clientLoad);
            
            // Determine if we need to scale up or down
            if (maxLoad > 0.85) // High load, scale down
            {
                var lowerQuality = GetLowerQuality(session.AdaptedQuality);
                if (lowerQuality != null && lowerQuality.Name != session.AdaptedQuality.Name)
                {
                    _logger.LogInformation($"Adapting session {session.SessionId} down to {lowerQuality.Name} " +
                        $"(Load: {maxLoad:P1})");
                    session.AdaptedQuality = lowerQuality;
                    session.LastAdaptation = DateTime.UtcNow;
                }
            }
            else if (maxLoad < 0.5) // Low load, scale up if possible
            {
                var higherQuality = GetHigherQuality(session.AdaptedQuality);
                if (higherQuality != null && 
                    higherQuality.Name != session.AdaptedQuality.Name &&
                    higherQuality.RequiredBandwidth <= (serverInfo.MaxBandwidth - serverInfo.CurrentBandwidth))
                {
                    _logger.LogInformation($"Adapting session {session.SessionId} up to {higherQuality.Name} " +
                        $"(Load: {maxLoad:P1})");
                    session.AdaptedQuality = higherQuality;
                    session.LastAdaptation = DateTime.UtcNow;
                }
            }
        }

        #endregion

        #region Bandwidth Monitoring

        private async Task MonitorBandwidthUsage()
        {
            try
            {
                var now = DateTime.UtcNow;
                
                // Update current bandwidth for all servers and clients
                foreach (var serverInfo in _serverBandwidth.Values)
                {
                    UpdateCurrentBandwidth(serverInfo.BandwidthHistory, now);
                    serverInfo.CurrentBandwidth = CalculateBandwidthInWindow(serverInfo.BandwidthHistory, now);
                }
                
                foreach (var clientInfo in _clientBandwidth.Values)
                {
                    UpdateCurrentBandwidth(clientInfo.BandwidthHistory, now);
                    clientInfo.CurrentBandwidth = CalculateBandwidthInWindow(clientInfo.BandwidthHistory, now);
                }
                
                // Log bandwidth statistics periodically
                if (now.Minute % 5 == 0 && now.Second < 10)
                {
                    LogBandwidthStatistics();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during bandwidth monitoring");
            }
        }

        private void UpdateServerBandwidth(string serverId, long bytes, DateTime timestamp)
        {
            var serverInfo = GetOrCreateServerInfo(serverId);
            lock (serverInfo.BandwidthHistory)
            {
                serverInfo.BandwidthHistory.Add(new BandwidthDataPoint
                {
                    Timestamp = timestamp,
                    Bytes = bytes
                });
                
                // Remove old data points
                var cutoff = timestamp.Subtract(_bandwidthWindowSize);
                serverInfo.BandwidthHistory.RemoveAll(dp => dp.Timestamp < cutoff);
            }
        }

        private void UpdateClientBandwidth(string clientId, long bytes, DateTime timestamp)
        {
            var clientInfo = GetOrCreateClientInfo(clientId);
            lock (clientInfo.BandwidthHistory)
            {
                clientInfo.BandwidthHistory.Add(new BandwidthDataPoint
                {
                    Timestamp = timestamp,
                    Bytes = bytes
                });
                
                // Remove old data points
                var cutoff = timestamp.Subtract(_bandwidthWindowSize);
                clientInfo.BandwidthHistory.RemoveAll(dp => dp.Timestamp < cutoff);
            }
        }

        private long CalculateBandwidthInWindow(List<BandwidthDataPoint> history, DateTime now)
        {
            var cutoff = now.Subtract(_bandwidthWindowSize);
            var relevantData = history.Where(dp => dp.Timestamp >= cutoff).ToList();
            
            if (!relevantData.Any())
                return 0;
            
            var totalBytes = relevantData.Sum(dp => dp.Bytes);
            var timeSpan = _bandwidthWindowSize.TotalSeconds;
            
            return (long)(totalBytes / timeSpan); // Bytes per second
        }

        private long CalculateCurrentBandwidth(BandwidthSession session)
        {
            var duration = (DateTime.UtcNow - session.StartTime).TotalSeconds;
            return duration > 0 ? (long)(session.TotalBytesTransferred / duration) : 0;
        }

        #endregion

        #region Quality Management

        private List<StreamQuality> GetAvailableQualities()
        {
            return new List<StreamQuality>
            {
                new StreamQuality { Name = "4K", Width = 3840, Height = 2160, Bitrate = 25000000, RequiredBandwidth = 30000000 },
                new StreamQuality { Name = "1080p60", Width = 1920, Height = 1080, Bitrate = 15000000, RequiredBandwidth = 18000000 },
                new StreamQuality { Name = "1080p", Width = 1920, Height = 1080, Bitrate = 8000000, RequiredBandwidth = 10000000 },
                new StreamQuality { Name = "720p60", Width = 1280, Height = 720, Bitrate = 6000000, RequiredBandwidth = 7500000 },
                new StreamQuality { Name = "720p", Width = 1280, Height = 720, Bitrate = 4000000, RequiredBandwidth = 5000000 },
                new StreamQuality { Name = "480p", Width = 854, Height = 480, Bitrate = 2000000, RequiredBandwidth = 2500000 },
                new StreamQuality { Name = "360p", Width = 640, Height = 360, Bitrate = 1000000, RequiredBandwidth = 1200000 },
                new StreamQuality { Name = "240p", Width = 426, Height = 240, Bitrate = 500000, RequiredBandwidth = 600000 }
            };
        }

        private StreamQuality GetLowestQuality()
        {
            return GetAvailableQualities().OrderBy(q => q.RequiredBandwidth).First();
        }

        private StreamQuality GetLowerQuality(StreamQuality currentQuality)
        {
            var qualities = GetAvailableQualities().OrderBy(q => q.RequiredBandwidth).ToList();
            var currentIndex = qualities.FindIndex(q => q.Name == currentQuality.Name);
            
            return currentIndex > 0 ? qualities[currentIndex - 1] : null;
        }

        private StreamQuality GetHigherQuality(StreamQuality currentQuality)
        {
            var qualities = GetAvailableQualities().OrderBy(q => q.RequiredBandwidth).ToList();
            var currentIndex = qualities.FindIndex(q => q.Name == currentQuality.Name);
            
            return currentIndex < qualities.Count - 1 ? qualities[currentIndex + 1] : null;
        }

        #endregion

        #region Helper Methods

        private ServerBandwidthInfo GetOrCreateServerInfo(string serverId)
        {
            return _serverBandwidth.GetOrAdd(serverId, _ => new ServerBandwidthInfo
            {
                ServerId = serverId,
                MaxBandwidth = _defaultMaxBandwidthPerServer,
                BandwidthHistory = new List<BandwidthDataPoint>()
            });
        }

        private ClientBandwidthInfo GetOrCreateClientInfo(string clientId)
        {
            return _clientBandwidth.GetOrAdd(clientId, _ => new ClientBandwidthInfo
            {
                ClientId = clientId,
                MaxBandwidth = _defaultMaxBandwidthPerClient,
                BandwidthHistory = new List<BandwidthDataPoint>()
            });
        }

        private void UpdateServerSessionCount(string serverId, int delta)
        {
            var serverInfo = GetOrCreateServerInfo(serverId);
            serverInfo.ActiveSessions = Math.Max(0, serverInfo.ActiveSessions + delta);
        }

        private void UpdateClientSessionCount(string clientId, int delta)
        {
            var clientInfo = GetOrCreateClientInfo(clientId);
            clientInfo.ActiveSessions = Math.Max(0, clientInfo.ActiveSessions + delta);
        }

        private void UpdateCurrentBandwidth(List<BandwidthDataPoint> history, DateTime now)
        {
            lock (history)
            {
                var cutoff = now.Subtract(_bandwidthWindowSize);
                history.RemoveAll(dp => dp.Timestamp < cutoff);
            }
        }

        private string FormatBandwidth(long bytesPerSecond)
        {
            var bitsPerSecond = bytesPerSecond * 8;
            
            if (bitsPerSecond >= 1_000_000_000)
                return $"{bitsPerSecond / 1_000_000_000.0:F1} Gbps";
            if (bitsPerSecond >= 1_000_000)
                return $"{bitsPerSecond / 1_000_000.0:F1} Mbps";
            if (bitsPerSecond >= 1_000)
                return $"{bitsPerSecond / 1_000.0:F1} Kbps";
            
            return $"{bitsPerSecond} bps";
        }

        private void LogBandwidthStatistics()
        {
            var totalActiveSessions = _activeSessions.Count;
            var totalServerBandwidth = _serverBandwidth.Values.Sum(s => s.CurrentBandwidth);
            var totalClientBandwidth = _clientBandwidth.Values.Sum(c => c.CurrentBandwidth);
            
            _logger.LogInformation($"Bandwidth stats: {totalActiveSessions} active sessions, " +
                $"Server: {FormatBandwidth(totalServerBandwidth)}, Client: {FormatBandwidth(totalClientBandwidth)}");
        }

        #endregion

        #region Public API

        public List<BandwidthSession> GetActiveSessions()
        {
            return _activeSessions.Values.Where(s => s.IsActive).ToList();
        }

        public BandwidthStatistics GetBandwidthStatistics()
        {
            var activeSessions = _activeSessions.Values.Where(s => s.IsActive).ToList();
            
            return new BandwidthStatistics
            {
                ActiveSessions = activeSessions.Count,
                TotalBandwidthUsage = _serverBandwidth.Values.Sum(s => s.CurrentBandwidth),
                ServerCount = _serverBandwidth.Count,
                ClientCount = _clientBandwidth.Count,
                AverageSessionBandwidth = activeSessions.Any() 
                    ? activeSessions.Average(s => s.CurrentBandwidth) 
                    : 0,
                QualityDistribution = activeSessions
                    .GroupBy(s => s.AdaptedQuality.Name)
                    .ToDictionary(g => g.Key, g => g.Count())
            };
        }

        public List<ServerBandwidthStatus> GetServerBandwidthStatus()
        {
            return _serverBandwidth.Values.Select(s => new ServerBandwidthStatus
            {
                ServerId = s.ServerId,
                CurrentBandwidth = s.CurrentBandwidth,
                MaxBandwidth = s.MaxBandwidth,
                ActiveSessions = s.ActiveSessions,
                UtilizationPercentage = (double)s.CurrentBandwidth / s.MaxBandwidth * 100.0
            }).ToList();
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _monitoringTimer?.Dispose();
                _managementSemaphore?.Dispose();
                _disposed = true;
                _logger.LogInformation("Federation bandwidth manager disposed");
            }
        }
    }

    #region Data Classes

    public class BandwidthSession
    {
        public string SessionId { get; set; }
        public string ServerId { get; set; }
        public string ClientId { get; set; }
        public string ItemId { get; set; }
        public StreamQuality RequestedQuality { get; set; }
        public StreamQuality AdaptedQuality { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime LastUpdate { get; set; }
        public DateTime LastAdaptation { get; set; }
        public long TotalBytesTransferred { get; set; }
        public long CurrentBandwidth { get; set; }
        public bool IsActive { get; set; }
    }

    public class StreamQuality
    {
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bitrate { get; set; }
        public long RequiredBandwidth { get; set; }
    }

    public class ServerBandwidthInfo
    {
        public string ServerId { get; set; }
        public long MaxBandwidth { get; set; }
        public long CurrentBandwidth { get; set; }
        public int ActiveSessions { get; set; }
        public List<BandwidthDataPoint> BandwidthHistory { get; set; }
    }

    public class ClientBandwidthInfo
    {
        public string ClientId { get; set; }
        public long MaxBandwidth { get; set; }
        public long CurrentBandwidth { get; set; }
        public int ActiveSessions { get; set; }
        public List<BandwidthDataPoint> BandwidthHistory { get; set; }
    }

    public class BandwidthDataPoint
    {
        public DateTime Timestamp { get; set; }
        public long Bytes { get; set; }
    }

    public class BandwidthStatistics
    {
        public int ActiveSessions { get; set; }
        public long TotalBandwidthUsage { get; set; }
        public int ServerCount { get; set; }
        public int ClientCount { get; set; }
        public double AverageSessionBandwidth { get; set; }
        public Dictionary<string, int> QualityDistribution { get; set; }
    }

    public class ServerBandwidthStatus
    {
        public string ServerId { get; set; }
        public long CurrentBandwidth { get; set; }
        public long MaxBandwidth { get; set; }
        public int ActiveSessions { get; set; }
        public double UtilizationPercentage { get; set; }
    }

    #endregion
}