using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Resolvers
{
    /// <summary>
    /// Resolves federation:// paths to virtual Jellyfin items.
    /// </summary>
    public class FederationItemResolver : IItemResolver
    {
        private readonly ILogger<FederationItemResolver> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationItemResolver"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
        public FederationItemResolver(
      ILogger<FederationItemResolver> logger,
         ILibraryManager libraryManager,
ILoggerFactory loggerFactory)
        {
    _logger = logger;
_libraryManager = libraryManager;
            _loggerFactory = loggerFactory;
   }

   /// <inheritdoc />
        public ResolverPriority Priority => ResolverPriority.Second;

        /// <inheritdoc />
 public BaseItem? ResolvePath(ItemResolveArgs args)
        {
if (args == null || string.IsNullOrEmpty(args.Path))
  {
   return null;
      }

   // Only handle federation:// paths
  if (!args.Path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
 {
     return null;
}

_logger.LogDebug("[Federation] Resolving federation path: {Path}", args.Path);

    try
     {
  // Parse the federation path to validate it
if (!Services.FederationLibraryManager.TryParseFederationPath(args.Path, out var serverId, out var itemId))
       {
   _logger.LogWarning("[Federation] Failed to parse federation path: {Path}", args.Path);
     return null;
  }

  // Get the federated item from the library manager
    var federationManager = new Services.FederationLibraryManager(
   _libraryManager,
       _loggerFactory.CreateLogger<Services.FederationLibraryManager>(),
  _loggerFactory);

 // Try to get the item by path
var item = federationManager.GetFederatedItemByPath(args.Path);
   if (item != null)
    {
    _logger.LogDebug("[Federation] Resolved federated item: {ItemName} ({ItemType})", 
         item.Name, item.GetType().Name);
        return item;
 }

   _logger.LogDebug("[Federation] Federated item not found in cache for path: {Path}", args.Path);
         
      // TODO: If item not in cache, could fetch from remote server on-demand
      // For now, return null
   
            return null;
            }
 catch (Exception ex)
      {
   _logger.LogError(ex, "[Federation] Error resolving federation path: {Path}", args.Path);
  return null;
   }
   }
    }
}