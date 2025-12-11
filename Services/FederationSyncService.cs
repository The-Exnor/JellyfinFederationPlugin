using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
  /// Service for syncing content from remote federated servers.
    /// </summary>
    public class FederationSyncService
    {
        private readonly ILogger<FederationSyncService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILoggerFactory _loggerFactory;

   /// <summary>
        /// Initializes a new instance of the <see cref="FederationSyncService"/> class.
 /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        public FederationSyncService(
     ILogger<FederationSyncService> logger,
        ILibraryManager libraryManager,
     ILoggerFactory loggerFactory)
        {
        _logger = logger;
        _libraryManager = libraryManager;
    _loggerFactory = loggerFactory;
        }

        /// <summary>
      /// Syncs all enabled mappings from the configuration.
        /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
      /// <returns>Sync result.</returns>
   public async Task<SyncResult> SyncAllAsync(CancellationToken cancellationToken = default)
      {
       try
     {
   _logger.LogInformation("[Federation] Starting sync of all libraries");

  // Generate operation ID for progress tracking
      var operationId = Guid.NewGuid().ToString();

    // Use the file-based service to create .strm and .nfo files
    var fileService = new FederationFileService(
         _loggerFactory.CreateLogger<FederationFileService>(),
   _libraryManager,
   _loggerFactory);

      SyncProgressTracker.Start(operationId, 100); // Start with estimate, will update
            SyncProgressTracker.Update(operationId, 0, "Starting sync...");

  var fileCount = await fileService.CreateFederationFilesAsync(operationId, cancellationToken);
  var basePath = fileService.GetFederationBasePath();

_logger.LogInformation("[Federation] Created {Count} .strm files at {Path}", fileCount, basePath);

       SyncProgressTracker.Complete(operationId, true, $"Synced {fileCount} items");

  // For now, we'll skip automatic library creation and guide user
    // TODO: Inject IServerConfigurationManager and use FederationLibraryCreationService
  
       var message = $"Created {fileCount} .strm files at {basePath}.\n\n" +
     $"To view content:\n" +
    $"1. Go to Dashboard → Libraries → Add Media Library\n" +
    $"2. Select content type (Movies/TV Shows)\n" +
      $"3. Add folder: {basePath}/[Mapping Name]\n" +
    $"4. Save and wait for scan to complete\n\n" +
          $"Or use the 'Create Libraries' button to do this automatically.\n\n" +
        $"Progress ID: {operationId}";

return new SyncResult
    {
     Success = true,
    ItemCount = fileCount,
    Message = message,
  OperationId = operationId
          };
  }
      catch (Exception ex)
   {
    _logger.LogError(ex, "[Federation] Error creating federation files");
       return new SyncResult { Success = false, Message = $"Error: {ex.Message}" };
 }
   }

        /// <summary>
        /// Syncs a specific server by its ID.
        /// </summary>
      /// <param name="serverId">Server ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Sync result.</returns>
        public async Task<SyncResult> SyncServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
     _logger.LogInformation("[Federation] Syncing server: {ServerId}", serverId);

  var config = Plugin.Instance?.Configuration;
    if (config == null)
{
      return new SyncResult { Success = false, Message = "Plugin not initialized" };
    }

     var server = config.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
 if (server == null)
  {
   return new SyncResult { Success = false, Message = "Server not found" };
  }

 // Find all mappings that use this server
          var relevantMappings = config.LibraryMappings?
            .Where(m => m.Enabled && m.RemoteLibrarySources?.Any(s => s.ServerId == serverId) == true)
       .ToList();

if (relevantMappings == null || relevantMappings.Count == 0)
 {
        return new SyncResult { Success = false, Message = "No mappings use this server" };
         }

   _logger.LogInformation("[Federation] Found {Count} mappings using server {ServerName}", 
 relevantMappings.Count, server.Name);

            // Use the file-based service to create .strm and .nfo files
  var fileService = new FederationFileService(
         _loggerFactory.CreateLogger<FederationFileService>(),
       _libraryManager,
     _loggerFactory);

    try
   {
     var fileCount = await fileService.CreateFederationFilesAsync(null, cancellationToken);
  var basePath = fileService.GetFederationBasePath();

    _logger.LogInformation("[Federation] Created {Count} .strm files for server {ServerName}", 
        fileCount, server.Name);

    return new SyncResult
     {
     Success = true,
             ItemCount = fileCount,
  Message = $"Synced {fileCount} items from server"
   };
 }
 catch (Exception ex)
   {
      _logger.LogError(ex, "[Federation] Error syncing server {ServerId}", serverId);
      return new SyncResult { Success = false, Message = $"Error: {ex.Message}" };
  }
        }

    /// <summary>
   /// Syncs a single mapping.
        /// </summary>
        private async Task<SyncResult> SyncMappingAsync(
  LibraryMapping mapping,
         PluginConfiguration config,
            CancellationToken cancellationToken)
  {
            _logger.LogInformation("[Federation] Processing mapping: {Name} with {Count} sources",
       mapping.LocalLibraryName,
          mapping.RemoteLibrarySources?.Count ?? 0);

        if (mapping.RemoteLibrarySources == null || mapping.RemoteLibrarySources.Count == 0)
    {
       _logger.LogWarning("[Federation] Mapping {Name} has no remote sources", mapping.LocalLibraryName);
  return new SyncResult { Success = false, Message = "No remote sources configured" };
          }

            // Get or create the virtual library folder
            var virtualFolder = await GetOrCreateVirtualFolderAsync(mapping);
            if (virtualFolder == null)
        {
    return new SyncResult { Success = false, Message = "Failed to create virtual folder" };
     }

            int totalItems = 0;

     // Sync from each remote source
        foreach (var source in mapping.RemoteLibrarySources)
            {
                try
           {
           var server = config.RemoteServers?.FirstOrDefault(s => s.Id == source.ServerId);
  if (server == null || !server.Enabled)
           {
   _logger.LogWarning("[Federation] Server {ServerId} not found or disabled", source.ServerId);
       continue;
  }

      _logger.LogInformation("[Federation] Syncing from {ServerName} → {LibraryName}",
            source.ServerName, source.RemoteLibraryName);

  using var client = new RemoteServerClient(server, _loggerFactory.CreateLogger<RemoteServerClient>());

               // Get items from remote library
     var items = await client.GetItemsAsync(
     userId: server.UserId,
  mediaType: mapping.MediaType,
        parentId: source.RemoteLibraryId,
 limit: 100, // Start with first 100 items
      cancellationToken: cancellationToken);

      _logger.LogInformation("[Federation] Retrieved {Count} items from {ServerName}", 
              items.Count, source.ServerName);

       // Create federated items
          foreach (var remoteItem in items)
            {
          try
   {
             await CreateFederatedItemAsync(remoteItem, server, virtualFolder, cancellationToken);
           totalItems++;
       }
      catch (Exception ex)
     {
     _logger.LogError(ex, "[Federation] Error creating federated item: {ItemName}", remoteItem.Name);
      }
            }
                }
    catch (Exception ex)
            {
          _logger.LogError(ex, "[Federation] Error syncing from source: {SourceName}", source.RemoteLibraryName);
   }
            }

            _logger.LogInformation("[Federation] Sync complete for {Name}: {Count} items", 
         mapping.LocalLibraryName, totalItems);

            return new SyncResult
            {
 Success = true,
           ItemCount = totalItems,
    Message = $"Synced {totalItems} items"
   };
        }

    /// <summary>
        /// Gets or creates a virtual folder for a mapping.
  /// </summary>
    private async Task<Folder?> GetOrCreateVirtualFolderAsync(LibraryMapping mapping)
    {
   _logger.LogInformation("[Federation] Creating placeholder folder for: {Name}", mapping.LocalLibraryName);

   // Note: With file-based approach, we don't actually create virtual folders
   // We create .strm files that Jellyfin scans as a regular library
   // This method creates a placeholder folder object that's not registered with Jellyfin

var folder = new Folder
   {
  Name = mapping.LocalLibraryName,
   DateCreated = DateTime.UtcNow,
     DateModified = DateTime.UtcNow
            };

   _logger.LogInformation("[Federation] Placeholder folder created (not registered with Jellyfin)");
    return folder;
    }

        /// <summary>
        /// Creates a federated item (placeholder) that points to a remote item.
        /// </summary>
 private async Task<BaseItem?> CreateFederatedItemAsync(
       BaseItemDto remoteItem,
     RemoteServer server,
    Folder parentFolder,
CancellationToken cancellationToken)
{
    BaseItem localItem;

   // Create appropriate item type based on remote item
  if (remoteItem.Type == Jellyfin.Data.Enums.BaseItemKind.Movie)
{
     localItem = new Movie
       {
Name = remoteItem.Name,
   Overview = remoteItem.Overview,
       CommunityRating = remoteItem.CommunityRating,
   OfficialRating = remoteItem.OfficialRating,
      PremiereDate = remoteItem.PremiereDate,
   ProductionYear = remoteItem.ProductionYear,
   RunTimeTicks = remoteItem.RunTimeTicks
      };
 }
  else if (remoteItem.Type == Jellyfin.Data.Enums.BaseItemKind.Series)
  {
     localItem = new Series
        {
     Name = remoteItem.Name,
           Overview = remoteItem.Overview,
      CommunityRating = remoteItem.CommunityRating,
        OfficialRating = remoteItem.OfficialRating,
   PremiereDate = remoteItem.PremiereDate,
  ProductionYear = remoteItem.ProductionYear
    };
    }
   else
   {
   // For other types, use Movie as a generic placeholder
     localItem = new Movie
{
     Name = remoteItem.Name,
           Overview = remoteItem.Overview
      };
        }

   // Set common properties
     localItem.Id = Guid.NewGuid();
        localItem.DateCreated = DateTime.UtcNow;
  localItem.DateModified = DateTime.UtcNow;

     // Store federation metadata using ProviderIds dictionary
    if (localItem.ProviderIds == null)
   {
     localItem.ProviderIds = new Dictionary<string, string>();
   }
       localItem.ProviderIds["FederationSource"] = server.Id;
         localItem.ProviderIds["FederationRemoteId"] = remoteItem.Id.ToString();
          localItem.ProviderIds["FederationServerUrl"] = server.Url;

      // TODO: Add the item to the library manager
  // _libraryManager.CreateItem(localItem, parentFolder);

      _logger.LogDebug("[Federation] Created federated item: {Name} (Remote: {RemoteId})", 
    localItem.Name, remoteItem.Id);

 return localItem;
   }
    }

    /// <summary>
    /// Result of a sync operation.
    /// </summary>
    public class SyncResult
    {
  /// <summary>
        /// Gets or sets a value indicating whether the sync was successful.
        /// </summary>
        public bool Success { get; set; }

      /// <summary>
     /// Gets or sets the number of items synced.
        /// </summary>
public int ItemCount { get; set; }

        /// <summary>
        /// Gets or sets a message describing the result.
  /// </summary>
        public string Message { get; set; } = string.Empty;
  
        /// <summary>
        /// Gets or sets the operation ID for progress tracking.
     /// </summary>
  public string? OperationId { get; set; }
    }
}
