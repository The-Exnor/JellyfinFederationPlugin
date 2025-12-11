using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Manages federated library integration with Jellyfin.
/// </summary>
    public class FederationLibraryManager : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
  private readonly ILogger<FederationLibraryManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, RemoteServerClient> _clients;
  private readonly ConcurrentDictionary<string, BaseItem> _federatedItems;

        /// <summary>
   /// Initializes a new instance of the <see cref="FederationLibraryManager"/> class.
   /// </summary>
     /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
        public FederationLibraryManager(
            ILibraryManager libraryManager,
   ILogger<FederationLibraryManager> logger,
     ILoggerFactory loggerFactory)
    {
 _libraryManager = libraryManager;
 _logger = logger;
        _loggerFactory = loggerFactory;
_clients = new ConcurrentDictionary<string, RemoteServerClient>();
            _federatedItems = new ConcurrentDictionary<string, BaseItem>();
   }

      /// <summary>
        /// Initializes the federation library manager.
        /// </summary>
    public void Initialize()
        {
       _logger.LogInformation("[Federation] Initializing Federation Library Manager");

         var config = Plugin.Instance?.Configuration;
        if (config == null)
     {
        _logger.LogWarning("[Federation] Plugin configuration is null");
       return;
    }

     InitializeClients();
   }

        /// <summary>
        /// Initializes clients for all configured remote servers.
        /// </summary>
        public void InitializeClients()
     {
          var config = Plugin.Instance?.Configuration;
   if (config?.RemoteServers == null)
  {
    return;
   }

          // Dispose existing clients
            foreach (var client in _clients.Values)
 {
   client.Dispose();
 }
     _clients.Clear();

            // Create new clients for enabled servers
            foreach (var server in config.RemoteServers.Where(s => s.Enabled))
            {
  try
       {
           var client = new RemoteServerClient(
   server,
        _loggerFactory.CreateLogger<RemoteServerClient>());

             _clients.TryAdd(server.Id, client);
         _logger.LogInformation("[Federation] Initialized client for remote server: {ServerName} ({ServerId})", 
               server.Name, server.Id);
   }
                catch (Exception ex)
 {
   _logger.LogError(ex, "[Federation] Failed to initialize client for remote server: {ServerName}", 
              server.Name);
         }
            }
        }

        /// <summary>
        /// Creates or updates virtual folders based on configured mappings.
        /// </summary>
     /// <param name="cancellationToken">Cancellation token.</param>
     /// <returns>Task.</returns>
  public async Task SyncVirtualFoldersAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[Federation] Syncing virtual folders");

  var config = Plugin.Instance?.Configuration;
            if (config?.LibraryMappings == null)
  {
     _logger.LogWarning("[Federation] No library mappings configured");
                return;
  }

      var enabledMappings = config.LibraryMappings.Where(m => m.Enabled).ToList();
            _logger.LogInformation("[Federation] Found {Count} enabled mappings", enabledMappings.Count);

 foreach (var mapping in enabledMappings)
     {
     try
      {
         await EnsureVirtualFolderExistsAsync(mapping, cancellationToken);
    }
   catch (Exception ex)
            {
        _logger.LogError(ex, "[Federation] Error creating virtual folder for mapping: {Name}", 
   mapping.LocalLibraryName);
                }
            }
   }

        /// <summary>
     /// Ensures a virtual folder exists for a mapping.
        /// </summary>
        /// <param name="mapping">Library mapping.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
   /// <returns>The virtual folder.</returns>
  public async Task<Folder?> EnsureVirtualFolderExistsAsync(
  LibraryMapping mapping, 
       CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[Federation] Ensuring virtual folder exists: {Name}", mapping.LocalLibraryName);

   // Note: With file-based approach, we don't need to create virtual folders in Jellyfin
            // The user will add the federation file directory as a library manually
   // This method is kept for backward compatibility but does nothing
      
    _logger.LogInformation("[Federation] File-based approach - user will add folder as library manually");

 // Create a placeholder folder object (not registered with Jellyfin)
    var folder = new Folder
     {
    Name = mapping.LocalLibraryName,
      DateCreated = DateTime.UtcNow,
    DateModified = DateTime.UtcNow
  };

     return folder;
   }

        /// <summary>
        /// Adds a federated item to the library.
        /// </summary>
        /// <param name="item">The item to add.</param>
        /// <param name="parentFolder">Parent folder.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task AddFederatedItemAsync(
            BaseItem item, 
         Folder parentFolder, 
            CancellationToken cancellationToken = default)
        {
  _logger.LogInformation("[Federation] Adding federated item: {Name}", item.Name);

         try
   {
         // Store in our cache
         _federatedItems.TryAdd(item.Id.ToString(), item);

         // TODO: Add to Jellyfin's library manager
     // This requires using the appropriate LibraryManager API
    // await _libraryManager.CreateItem(item, parentFolder, cancellationToken);

     _logger.LogInformation("[Federation] Federated item cached: {Name} (Id: {Id})", item.Name, item.Id);
     }
     catch (Exception ex)
          {
     _logger.LogError(ex, "[Federation] Error adding federated item: {Name}", item.Name);
    }
        }

        /// <summary>
        /// Gets a federated item by ID.
        /// </summary>
        /// <param name="itemId">Item ID.</param>
      /// <returns>The federated item, or null if not found.</returns>
        public BaseItem? GetFederatedItem(string itemId)
        {
       _federatedItems.TryGetValue(itemId, out var item);
    return item;
        }

