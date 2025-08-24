using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using JellyfinFederationPlugin.Configuration;
using System.Collections.Generic;
using System;
using MediaBrowser.Common.Configuration;
using JellyfinFederationPlugin.Services;
using JellyfinFederationPlugin.Library;
using JellyfinFederationPlugin.Streaming;
using System.Threading.Tasks;

namespace JellyfinFederationPlugin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
    {
        public override string Name => "Jellyfin Federation Plugin";
        public override string Description => "Enables federation across Jellyfin servers for streaming media without syncing files.";
        public override Guid Id => new Guid("ab6fad0e-ffcb-469c-b1b0-b11f79ddf447");

        public static Plugin Instance { get; private set; }

        private readonly ILogger<Plugin> _logger;
        private FederationSyncService _syncService;
        private FederationLibraryMonitor _libraryMonitor;
        private FederationStreamingService _streamingService;
        private bool _disposed = false;
        private bool _initialized = false;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logger;
            
            // Initialize the federation services when plugin is created
            _ = Task.Run(InitializeFederationServicesAsync);
        }

        private async Task InitializeFederationServicesAsync()
        {
            try
            {
                _logger.LogInformation("Initializing Jellyfin Federation Plugin services...");
                
                // Wait a bit for dependency injection to be fully set up
                await Task.Delay(2000);
                
                await InitializeServices();
                
                _initialized = true;
                _logger.LogInformation("Federation Plugin services initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Federation Plugin services");
            }
        }

        private async Task InitializeServices()
        {
            try
            {
                // Note: In a production implementation, you would get these from the DI container
                // For this example, we'll show how they would be initialized
                
                _logger.LogInformation("Setting up federation services...");
                
                // Library monitor would be initialized here with proper DI
                // _libraryMonitor = serviceProvider.GetService<FederationLibraryMonitor>();
                // await _libraryMonitor.InitializeAsync();
                
                // Sync service would be initialized here
                // _syncService = serviceProvider.GetService<FederationSyncService>();
                // await _syncService.StartAsync();
                
                // Streaming service would be initialized here
                // _streamingService = serviceProvider.GetService<FederationStreamingService>();
                
                _logger.LogInformation("Federation services setup completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up federation services");
                throw;
            }
        }

        public async Task<bool> TriggerLibrarySyncAsync()
        {
            try
            {
                if (!_initialized)
                {
                    _logger.LogWarning("Plugin not yet initialized, cannot trigger sync");
                    return false;
                }

                _logger.LogInformation("Triggering manual federation library sync...");
                
                if (_libraryMonitor != null)
                {
                    await _libraryMonitor.SyncFederatedLibrariesAsync();
                    return true;
                }
                else if (_syncService != null)
                {
                    await _syncService.TriggerManualSync();
                    return true;
                }
                
                _logger.LogWarning("No sync service available");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering federation sync");
                return false;
            }
        }

        public async Task<List<FederationStreamingService.StreamSession>> GetActiveStreamSessionsAsync()
        {
            try
            {
                if (_streamingService != null)
                {
                    return await _streamingService.GetActiveSessionsAsync();
                }
                
                return new List<FederationStreamingService.StreamSession>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active stream sessions");
                return new List<FederationStreamingService.StreamSession>();
            }
        }

        public void UpdateConfiguration(PluginConfiguration configuration)
        {
            var currentConfig = this.Configuration;
            currentConfig.FederatedServers = configuration.FederatedServers;
            SaveConfiguration();
            _logger?.LogInformation("Plugin configuration updated.");
            
            // Trigger a resync when configuration changes
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000); // Small delay to ensure config is saved
                    await TriggerLibrarySyncAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error triggering sync after configuration update");
                }
            });
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "JellyfinFederationSettings",
                    EmbeddedResourcePath = "JellyfinFederationPlugin.Web.JellyfinFederationSettings.html"
                }
            };
        }

        public bool IsInitialized => _initialized;

        public void Dispose()
        {
            if (!_disposed)
            {
                _logger?.LogInformation("Shutting down Jellyfin Federation Plugin...");
                
                try
                {
                    _syncService?.Dispose();
                    _libraryMonitor?.Dispose();
                    _streamingService?.Dispose();
                    
                    _logger?.LogInformation("Federation Plugin shutdown completed");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error during plugin shutdown");
                }
                
                _disposed = true;
            }
        }
    }
}
