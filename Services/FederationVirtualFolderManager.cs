using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Manages virtual folders for federated content.
    /// </summary>
    public class FederationVirtualFolderManager
    {
  private readonly ILibraryManager _libraryManager;
        private readonly ILogger<FederationVirtualFolderManager> _logger;

        /// <summary>
    /// Initializes a new instance of the <see cref="FederationVirtualFolderManager"/> class.
        /// </summary>
    /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="logger">Logger instance.</param>
    public FederationVirtualFolderManager(
     ILibraryManager libraryManager,
     ILogger<FederationVirtualFolderManager> logger)
        {
  _libraryManager = libraryManager;
            _logger = logger;
   }

/// <summary>
        /// Creates or updates virtual folders for all configured library mappings.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
  public async Task SyncVirtualFoldersAsync()
        {
 var config = Plugin.Instance?.Configuration;
 if (config?.LibraryMappings == null)
      {
   _logger.LogWarning("No library mappings configured");
 return;
        }

         _logger.LogInformation("Syncing virtual folders for {Count} library mappings", config.LibraryMappings.Count);

   foreach (var mapping in config.LibraryMappings.Where(m => m.Enabled))
  {
  try
{
    await EnsureVirtualFolderExistsAsync(mapping);
  }
      catch (Exception ex)
        {
        _logger.LogError(ex, "Error ensuring virtual folder exists for mapping: {MappingName}", mapping.LocalLibraryName);
   }
   }

       // Remove virtual folders for disabled mappings
    await RemoveDisabledVirtualFoldersAsync();
        }

 /// <summary>
        /// Ensures a virtual folder exists for a library mapping.
        /// </summary>
        /// <param name="mapping">The library mapping.</param>
  /// <returns>A task representing the asynchronous operation.</returns>
   private async Task EnsureVirtualFolderExistsAsync(LibraryMapping mapping)
        {
            _logger.LogInformation("Ensuring virtual folder exists for: {LibraryName}", mapping.LocalLibraryName);

 // Check if the folder already exists
            var virtualFolders = _libraryManager.GetVirtualFolders();

            var existingVirtualFolder = virtualFolders?.FirstOrDefault(vf =>
   string.Equals(vf.Name, mapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));

     if (existingVirtualFolder != null)
 {
   _logger.LogDebug("Virtual folder already exists: {LibraryName}", mapping.LocalLibraryName);
    return;
     }

         // Create new virtual folder
            try
            {
              var libraryOptions = new LibraryOptions
  {
        PathInfos = new[]
  {
            new MediaPathInfo
       {
           Path = $"federation://{mapping.LocalLibraryName}"
        }
       }
       };

    await _libraryManager.AddVirtualFolder(
   mapping.LocalLibraryName,
     GetCollectionType(mapping.MediaType),
   libraryOptions,
  true);

    _logger.LogInformation("Created virtual folder: {LibraryName}", mapping.LocalLibraryName);
 }
       catch (Exception ex)
   {
   _logger.LogError(ex, "Error creating virtual folder: {LibraryName}", mapping.LocalLibraryName);
         throw;
      }
        }

    /// <summary>
   /// Removes virtual folders for disabled library mappings.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RemoveDisabledVirtualFoldersAsync()
      {
var config = Plugin.Instance?.Configuration;
            if (config?.LibraryMappings == null)
     {
       return;
            }

    var virtualFolders = _libraryManager.GetVirtualFolders();
  if (virtualFolders == null)
       {
       return;
   }

          var disabledMappings = config.LibraryMappings.Where(m => !m.Enabled).ToList();

     foreach (var disabledMapping in disabledMappings)
  {
     var existingVirtualFolder = virtualFolders.FirstOrDefault(vf =>
          string.Equals(vf.Name, disabledMapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));

 if (existingVirtualFolder != null)
       {
      try
    {
  await _libraryManager.RemoveVirtualFolder(existingVirtualFolder.Name, true);
       _logger.LogInformation("Removed virtual folder for disabled mapping: {LibraryName}", disabledMapping.LocalLibraryName);
     }
       catch (Exception ex)
       {
      _logger.LogError(ex, "Error removing virtual folder: {LibraryName}", disabledMapping.LocalLibraryName);
}
      }
   }
    }

        /// <summary>
        /// Gets the collection type for a media type.
 /// </summary>
     /// <param name="mediaType">The media type.</param>
   /// <returns>The collection type.</returns>
        private CollectionTypeOptions? GetCollectionType(string mediaType)
        {
     // Return null to let Jellyfin auto-detect
    // This is simpler and more compatible
   return null;
        }

        /// <summary>
     /// Triggers a library scan for a specific virtual folder.
   /// </summary>
        /// <param name="libraryName">The library name.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
   public async Task TriggerLibraryScanAsync(string libraryName)
     {
     try
  {
     var virtualFolders = _libraryManager.GetVirtualFolders();
     var folder = virtualFolders?.FirstOrDefault(vf =>
        string.Equals(vf.Name, libraryName, StringComparison.OrdinalIgnoreCase));

            if (folder != null)
           {
          _logger.LogInformation("Triggering library scan for: {LibraryName}", libraryName);

// Get the root folder and validate children
 var rootFolder = _libraryManager.RootFolder;
      if (rootFolder != null)
       {
await rootFolder.ValidateChildren(new Progress<double>(), default);
      _logger.LogInformation("Library scan completed for: {LibraryName}", libraryName);
    }
     }
 }
         catch (Exception ex)
   {
     _logger.LogError(ex, "Error triggering library scan for: {LibraryName}", libraryName);
     }
        }
    }
}