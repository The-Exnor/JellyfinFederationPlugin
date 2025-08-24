using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using System.Text.Json;

namespace JellyfinFederationPlugin.Caching
{
    public class FederationCacheService : IDisposable
    {
        private readonly ILogger<FederationCacheService> _logger;
        private readonly ConcurrentDictionary<string, CacheEntry<BaseItemDto>> _metadataCache;
        private readonly ConcurrentDictionary<string, CacheEntry<byte[]>> _thumbnailCache;
        private readonly ConcurrentDictionary<string, CacheEntry<List<BaseItemDto>>> _libraryCache;
        private readonly ConcurrentDictionary<string, ServerHealthInfo> _serverHealthCache;
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _cacheSemaphore;
        private readonly string _cacheDirectory;
        private bool _disposed = false;

        // Cache configuration
        private readonly TimeSpan _metadataCacheTtl = TimeSpan.FromHours(6);
        private readonly TimeSpan _thumbnailCacheTtl = TimeSpan.FromDays(7);
        private readonly TimeSpan _libraryCacheTtl = TimeSpan.FromMinutes(30);
        private readonly TimeSpan _serverHealthCacheTtl = TimeSpan.FromMinutes(5);
        private readonly int _maxThumbnailCacheSize = 1000; // Max thumbnail entries
        private readonly long _maxThumbnailFileSize = 5 * 1024 * 1024; // 5MB per thumbnail

        public FederationCacheService(ILogger<FederationCacheService> logger)
        {
            _logger = logger;
            _metadataCache = new ConcurrentDictionary<string, CacheEntry<BaseItemDto>>();
            _thumbnailCache = new ConcurrentDictionary<string, CacheEntry<byte[]>>();
            _libraryCache = new ConcurrentDictionary<string, CacheEntry<List<BaseItemDto>>>();
            _serverHealthCache = new ConcurrentDictionary<string, ServerHealthInfo>();
            _cacheSemaphore = new SemaphoreSlim(1, 1);
            
            // Setup cache directory
            _cacheDirectory = Path.Combine(Path.GetTempPath(), "JellyfinFederation", "Cache");
            Directory.CreateDirectory(_cacheDirectory);
            
            // Start cleanup timer (runs every 15 minutes)
            _cleanupTimer = new Timer(async _ => await CleanupExpiredEntries(), null, 
                TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
                
            _logger.LogInformation("Federation cache service initialized");
        }

        #region Metadata Caching

        public async Task<BaseItemDto> GetCachedMetadataAsync(string serverId, string itemId)
        {
            var key = $"metadata_{serverId}_{itemId}";
            
            if (_metadataCache.TryGetValue(key, out var entry) && !entry.IsExpired)
            {
                _logger.LogDebug($"Cache hit for metadata: {key}");
                entry.LastAccessed = DateTime.UtcNow;
                return entry.Value;
            }

            return null;
        }

        public async Task SetCachedMetadataAsync(string serverId, string itemId, BaseItemDto metadata)
        {
            var key = $"metadata_{serverId}_{itemId}";
            var entry = new CacheEntry<BaseItemDto>
            {
                Value = metadata,
                CreatedAt = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_metadataCacheTtl)
            };

            _metadataCache.AddOrUpdate(key, entry, (k, v) => entry);
            _logger.LogDebug($"Cached metadata: {key}");
        }

        #endregion

        #region Thumbnail Caching

        public async Task<byte[]> GetCachedThumbnailAsync(string serverId, string itemId)
        {
            var key = $"thumbnail_{serverId}_{itemId}";
            
            if (_thumbnailCache.TryGetValue(key, out var entry) && !entry.IsExpired)
            {
                _logger.LogDebug($"Cache hit for thumbnail: {key}");
                entry.LastAccessed = DateTime.UtcNow;
                return entry.Value;
            }

            // Try to load from disk cache
            var diskPath = GetThumbnailDiskPath(serverId, itemId);
            if (File.Exists(diskPath))
            {
                try
                {
                    var fileInfo = new FileInfo(diskPath);
                    if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc < _thumbnailCacheTtl)
                    {
                        var data = await File.ReadAllBytesAsync(diskPath);
                        
                        // Add back to memory cache
                        var cacheEntry = new CacheEntry<byte[]>
                        {
                            Value = data,
                            CreatedAt = fileInfo.LastWriteTimeUtc,
                            LastAccessed = DateTime.UtcNow,
                            ExpiresAt = fileInfo.LastWriteTimeUtc.Add(_thumbnailCacheTtl)
                        };
                        
                        _thumbnailCache.TryAdd(key, cacheEntry);
                        _logger.LogDebug($"Loaded thumbnail from disk cache: {key}");
                        return data;
                    }
                    else
                    {
                        // File expired, delete it
                        File.Delete(diskPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error loading thumbnail from disk: {diskPath}");
                }
            }

            return null;
        }

        public async Task SetCachedThumbnailAsync(string serverId, string itemId, byte[] thumbnailData)
        {
            if (thumbnailData == null || thumbnailData.Length == 0)
                return;

            if (thumbnailData.Length > _maxThumbnailFileSize)
            {
                _logger.LogWarning($"Thumbnail too large to cache: {thumbnailData.Length} bytes");
                return;
            }

            var key = $"thumbnail_{serverId}_{itemId}";
            
            // Ensure we don't exceed cache size
            await EnsureThumbnailCacheSize();

            var entry = new CacheEntry<byte[]>
            {
                Value = thumbnailData,
                CreatedAt = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_thumbnailCacheTtl)
            };

            _thumbnailCache.AddOrUpdate(key, entry, (k, v) => entry);

            // Also save to disk
            try
            {
                var diskPath = GetThumbnailDiskPath(serverId, itemId);
                Directory.CreateDirectory(Path.GetDirectoryName(diskPath));
                await File.WriteAllBytesAsync(diskPath, thumbnailData);
                _logger.LogDebug($"Cached thumbnail: {key} ({thumbnailData.Length} bytes)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to save thumbnail to disk: {key}");
            }
        }

