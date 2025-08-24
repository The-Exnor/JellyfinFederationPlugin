using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Configuration;
using JellyfinFederationPlugin.Caching;
using MediaBrowser.Model.Dto;
using System.Net.Http;

namespace JellyfinFederationPlugin.Failover
{
    public class FederationFailoverService : IDisposable
    {
        private readonly ILogger<FederationFailoverService> _logger;
        private readonly FederationCacheService _cacheService;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, List<string>> _contentIndex; // itemId -> list of serverIds
        private readonly SemaphoreSlim _indexSemaphore;
        private readonly Timer _healthCheckTimer;
        private bool _disposed = false;

        // Failover configuration
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(2);
        private readonly int _maxConsecutiveFailures = 3;
        private readonly TimeSpan _serverBackoffTime = TimeSpan.FromMinutes(10);

        public FederationFailoverService(
            ILogger<FederationFailoverService> logger,
            FederationCacheService cacheService,
            HttpClient httpClient)
        {
            _logger = logger;
            _cacheService = cacheService;
            _httpClient = httpClient;
            _contentIndex = new Dictionary<string, List<string>>();
            _indexSemaphore = new SemaphoreSlim(1, 1);
            
            // Start health check timer
            _healthCheckTimer = new Timer(async _ => await PerformHealthChecks(), null,
                TimeSpan.FromMinutes(1), _healthCheckInterval);
                
            _logger.LogInformation("Federation failover service initialized");
        }

        #region Content Discovery and Indexing

        public async Task IndexContentFromServerAsync(string serverId, List<BaseItemDto> items)
        {
            await _indexSemaphore.WaitAsync();
            try
            {
                var addedCount = 0;
                var updatedCount = 0;

                foreach (var item in items)
                {
                    var contentKey = GenerateContentKey(item);
                    
                    if (!_contentIndex.ContainsKey(contentKey))
                    {
                        _contentIndex[contentKey] = new List<string>();
                        addedCount++;
                    }

                    if (!_contentIndex[contentKey].Contains(serverId))
                    {
                        _contentIndex[contentKey].Add(serverId);
                        updatedCount++;
                    }
                }

                _logger.LogInformation($"Indexed content from {serverId}: {addedCount} new items, {updatedCount} server mappings");
            }
            finally
            {
                _indexSemaphore.Release();
            }
        }

        public async Task<List<string>> GetAvailableServersForContentAsync(string itemId, string primaryServerId = null)
        {
            await _indexSemaphore.WaitAsync();
            try
            {
                // Try exact match first
                if (_contentIndex.TryGetValue(itemId, out var servers))
                {
                    return FilterHealthyServers(servers, primaryServerId);
                }

                // Try to find by content key pattern matching
                var matchingKeys = _contentIndex.Keys
                    .Where(key => key.Contains(itemId) || ContentSimilarity(key, itemId) > 0.8)
                    .ToList();

                var allServers = new HashSet<string>();
                foreach (var key in matchingKeys)
                {
                    foreach (var server in _contentIndex[key])
                    {
                        allServers.Add(server);
                    }
                }

                return FilterHealthyServers(allServers.ToList(), primaryServerId);
            }
            finally
            {
                _indexSemaphore.Release();
            }
        }

        public async Task<PluginConfiguration.FederatedServer> GetBestAvailableServerAsync(string itemId, string preferredServerId = null)
        {
            var config = Plugin.Instance.Configuration;
            var availableServerIds = await GetAvailableServersForContentAsync(itemId, preferredServerId);
            
            if (availableServerIds == null || !availableServerIds.Any())
            {
                _logger.LogWarning($"No available servers found for content: {itemId}");
                return null;
            }

            // Prefer the specified server if it's healthy
            if (!string.IsNullOrEmpty(preferredServerId) && availableServerIds.Contains(preferredServerId))
            {
                var preferredServer = config.FederatedServers?.FirstOrDefault(s => s.ServerUrl == preferredServerId);
                if (preferredServer != null && IsServerHealthy(preferredServerId))
                {
                    return preferredServer;
                }
            }

            // Find the best alternative server
            var healthyServers = availableServerIds
                .Select(serverId => new
                {
                    ServerId = serverId,
                    Server = config.FederatedServers?.FirstOrDefault(s => s.ServerUrl == serverId),
                    Health = _cacheService.GetServerHealth(serverId)
                })
                .Where(x => x.Server != null && IsServerHealthy(x.ServerId))
                .OrderBy(x => x.Health?.ResponseTime?.TotalMilliseconds ?? double.MaxValue)
                .ThenBy(x => x.Health?.ConsecutiveFailures ?? 0)
                .ToList();

            if (!healthyServers.Any())
            {
                _logger.LogWarning($"No healthy servers available for content: {itemId}");
                return null;
            }

            var bestServer = healthyServers.First();
            _logger.LogDebug($"Selected server {bestServer.ServerId} for content {itemId} (response time: {bestServer.Health?.ResponseTime?.TotalMilliseconds:F1}ms)");
            
            return bestServer.Server;
        }

