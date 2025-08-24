using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Net;
using JellyfinFederationPlugin.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Services;
using JellyfinFederationPlugin.Library;
using JellyfinFederationPlugin.Streaming;
using JellyfinFederationPlugin.Caching;
using JellyfinFederationPlugin.Failover;
using JellyfinFederationPlugin.Bandwidth;
using System.Linq;

namespace JellyfinFederationPlugin.Api
{
    [ApiController]
    [Authorize(Policy = "RequiresElevation")]
    [Route("Plugins/JellyfinFederationPlugin")]
    public class FederationPluginController : ControllerBase
    {
        private readonly ILogger<FederationPluginController> _logger;
        private readonly FederationSyncService _syncService;
        private readonly FederationLibraryService _libraryService;
        private readonly EnhancedFederationStreamingService _streamingService;
        private readonly FederationCacheService _cacheService;
        private readonly FederationFailoverService _failoverService;
        private readonly FederationBandwidthManager _bandwidthManager;

        public FederationPluginController(
            ILogger<FederationPluginController> logger,
            FederationSyncService syncService,
            FederationLibraryService libraryService,
            EnhancedFederationStreamingService streamingService,
            FederationCacheService cacheService,
            FederationFailoverService failoverService,
            FederationBandwidthManager bandwidthManager)
        {
            _logger = logger;
            _syncService = syncService;
            _libraryService = libraryService;
            _streamingService = streamingService;
            _cacheService = cacheService;
            _failoverService = failoverService;
            _bandwidthManager = bandwidthManager;
        }

        #region Configuration Management

        [HttpGet("GetConfiguration")]
        public ActionResult<PluginConfiguration> GetConfiguration()
        {
            return Plugin.Instance.Configuration;
        }

        [HttpPost("SaveConfiguration")]
        public IActionResult SaveConfiguration([FromBody] PluginConfiguration config)
        {
            Plugin.Instance.UpdateConfiguration(config);
            _logger.LogDebug("Configuration saved successfully.");
            return Ok();
        }

        #endregion

        #region Library Operations

        [HttpPost("TriggerSync")]
        public async Task<IActionResult> TriggerSync()
        {
            try
            {
                _logger.LogInformation("Manual federation sync triggered via API");
                
                if (Plugin.Instance != null)
                {
                    var success = await Plugin.Instance.TriggerLibrarySyncAsync();
                    if (success)
                    {
                        return Ok(new { message = "Federation sync started successfully" });
                    }
                    else
                    {
                        return StatusCode(500, new { error = "Failed to start sync - plugin not initialized" });
                    }
                }
                
                // Fallback to direct service call
                await _syncService.TriggerManualSync();
                return Ok(new { message = "Federation sync started successfully" });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error triggering federation sync");
                return StatusCode(500, new { error = "Failed to trigger sync", details = ex.Message });
            }
        }

        [HttpPost("TestServer")]
        public async Task<IActionResult> TestServer([FromBody] PluginConfiguration.FederatedServer server)
        {
            try
            {
                if (server == null || string.IsNullOrWhiteSpace(server.ServerUrl))
                {
                    return BadRequest(new { error = "Invalid server configuration" });
                }

                _logger.LogInformation($"Testing server connection: {server.ServerUrl}");
                var isValid = await _libraryService.TestFederatedServerAsync(server);
                
                return Ok(new 
                { 
                    success = isValid,
                    message = isValid ? "Server connection successful" : "Server connection failed",
                    serverUrl = server.ServerUrl
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"Error testing server {server?.ServerUrl}");
                return StatusCode(500, new { error = "Failed to test server", details = ex.Message });
            }
        }

        #endregion

        #region Status and Monitoring

        [HttpGet("Status")]
        public async Task<IActionResult> GetStatus()
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                var serverCount = config.FederatedServers?.Count ?? 0;
                
                // Get comprehensive status information
                var cacheStats = _cacheService?.GetCacheStatistics();
                var failoverStats = _failoverService?.GetFailoverStatistics();
                var bandwidthStats = _bandwidthManager?.GetBandwidthStatistics();
                var serverHealthStatuses = _failoverService?.GetServerHealthStatuses();
                
                return Ok(new
                {
                    pluginVersion = "2.0.0",
                    isInitialized = Plugin.Instance?.IsInitialized ?? false,
                    configuredServers = serverCount,
                    activeStreamSessions = bandwidthStats?.ActiveSessions ?? 0,
                    
                    // Cache information
                    caching = new
                    {
                        enabled = config.Caching.EnableMetadataCache || config.Caching.EnableThumbnailCache || config.Caching.EnableLibraryCache,
                        metadataEntries = cacheStats?.MetadataEntries ?? 0,
                        thumbnailEntries = cacheStats?.ThumbnailEntries ?? 0,
                        libraryEntries = cacheStats?.LibraryEntries ?? 0,
                        totalMemoryUsage = cacheStats?.TotalMemoryUsage ?? 0
                    },
                    
                    // Failover information
                    failover = new
                    {
                        enabled = config.Failover.EnableFailover,
                        healthyServers = failoverStats?.HealthyServers ?? 0,
                        unhealthyServers = failoverStats?.UnhealthyServers ?? 0,
                        redundantContent = failoverStats?.RedundantContentEntries ?? 0,
                        redundancyPercentage = failoverStats?.RedundancyPercentage ?? 0.0
                    },
                    
                    // Bandwidth information
                    bandwidth = new
                    {
                        enabled = config.Bandwidth.EnableBandwidthManagement,
                        totalUsage = FormatBandwidth(bandwidthStats?.TotalBandwidthUsage ?? 0),
                        averageSessionBandwidth = FormatBandwidth((long)(bandwidthStats?.AverageSessionBandwidth ?? 0)),
                        qualityDistribution = bandwidthStats?.QualityDistribution ?? new System.Collections.Generic.Dictionary<string, int>()
                    },
                    
                    servers = config.FederatedServers?.Select(s => new
                    {
                        serverUrl = s.ServerUrl,
                        port = s.Port,
                        hasApiKey = !string.IsNullOrWhiteSpace(s.ApiKey),
                        priority = s.Priority,
                        maxBandwidth = FormatBandwidth(s.MaxBandwidth),
                        health = serverHealthStatuses?.FirstOrDefault(h => h.ServerId == s.ServerUrl)
                    }).ToArray() ?? new object[0]
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting plugin status");
                return StatusCode(500, new { error = "Failed to get status", details = ex.Message });
            }
        }

        #endregion

        #region Caching Management

        [HttpGet("Cache/Statistics")]
        public IActionResult GetCacheStatistics()
        {
            try
            {
                var stats = _cacheService?.GetCacheStatistics();
                return Ok(stats);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting cache statistics");
                return StatusCode(500, new { error = "Failed to get cache statistics", details = ex.Message });
            }
        }

        [HttpPost("Cache/Clear")]
        public async Task<IActionResult> ClearCache([FromQuery] string cacheType = "all")
        {
            try
            {
                switch (cacheType.ToLowerInvariant())
                {
                    case "all":
                        await _cacheService.ClearAllCachesAsync();
                        break;
                    case "metadata":
                        // Implementation would go here
                        break;
                    case "thumbnails":
                        // Implementation would go here
                        break;
                    case "library":
                        // Implementation would go here
                        break;
                    default:
                        return BadRequest(new { error = "Invalid cache type. Use: all, metadata, thumbnails, or library" });
                }
                
                return Ok(new { message = $"Cache cleared: {cacheType}" });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"Error clearing cache: {cacheType}");
                return StatusCode(500, new { error = "Failed to clear cache", details = ex.Message });
            }
        }