        private string GetThumbnailDiskPath(string serverId, string itemId)
        {
            var serverDir = Path.Combine(_cacheDirectory, "thumbnails", SanitizeFileName(serverId));
            return Path.Combine(serverDir, $"{SanitizeFileName(itemId)}.jpg");
        }

        private async Task EnsureThumbnailCacheSize()
        {
            if (_thumbnailCache.Count <= _maxThumbnailCacheSize)
                return;

            await _cacheSemaphore.WaitAsync();
            try
            {
                if (_thumbnailCache.Count <= _maxThumbnailCacheSize)
                    return;

                var entriesToRemove = _thumbnailCache
                    .OrderBy(kvp => kvp.Value.LastAccessed)
                    .Take(_thumbnailCache.Count - _maxThumbnailCacheSize + 10) // Remove extra to avoid frequent cleanup
                    .ToList();

                foreach (var entry in entriesToRemove)
                {
                    _thumbnailCache.TryRemove(entry.Key, out _);
                }

                _logger.LogInformation($"Removed {entriesToRemove.Count} old thumbnail cache entries");
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }

        #endregion

        #region Library Caching

        public async Task<List<BaseItemDto>> GetCachedLibraryAsync(string serverId)
        {
            var key = $"library_{serverId}";
            
            if (_libraryCache.TryGetValue(key, out var entry) && !entry.IsExpired)
            {
                _logger.LogDebug($"Cache hit for library: {key}");
                entry.LastAccessed = DateTime.UtcNow;
                return entry.Value;
            }

            return null;
        }

        public async Task SetCachedLibraryAsync(string serverId, List<BaseItemDto> libraryItems)
        {
            var key = $"library_{serverId}";
            var entry = new CacheEntry<List<BaseItemDto>>
            {
                Value = libraryItems,
                CreatedAt = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_libraryCacheTtl)
            };

            _libraryCache.AddOrUpdate(key, entry, (k, v) => entry);
            _logger.LogDebug($"Cached library: {key} ({libraryItems?.Count} items)");
        }

        public async Task InvalidateLibraryCacheAsync(string serverId)
        {
            var key = $"library_{serverId}";
            _libraryCache.TryRemove(key, out _);
            _logger.LogDebug($"Invalidated library cache: {key}");
        }

        #endregion

        #region Server Health Caching

        public ServerHealthInfo GetServerHealth(string serverId)
        {
            if (_serverHealthCache.TryGetValue(serverId, out var health) && 
                DateTime.UtcNow - health.LastCheck < _serverHealthCacheTtl)
            {
                return health;
            }

            return null;
        }

        public void SetServerHealth(string serverId, bool isHealthy, TimeSpan? responseTime = null, string error = null)
        {
            var health = new ServerHealthInfo
            {
                ServerId = serverId,
                IsHealthy = isHealthy,
                LastCheck = DateTime.UtcNow,
                ResponseTime = responseTime,
                ErrorMessage = error,
                ConsecutiveFailures = isHealthy ? 0 : (GetServerHealth(serverId)?.ConsecutiveFailures ?? 0) + 1
            };

            _serverHealthCache.AddOrUpdate(serverId, health, (k, v) => health);
            _logger.LogDebug($"Updated server health: {serverId} - {(isHealthy ? "Healthy" : "Unhealthy")}");
        }

        public List<ServerHealthInfo> GetAllServerHealth()
        {
            return _serverHealthCache.Values.ToList();
        }

