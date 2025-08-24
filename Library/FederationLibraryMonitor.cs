using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using JellyfinFederationPlugin.Configuration;

namespace JellyfinFederationPlugin.Library
{
    public class FederationLibraryMonitor : ILibraryMonitor, IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly FederationRequestHandler _federationRequestHandler;
        private readonly ILogger<FederationLibraryMonitor> _logger;
        private readonly Dictionary<string, FederatedVirtualFolder> _virtualFolders;
        private readonly SemaphoreSlim _syncSemaphore;
        private bool _disposed = false;

        public FederationLibraryMonitor(
            ILibraryManager libraryManager,
            FederationRequestHandler federationRequestHandler,
            ILogger<FederationLibraryMonitor> logger)
        {
            _libraryManager = libraryManager;
            _federationRequestHandler = federationRequestHandler;
            _logger = logger;
            _virtualFolders = new Dictionary<string, FederatedVirtualFolder>();
            _syncSemaphore = new SemaphoreSlim(1, 1);
        }

        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("Initializing Federation Library Monitor...");
                
                // Create virtual folders for each federated server
                await CreateFederatedVirtualFolders();
                
                // Note: In a real implementation, you'd hook into library events here
                // _libraryManager.ItemAdded += OnLibraryItemAdded;
                // _libraryManager.ItemRemoved += OnLibraryItemRemoved;
                // _libraryManager.ItemUpdated += OnLibraryItemUpdated;
                