/// <summary>
        /// Gets a federated item by federation path.
        /// </summary>
        /// <param name="federationPath">Federation path (federation://serverId/itemId).</param>
      /// <returns>The federated item, or null if not found.</returns>
      public BaseItem? GetFederatedItemByPath(string federationPath)
        {
     if (TryParseFederationPath(federationPath, out var serverId, out var remoteItemId))
   {
         // Find item by remote ID
       return _federatedItems.Values.FirstOrDefault(item =>
   item.ProviderIds != null &&
     item.ProviderIds.TryGetValue("FederationSource", out var itemServerId) &&
          itemServerId == serverId &&
    item.ProviderIds.TryGetValue("FederationRemoteId", out var itemRemoteId) &&
  itemRemoteId == remoteItemId);
      }

    return null;
        }

        /// <summary>
        /// Checks if an item is federated.
        /// </summary>
     /// <param name="item">The item to check.</param>
        /// <returns>True if federated, false otherwise.</returns>
        public bool IsFederatedItem(BaseItem item)
        {
            return item?.ProviderIds?.ContainsKey("FederationSource") == true;
        }

        /// <summary>
    /// Gets the remote server ID for a federated item.
     /// </summary>
        /// <param name="item">The federated item.</param>
        /// <returns>Server ID, or null if not federated.</returns>
        public string? GetFederatedServerId(BaseItem item)
        {
  if (item?.ProviderIds == null)
  {
       return null;
    }

  item.ProviderIds.TryGetValue("FederationSource", out var serverId);
            return serverId;
        }

     /// <summary>
    /// Gets the remote item ID for a federated item.
   /// </summary>
        /// <param name="item">The federated item.</param>
        /// <returns>Remote item ID, or null if not federated.</returns>
        public string? GetFederatedRemoteId(BaseItem item)
        {
    if (item?.ProviderIds == null)
      {
    return null;
}

 item.ProviderIds.TryGetValue("FederationRemoteId", out var remoteId);
  return remoteId;
}

        /// <summary>
     /// Gets a remote server client by server ID.
        /// </summary>
/// <param name="serverId">The server ID.</param>
        /// <returns>The client, or null if not found.</returns>
        public RemoteServerClient? GetClient(string serverId)
      {
     _clients.TryGetValue(serverId, out var client);
    return client;
      }

        /// <summary>
        /// Parses a federation path into server ID and item ID.
        /// </summary>
        /// <param name="federationPath">The federation path.</param>
      /// <param name="serverId">Output server ID.</param>
    /// <param name="itemId">Output item ID.</param>
        /// <returns>True if parsing succeeded.</returns>
        public static bool TryParseFederationPath(string federationPath, out string serverId, out string itemId)
        {
          serverId = string.Empty;
    itemId = string.Empty;

    if (string.IsNullOrEmpty(federationPath))
            {
        return false;
            }

            if (!federationPath.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

     var pathPart = federationPath.Substring("federation://".Length);
            var parts = pathPart.Split('/', 2);

            if (parts.Length != 2)
    {
  return false;
            }

            serverId = parts[0];
       itemId = parts[1];
          return true;
        }

      /// <summary>
        /// Gets all federated items.
        /// </summary>
        /// <returns>Collection of federated items.</returns>
 public IEnumerable<BaseItem> GetAllFederatedItems()
      {
        return _federatedItems.Values;
        }

        /// <inheritdoc />
        public void Dispose()
        {
    foreach (var client in _clients.Values)
   {
                client.Dispose();
      }
            _clients.Clear();
    _federatedItems.Clear();
        }
    }
}