        #endregion

        #region Cache Management

        private async Task CleanupExpiredEntries()
        {
            await _cacheSemaphore.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;
                var removedCount = 0;

                // Cleanup metadata cache
                var expiredMetadata = _metadataCache
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredMetadata)
                {
                    _metadataCache.TryRemove(key, out _);
                    removedCount++;
                }

                // Cleanup thumbnail cache
                var expiredThumbnails = _thumbnailCache
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredThumbnails)
                {
                    _thumbnailCache.TryRemove(key, out _);
                    removedCount++;
                }

                // Cleanup library cache
                var expiredLibraries = _libraryCache
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredLibraries)
                {
                    _libraryCache.TryRemove(key, out _);
                    removedCount++;
                }

                // Cleanup expired disk thumbnails
                await CleanupExpiredDiskThumbnails();

                if (removedCount > 0)
                {
                    _logger.LogInformation($"Cleaned up {removedCount} expired cache entries");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache cleanup");
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }

        private async Task CleanupExpiredDiskThumbnails()
        {
            try
            {
                var thumbnailDir = Path.Combine(_cacheDirectory, "thumbnails");
                if (!Directory.Exists(thumbnailDir))
                    return;

                var expiredFiles = Directory.GetFiles(thumbnailDir, "*.jpg", SearchOption.AllDirectories)
                    .Where(file =>
                    {
                        var fileInfo = new FileInfo(file);
                        return DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _thumbnailCacheTtl;
                    })
                    .ToList();

                foreach (var file in expiredFiles)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete expired thumbnail: {file}");
                    }
                }

                if (expiredFiles.Count > 0)
                {
                    _logger.LogDebug($"Deleted {expiredFiles.Count} expired thumbnail files");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up disk thumbnails");
            }
        }

        public async Task ClearAllCachesAsync()
        {
            await _cacheSemaphore.WaitAsync();
            try
            {
                _metadataCache.Clear();
                _thumbnailCache.Clear();
                _libraryCache.Clear();
                
                // Clear disk cache
                if (Directory.Exists(_cacheDirectory))
                {
                    Directory.Delete(_cacheDirectory, true);
                    Directory.CreateDirectory(_cacheDirectory);
                }

                _logger.LogInformation("Cleared all caches");
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }

        public CacheStatistics GetCacheStatistics()
        {
            var now = DateTime.UtcNow;
            
            return new CacheStatistics
            {
                MetadataEntries = _metadataCache.Count,
                ThumbnailEntries = _thumbnailCache.Count,
                LibraryEntries = _libraryCache.Count,
                MetadataHitRate = CalculateHitRate(_metadataCache),
                ThumbnailHitRate = CalculateHitRate(_thumbnailCache),
                LibraryHitRate = CalculateHitRate(_libraryCache),
                TotalMemoryUsage = EstimateMemoryUsage(),
                ServerHealthEntries = _serverHealthCache.Count
            };
        }

        private double CalculateHitRate<T>(ConcurrentDictionary<string, CacheEntry<T>> cache)
        {
            if (cache.Count == 0) return 0.0;
            
            var validEntries = cache.Values.Count(e => !e.IsExpired);
            return (double)validEntries / cache.Count * 100.0;
        }

        private long EstimateMemoryUsage()
        {
            long size = 0;
            
            // Estimate thumbnail cache size
            foreach (var entry in _thumbnailCache.Values)
            {
                size += entry.Value?.Length ?? 0;
            }
            
            // Add overhead for other caches (rough estimate)
            size += _metadataCache.Count * 1024; // ~1KB per metadata entry
            size += _libraryCache.Count * 10240; // ~10KB per library cache
            
            return size;
        }

        #endregion

        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanupTimer?.Dispose();
                _cacheSemaphore?.Dispose();
                _disposed = true;
                _logger.LogInformation("Federation cache service disposed");
            }
        }
    }

    public class CacheEntry<T>
    {
        public T Value { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccessed { get; set; }
        public DateTime ExpiresAt { get; set; }
        
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }

    public class ServerHealthInfo
    {
        public string ServerId { get; set; }
        public bool IsHealthy { get; set; }
        public DateTime LastCheck { get; set; }
        public TimeSpan? ResponseTime { get; set; }
        public string ErrorMessage { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    public class CacheStatistics
    {
        public int MetadataEntries { get; set; }
        public int ThumbnailEntries { get; set; }
        public int LibraryEntries { get; set; }
        public double MetadataHitRate { get; set; }
        public double ThumbnailHitRate { get; set; }
        public double LibraryHitRate { get; set; }
        public long TotalMemoryUsage { get; set; }
        public int ServerHealthEntries { get; set; }
    }
}