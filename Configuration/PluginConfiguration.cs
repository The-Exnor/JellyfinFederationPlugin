using MediaBrowser.Model.Plugins;
using System.Collections.Generic;

namespace JellyfinFederationPlugin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<FederatedServer> FederatedServers { get; set; } = new List<FederatedServer>();
        
        // Caching Configuration
        public CachingSettings Caching { get; set; } = new CachingSettings();
        
        // Failover Configuration
        public FailoverSettings Failover { get; set; } = new FailoverSettings();
        
        // Bandwidth Management Configuration
        public BandwidthSettings Bandwidth { get; set; } = new BandwidthSettings();

        public class FederatedServer
        {
            public string ServerUrl { get; set; }
            public string ApiKey { get; set; }
            public int Port { get; set; }
            public int Priority { get; set; } = 1; // For failover ordering
            public long MaxBandwidth { get; set; } = 100 * 1024 * 1024; // 100 Mbps default
            public bool EnableFailover { get; set; } = true;
            public bool EnableCaching { get; set; } = true;
        }

        public class CachingSettings
        {
            public bool EnableMetadataCache { get; set; } = true;
            public bool EnableThumbnailCache { get; set; } = true;
            public bool EnableLibraryCache { get; set; } = true;
            public int MetadataCacheHours { get; set; } = 6;
            public int ThumbnailCacheDays { get; set; } = 7;
            public int LibraryCacheMinutes { get; set; } = 30;
            public int MaxThumbnailCacheSize { get; set; } = 1000;
            public long MaxThumbnailFileSize { get; set; } = 5 * 1024 * 1024; // 5MB
        }

        public class FailoverSettings
        {
            public bool EnableFailover { get; set; } = true;
            public bool EnableContentIndexing { get; set; } = true;
            public int MaxConsecutiveFailures { get; set; } = 3;
            public int ServerBackoffMinutes { get; set; } = 10;
            public int HealthCheckIntervalMinutes { get; set; } = 2;
            public bool PreferOriginalServer { get; set; } = true;
        }

        public class BandwidthSettings
        {
            public bool EnableBandwidthManagement { get; set; } = true;
            public bool EnableAdaptiveStreaming { get; set; } = true;
            public long DefaultMaxBandwidthPerServer { get; set; } = 100 * 1024 * 1024; // 100 Mbps
            public long DefaultMaxBandwidthPerClient { get; set; } = 50 * 1024 * 1024;  // 50 Mbps
            public int MaxConcurrentStreamsPerServer { get; set; } = 10;
            public int MaxConcurrentStreamsPerClient { get; set; } = 3;
            public int BandwidthWindowMinutes { get; set; } = 5;
            public int AdaptationIntervalSeconds { get; set; } = 30;
            public string DefaultQuality { get; set; } = "1080p";
        }
    }
}