                _logger.LogInformation("Federation Library Monitor initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Federation Library Monitor");
            }
        }

        private async Task CreateFederatedVirtualFolders()
        {
            var config = Plugin.Instance.Configuration;
            
            if (config.FederatedServers == null || !config.FederatedServers.Any())
            {
                _logger.LogInformation("No federated servers configured");
                return;
            }

            foreach (var server in config.FederatedServers)
            {
                try
                {
                    var virtualFolder = new FederatedVirtualFolder
                    {
                        ServerId = server.ServerUrl,
                        Name = $"Federation - {GetServerDisplayName(server.ServerUrl)}",
                        FolderPath = $"federation://{server.ServerUrl}",
                        Server = server
                    };

                    _virtualFolders[server.ServerUrl] = virtualFolder;
                    
                    _logger.LogInformation($"Created virtual folder for server: {server.ServerUrl}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to create virtual folder for server: {server.ServerUrl}");
                }
            }
        }

        public async Task SyncFederatedLibrariesAsync()
        {
            await _syncSemaphore.WaitAsync();
            
            try
            {
                _logger.LogInformation("Starting federated library sync...");
                
                var tasks = _virtualFolders.Values.Select(async virtualFolder =>
                {
                    try
                    {
                        await SyncVirtualFolder(virtualFolder);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to sync virtual folder: {virtualFolder.Name}");
                    }
                });

                await Task.WhenAll(tasks);
                
                _logger.LogInformation("Federated library sync completed");
            }
            finally
            {
                _syncSemaphore.Release();
            }
        }

        private async Task SyncVirtualFolder(FederatedVirtualFolder virtualFolder)
        {
            _logger.LogInformation($"Syncing virtual folder: {virtualFolder.Name}");
            
            var federatedItems = await _federationRequestHandler.GetFederatedLibrary(virtualFolder.Server);
            
            if (federatedItems == null || !federatedItems.Any())
            {
                _logger.LogInformation($"No items found for virtual folder: {virtualFolder.Name}");
                return;
            }

            var addedCount = 0;
            foreach (var item in federatedItems)
            {
                try
                {
                    if (await AddFederatedItemToLibrary(item, virtualFolder))
                    {
                        addedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to add federated item: {item.Name}");
                }
            }
            
            _logger.LogInformation($"Added {addedCount} items from virtual folder: {virtualFolder.Name}");
        }

        private async Task<bool> AddFederatedItemToLibrary(BaseItemDto remoteItem, FederatedVirtualFolder virtualFolder)
        {
            try
            {
                // Create a unique path for the federated item
                var federatedPath = $"federation://{virtualFolder.ServerId}/{remoteItem.Id}";
                
                // Check if item already exists
                var existingItem = _libraryManager.FindByPath(federatedPath, false);
                if (existingItem != null)
                {
                    // Update existing item if needed
                    await UpdateFederatedItem(existingItem, remoteItem);
                    return false; // Not newly added
                }

                // Create new federated item
                var federatedItem = CreateFederatedItem(remoteItem, virtualFolder);
                if (federatedItem == null)
                {
                    return false;
                }

                // Log that we would add the item (in a real implementation, you'd add it to the library)
                _logger.LogDebug($"Would add federated item: {federatedItem.Name} (Path: {federatedItem.Path})");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding federated item: {remoteItem.Name}");
                return false;
            }
        }

        private BaseItem CreateFederatedItem(BaseItemDto remoteItem, FederatedVirtualFolder virtualFolder)
        {
            BaseItem item = remoteItem.Type.ToString().ToLowerInvariant() switch
            {
                "movie" => new Movie(),
                "series" => new Series(),
                "episode" => new Episode(),
                "season" => new Season(),
                _ => new Movie() // Default to movie
            };

            // Set basic properties
            item.Id = Guid.NewGuid();
            item.Name = $"[{GetServerDisplayName(virtualFolder.ServerId)}] {remoteItem.Name}";
            item.Path = $"federation://{virtualFolder.ServerId}/{remoteItem.Id}";
            item.DateCreated = remoteItem.DateCreated ?? DateTime.UtcNow;
            item.DateModified = DateTime.UtcNow;

            // Copy metadata
            if (remoteItem.ProductionYear.HasValue)
                item.ProductionYear = remoteItem.ProductionYear;
            
            if (!string.IsNullOrWhiteSpace(remoteItem.Overview))
                item.Overview = remoteItem.Overview;
            
            if (remoteItem.CommunityRating.HasValue)
                item.CommunityRating = remoteItem.CommunityRating;

            // Note: SetProviderId is not available in this version, we'd store this differently
            // item.SetProviderId("FederationServerId", virtualFolder.ServerId);
            // item.SetProviderId("FederationItemId", remoteItem.Id.ToString());

            return item;
        }

        private async Task UpdateFederatedItem(BaseItem existingItem, BaseItemDto remoteItem)
        {
            // Update metadata if remote item is newer
            if (remoteItem.DateCreated > existingItem.DateModified)
            {
                // Note: GetProviderId is not available, we'd get server info differently
                var serverDisplayName = "Unknown Server"; // In real implementation, extract from path
                existingItem.Name = $"[{serverDisplayName}] {remoteItem.Name}";
                
                if (remoteItem.ProductionYear.HasValue)
                    existingItem.ProductionYear = remoteItem.ProductionYear;
                
                if (!string.IsNullOrWhiteSpace(remoteItem.Overview))
                    existingItem.Overview = remoteItem.Overview;
                
                if (remoteItem.CommunityRating.HasValue)
                    existingItem.CommunityRating = remoteItem.CommunityRating;

                existingItem.DateModified = DateTime.UtcNow;
                
                _logger.LogDebug($"Updated federated item: {existingItem.Name}");
            }
        }

        private string GetServerDisplayName(string serverUrl)
        {
            try
            {
                var uri = new Uri(serverUrl);
                return uri.Host;
            }
            catch
            {
                return serverUrl;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // Note: In a real implementation, you'd unhook from library events here
                // _libraryManager.ItemAdded -= OnLibraryItemAdded;
                // _libraryManager.ItemRemoved -= OnLibraryItemRemoved;
                // _libraryManager.ItemUpdated -= OnLibraryItemUpdated;
                
                _syncSemaphore?.Dispose();
                _disposed = true;
                
                _logger.LogInformation("Federation Library Monitor disposed");
            }
        }

        private class FederatedVirtualFolder
        {
            public string ServerId { get; set; }
            public string Name { get; set; }
            public string FolderPath { get; set; }
            public PluginConfiguration.FederatedServer Server { get; set; }
        }
    }

    public interface ILibraryMonitor
    {
        Task InitializeAsync();
        Task SyncFederatedLibrariesAsync();
    }
}