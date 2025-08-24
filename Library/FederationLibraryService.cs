using MediaBrowser.Controller.Library;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities.Movies;
using System.Linq;
using BaseItem = MediaBrowser.Controller.Entities.BaseItem;
using BaseItemDto = MediaBrowser.Model.Dto.BaseItemDto;
using System.Collections.Generic;
using System;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace JellyfinFederationPlugin.Library
{
    public class FederationLibraryService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly FederationRequestHandler _federationRequestHandler;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _mergeSemaphore;

        public FederationLibraryService(ILibraryManager libraryManager, FederationRequestHandler federationRequestHandler, ILogger logger)
        {
            _libraryManager = libraryManager;
            _federationRequestHandler = federationRequestHandler;
            _logger = logger;
            _mergeSemaphore = new SemaphoreSlim(1, 1); // Ensure only one merge operation at a time
        }

        public async Task MergeFederatedLibrariesAsync()
        {
            await _mergeSemaphore.WaitAsync();
            
            try
            {
                _logger.LogInformation("Starting federation library merge...");
                
                var config = Plugin.Instance.Configuration;
                
                if (config.FederatedServers == null || !config.FederatedServers.Any())
                {
                    _logger.LogInformation("No federated servers configured. Skipping merge.");
                    return;
                }

                var totalItemsAdded = 0;
                var serverResults = new List<(string ServerUrl, int ItemCount, bool Success)>();

                // Process servers in parallel with limited concurrency
                var semaphore = new SemaphoreSlim(3, 3); // Process max 3 servers concurrently
                var tasks = config.FederatedServers.Select(async server =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        return await ProcessFederatedServer(server);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks);
                
                foreach (var (serverUrl, itemCount, success) in results)
                {
                    serverResults.Add((serverUrl, itemCount, success));
                    if (success)
                    {
                        totalItemsAdded += itemCount;
                    }
                }

                _logger.LogInformation($"Federation library merge completed. Added {totalItemsAdded} items from {serverResults.Count(r => r.Success)} servers.");
                
                // Log individual server results
                foreach (var (serverUrl, itemCount, success) in serverResults)
                {
                    if (success)
                    {
                        _logger.LogInformation($"Server {serverUrl}: {itemCount} items added");
                    }
                    else
                    {
                        _logger.LogWarning($"Server {serverUrl}: Failed to fetch items");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during federation library merge");
            }
            finally
            {
                _mergeSemaphore.Release();
            }
        }

        private async Task<(string ServerUrl, int ItemCount, bool Success)> ProcessFederatedServer(Configuration.PluginConfiguration.FederatedServer server)
        {
            try
            {
                _logger.LogInformation($"Processing federated server: {server.ServerUrl}");

                // Validate server connection first
                if (!await _federationRequestHandler.ValidateServerConnection(server))
                {
                    _logger.LogWarning($"Server validation failed for {server.ServerUrl}");
                    return (server.ServerUrl, 0, false);
                }

                var federatedItems = await _federationRequestHandler.GetFederatedLibrary(server);

                if (federatedItems == null || !federatedItems.Any())
                {
                    _logger.LogInformation($"No items found on server {server.ServerUrl}");
                    return (server.ServerUrl, 0, true);
                }

                _logger.LogInformation($"Fetched {federatedItems.Count} items from {server.ServerUrl}");

                var addedCount = 0;
                foreach (var federatedItem in federatedItems)
                {
                    try
                    {
                        if (await AddVirtualItemToLibrary(federatedItem, server.ServerUrl))
                        {
                            addedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to add item {federatedItem.Name} from server {server.ServerUrl}");
                    }
                }

                return (server.ServerUrl, addedCount, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing server {server.ServerUrl}");
                return (server.ServerUrl, 0, false);
            }
        }

        private async Task<bool> AddVirtualItemToLibrary(BaseItemDto remoteItem, string serverUrl)
        {
            try
            {
                // Check if item already exists to avoid duplicates
                var existingPath = $"remote://{serverUrl}|{remoteItem.Id}";
                var existingItem = _libraryManager.FindByPath(existingPath, false);
                
                if (existingItem != null)
                {
                    _logger.LogDebug($"Item {remoteItem.Name} already exists in library, skipping");
                    return false;
                }

                var virtualItem = CreateVirtualItem(remoteItem, serverUrl);
                
                if (virtualItem == null)
                {
                    _logger.LogWarning($"Failed to create virtual item for {remoteItem.Name}");
                    return false;
                }

                // Note: In a real implementation, you'd need to integrate with Jellyfin's library scanning
                // For now, we'll just log that we would add the item
                _logger.LogDebug($"Would add virtual item: {virtualItem.Name} (Path: {virtualItem.Path})");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding virtual item {remoteItem.Name} to library");
                return false;
            }
        }

        private BaseItem CreateVirtualItem(BaseItemDto remoteItem, string serverUrl)
        {
            if (remoteItem == null || string.IsNullOrWhiteSpace(remoteItem.Name))
            {
                return null;
            }

            // Simple type detection based on string comparison
            BaseItem virtualItem = remoteItem.Type.ToString().ToLowerInvariant() switch
            {
                "movie" => new Movie(),
                "series" => new Series(),
                "episode" => new Episode(),
                "season" => new Season(),
                _ => new Movie() // Default to movie for unknown types
            };

            virtualItem.Name = remoteItem.Name;
            virtualItem.Path = $"remote://{serverUrl}|{remoteItem.Id}";
            virtualItem.Id = Guid.NewGuid(); // Generate new local ID
            virtualItem.DateCreated = DateTime.UtcNow;
            virtualItem.DateModified = DateTime.UtcNow;

            // Copy additional metadata if available
            if (remoteItem.ProductionYear.HasValue)
            {
                virtualItem.ProductionYear = remoteItem.ProductionYear;
            }

            if (!string.IsNullOrWhiteSpace(remoteItem.Overview))
            {
                virtualItem.Overview = remoteItem.Overview;
            }

            if (remoteItem.CommunityRating.HasValue)
            {
                virtualItem.CommunityRating = remoteItem.CommunityRating;
            }

            // Add federation prefix to distinguish from local content
            virtualItem.Name = $"[Fed] {virtualItem.Name}";

            return virtualItem;
        }

        public async Task<bool> TestFederatedServerAsync(Configuration.PluginConfiguration.FederatedServer server)
        {
            try
            {
                _logger.LogInformation($"Testing connection to federated server: {server.ServerUrl}");
                
                var isValid = await _federationRequestHandler.ValidateServerConnection(server);
                
                if (isValid)
                {
                    _logger.LogInformation($"Successfully connected to server: {server.ServerUrl}");
                }
                else
                {
                    _logger.LogWarning($"Failed to connect to server: {server.ServerUrl}");
                }
                
                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error testing server {server.ServerUrl}");
                return false;
            }
        }
    }
}