        #endregion

        #region Health Monitoring

        private async Task PerformHealthChecks()
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                if (config.FederatedServers == null || !config.FederatedServers.Any())
                    return;

                var healthCheckTasks = config.FederatedServers.Select(async server =>
                {
                    try
                    {
                        await CheckServerHealth(server);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error checking health for server: {server.ServerUrl}");
                        _cacheService.SetServerHealth(server.ServerUrl, false, null, ex.Message);
                    }
                });

                await Task.WhenAll(healthCheckTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check cycle");
            }
        }

        private async Task CheckServerHealth(PluginConfiguration.FederatedServer server)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                var baseUrl = server.Port > 0 ? $"{server.ServerUrl}:{server.Port}" : server.ServerUrl;
                
                // Check basic connectivity first
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/System/Info/Public");
                request.Headers.Add("User-Agent", "Jellyfin-Federation-Plugin/1.0.0");
                
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                
                if (response.IsSuccessStatusCode)
                {
                    stopwatch.Stop();
                    _cacheService.SetServerHealth(server.ServerUrl, true, stopwatch.Elapsed);
                    _logger.LogDebug($"Server {server.ServerUrl} is healthy (response time: {stopwatch.ElapsedMilliseconds}ms)");
                }
                else
                {
                    stopwatch.Stop();
                    _cacheService.SetServerHealth(server.ServerUrl, false, stopwatch.Elapsed, $"HTTP {response.StatusCode}");
                    _logger.LogWarning($"Server {server.ServerUrl} returned unhealthy status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _cacheService.SetServerHealth(server.ServerUrl, false, stopwatch.Elapsed, ex.Message);
                _logger.LogWarning($"Server {server.ServerUrl} health check failed: {ex.Message}");
            }
        }

        public bool IsServerHealthy(string serverId)
        {
            var health = _cacheService.GetServerHealth(serverId);
            
            if (health == null)
            {
                // Unknown health, assume healthy for first attempt
                return true;
            }

            // Consider server unhealthy if it has too many consecutive failures
            if (health.ConsecutiveFailures >= _maxConsecutiveFailures)
            {
                // Check if enough time has passed for retry
                if (DateTime.UtcNow - health.LastCheck < _serverBackoffTime)
                {
                    return false;
                }
            }

            return health.IsHealthy;
        }

        public List<ServerHealthStatus> GetServerHealthStatuses()
        {
            var config = Plugin.Instance.Configuration;
            var healthStatuses = new List<ServerHealthStatus>();

            if (config.FederatedServers != null)
            {
                foreach (var server in config.FederatedServers)
                {
                    var health = _cacheService.GetServerHealth(server.ServerUrl);
                    var status = new ServerHealthStatus
                    {
                        ServerId = server.ServerUrl,
                        ServerUrl = server.ServerUrl,
                        Port = server.Port,
                        IsHealthy = IsServerHealthy(server.ServerUrl),
                        LastCheck = health?.LastCheck,
                        ResponseTime = health?.ResponseTime,
                        ConsecutiveFailures = health?.ConsecutiveFailures ?? 0,
                        ErrorMessage = health?.ErrorMessage,
                        ContentCount = GetContentCountForServer(server.ServerUrl)
                    };
                    
                    healthStatuses.Add(status);
                }
            }

            return healthStatuses;
        }

        #endregion

        #region Content Similarity and Matching

        private string GenerateContentKey(BaseItemDto item)
        {
            // Create a key that can help identify duplicate content across servers
            var key = $"{item.Name}_{item.ProductionYear}";
            
            if (item.Type.ToString().Equals("Movie", StringComparison.OrdinalIgnoreCase))
            {
                key += $"_{item.RunTimeTicks}";
            }
            else if (item.Type.ToString().Equals("Episode", StringComparison.OrdinalIgnoreCase))
            {
                key += $"_{item.ParentIndexNumber}_{item.IndexNumber}";
            }
            
            return key.ToLowerInvariant().Replace(" ", "_");
        }