        #endregion

        #region Failover Management

        [HttpGet("Failover/Status")]
        public IActionResult GetFailoverStatus()
        {
            try
            {
                var stats = _failoverService?.GetFailoverStatistics();
                var serverHealth = _failoverService?.GetServerHealthStatuses();
                
                return Ok(new
                {
                    statistics = stats,
                    serverHealth = serverHealth
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting failover status");
                return StatusCode(500, new { error = "Failed to get failover status", details = ex.Message });
            }
        }

        #endregion

        #region Bandwidth Management

        [HttpGet("Bandwidth/Statistics")]
        public IActionResult GetBandwidthStatistics()
        {
            try
            {
                var stats = _bandwidthManager?.GetBandwidthStatistics();
                var serverStatus = _bandwidthManager?.GetServerBandwidthStatus();
                
                return Ok(new
                {
                    statistics = stats,
                    serverStatus = serverStatus
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting bandwidth statistics");
                return StatusCode(500, new { error = "Failed to get bandwidth statistics", details = ex.Message });
            }
        }

        [HttpGet("Bandwidth/Sessions")]
        public IActionResult GetBandwidthSessions()
        {
            try
            {
                var sessions = _bandwidthManager?.GetActiveSessions();
                return Ok(new { sessions = sessions });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting bandwidth sessions");
                return StatusCode(500, new { error = "Failed to get bandwidth sessions", details = ex.Message });
            }
        }

        #endregion

        #region Streaming Management

        [HttpGet("StreamingSessions")]
        public async Task<IActionResult> GetStreamingSessions()
        {
            try
            {
                if (_streamingService == null)
                {
                    return Ok(new { sessions = new object[0] });
                }

                var sessions = await _streamingService.GetActiveSessionsAsync();
                var sessionInfo = sessions.Select(s => new
                {
                    sessionId = s.SessionId,
                    mediaSourceId = s.MediaSourceId,
                    serverId = s.ServerId,
                    itemId = s.ItemId,
                    startTime = s.StartTime,
                    duration = System.DateTime.UtcNow - s.StartTime,
                    isTranscoding = s.IsTranscoding,
                    transcodeContainer = s.TranscodeContainer,
                    bytesStreamed = s.BytesStreamed,
                    quality = s.Quality?.Name ?? "Unknown",
                    currentBandwidth = FormatBandwidth(s.BandwidthSession?.CurrentBandwidth ?? 0)
                }).ToArray();

                return Ok(new { sessions = sessionInfo });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting streaming sessions");
                return StatusCode(500, new { error = "Failed to get streaming sessions", details = ex.Message });
            }
        }

        [HttpPost("StopSession/{sessionId}")]
        public async Task<IActionResult> StopStreamingSession(string sessionId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return BadRequest(new { error = "Session ID is required" });
                }

                if (_streamingService == null)
                {
                    return NotFound(new { error = "Streaming service not available" });
                }

                await _streamingService.StopSessionAsync(sessionId);
                return Ok(new { message = "Session stopped successfully" });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, $"Error stopping streaming session: {sessionId}");
                return StatusCode(500, new { error = "Failed to stop session", details = ex.Message });
            }
        }

        #endregion

        #region Helper Methods

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

        #endregion
    }
}