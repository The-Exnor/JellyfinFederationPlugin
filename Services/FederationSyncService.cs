using Microsoft.Extensions.Logging;
using JellyfinFederationPlugin.Library;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinFederationPlugin.Services
{
    public class FederationSyncService : IDisposable
    {
        private readonly FederationLibraryService _federationLibraryService;
        private readonly ILogger<FederationSyncService> _logger;
        private Timer _syncTimer;
        private bool _disposed = false;

        public FederationSyncService(FederationLibraryService federationLibraryService, ILogger<FederationSyncService> logger)
        {
            _federationLibraryService = federationLibraryService;
            _logger = logger;
        }

        public Task StartAsync()
        {
            _logger.LogInformation("Federation Sync Service starting...");
            
            // Start periodic sync every 30 minutes
            var syncInterval = TimeSpan.FromMinutes(30);
            _syncTimer = new Timer(async _ => await PerformSync(), null, TimeSpan.Zero, syncInterval);
            
            return Task.CompletedTask;
        }

        public async Task TriggerManualSync()
        {
            _logger.LogInformation("Manual federation sync triggered");
            await PerformSync();
        }

        private async Task PerformSync()
        {
            try
            {
                _logger.LogInformation("Starting federation library sync...");
                await _federationLibraryService.MergeFederatedLibrariesAsync();
                _logger.LogInformation("Federation library sync completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during federation library sync");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _syncTimer?.Dispose();
                _disposed = true;
            }
        }
    }
}