        private double ContentSimilarity(string content1, string content2)
        {
            if (string.IsNullOrEmpty(content1) || string.IsNullOrEmpty(content2))
                return 0.0;

            var words1 = content1.Split('_').Where(w => !string.IsNullOrEmpty(w)).ToHashSet();
            var words2 = content2.Split('_').Where(w => !string.IsNullOrEmpty(w)).ToHashSet();

            if (!words1.Any() || !words2.Any())
                return 0.0;

            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();

            return (double)intersection / union;
        }

        private List<string> FilterHealthyServers(List<string> serverIds, string preferredServerId = null)
        {
            var healthyServers = serverIds
                .Where(IsServerHealthy)
                .ToList();

            // If preferred server is healthy, put it first
            if (!string.IsNullOrEmpty(preferredServerId) && healthyServers.Contains(preferredServerId))
            {
                healthyServers.Remove(preferredServerId);
                healthyServers.Insert(0, preferredServerId);
            }

            return healthyServers;
        }

        private int GetContentCountForServer(string serverId)
        {
            return _contentIndex.Values.Count(serverList => serverList.Contains(serverId));
        }

        #endregion

        #region Failover Operations

        public async Task<T> ExecuteWithFailoverAsync<T>(
            string itemId,
            Func<PluginConfiguration.FederatedServer, Task<T>> operation,
            string preferredServerId = null)
        {
            var availableServers = await GetAvailableServersForContentAsync(itemId, preferredServerId);
            var config = Plugin.Instance.Configuration;
            
            foreach (var serverId in availableServers)
            {
                var server = config.FederatedServers?.FirstOrDefault(s => s.ServerUrl == serverId);
                if (server == null)
                    continue;

                try
                {
                    _logger.LogDebug($"Attempting operation on server: {serverId} for item: {itemId}");
                    var result = await operation(server);
                    
                    // Mark server as healthy on successful operation
                    _cacheService.SetServerHealth(serverId, true);
                    
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Operation failed on server {serverId}, trying next server");
                    
                    // Mark server as unhealthy
                    _cacheService.SetServerHealth(serverId, false, null, ex.Message);
                    
                    // Continue to next server
                    continue;
                }
            }

            throw new InvalidOperationException($"All available servers failed for item: {itemId}");
        }

        public async Task RemoveServerFromIndexAsync(string serverId)
        {
            await _indexSemaphore.WaitAsync();
            try
            {
                var keysToRemove = new List<string>();
                
                foreach (var kvp in _contentIndex)
                {
                    kvp.Value.Remove(serverId);
                    
                    // Remove content entries that no longer have any servers
                    if (!kvp.Value.Any())
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _contentIndex.Remove(key);
                }

                _logger.LogInformation($"Removed server {serverId} from content index");
            }
            finally
            {
                _indexSemaphore.Release();
            }
        }

        #endregion

        public FailoverStatistics GetFailoverStatistics()
        {
            var config = Plugin.Instance.Configuration;
            var totalServers = config.FederatedServers?.Count ?? 0;
            var healthyServers = config.FederatedServers?.Count(s => IsServerHealthy(s.ServerUrl)) ?? 0;
            var totalContentEntries = _contentIndex.Count;
            var redundantContent = _contentIndex.Values.Count(serverList => serverList.Count > 1);

            return new FailoverStatistics
            {
                TotalServers = totalServers,
                HealthyServers = healthyServers,
                UnhealthyServers = totalServers - healthyServers,
                TotalContentEntries = totalContentEntries,
                RedundantContentEntries = redundantContent,
                RedundancyPercentage = totalContentEntries > 0 ? (double)redundantContent / totalContentEntries * 100.0 : 0.0
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _healthCheckTimer?.Dispose();
                _indexSemaphore?.Dispose();
                _disposed = true;
                _logger.LogInformation("Federation failover service disposed");
            }
        }
    }

    public class ServerHealthStatus
    {
        public string ServerId { get; set; }
        public string ServerUrl { get; set; }
        public int Port { get; set; }
        public bool IsHealthy { get; set; }
        public DateTime? LastCheck { get; set; }
        public TimeSpan? ResponseTime { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string ErrorMessage { get; set; }
        public int ContentCount { get; set; }
    }

    public class FailoverStatistics
    {
        public int TotalServers { get; set; }
        public int HealthyServers { get; set; }
        public int UnhealthyServers { get; set; }
        public int TotalContentEntries { get; set; }
        public int RedundantContentEntries { get; set; }
        public double RedundancyPercentage { get; set; }
    }
}