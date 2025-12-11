using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Providers
{
    /// <summary>
    /// Provides images for federated content.
    /// </summary>
    public class FederationImageProvider : IRemoteImageProvider
    {
 private readonly ILogger<FederationImageProvider> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly HttpClient _httpClient;

        /// <summary>
/// Initializes a new instance of the <see cref="FederationImageProvider"/> class.
    /// </summary>
      /// <param name="logger">Logger instance.</param>
 /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
 public FederationImageProvider(
   ILogger<FederationImageProvider> logger,
      ILibraryManager libraryManager,
   ILoggerFactory loggerFactory)
        {
      _logger = logger;
         _libraryManager = libraryManager;
    _loggerFactory = loggerFactory;
   _httpClient = new HttpClient();
  }

        /// <inheritdoc />
    public string Name => "Federation";

   /// <inheritdoc />
   public bool Supports(BaseItem item)
    {
  return IsFederatedItem(item);
  }

   /// <inheritdoc />
   public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
 return new[]
  {
       ImageType.Primary,
      ImageType.Backdrop,
        ImageType.Banner,
    ImageType.Thumb,
         ImageType.Logo,
     ImageType.Art
  };
        }

   /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
     {
     if (!IsFederatedItem(item))
            {
      return Enumerable.Empty<RemoteImageInfo>();
     }

   try
      {
         if (!Services.FederationLibraryManager.TryParseFederationPath(item.Path, out var serverId, out var itemId))
  {
           _logger.LogWarning("Failed to parse federation path for images: {Path}", item.Path);
    return Enumerable.Empty<RemoteImageInfo>();
  }

    var federationManager = new Services.FederationLibraryManager(
    _libraryManager,
     _loggerFactory.CreateLogger<Services.FederationLibraryManager>(),
 _loggerFactory);

     var client = federationManager.GetClient(serverId);
            if (client == null)
     {
_logger.LogWarning("Client not found for server ID: {ServerId}", serverId);
 return Enumerable.Empty<RemoteImageInfo>();
   }

    // Get the item from remote server to get its images
   var remoteItem = await client.GetItemAsync(itemId, cancellationToken: cancellationToken);
  if (remoteItem == null)
   {
       return Enumerable.Empty<RemoteImageInfo>();
   }

     var images = new List<RemoteImageInfo>();

     // Add primary image
      if (remoteItem.ImageTags?.ContainsKey(ImageType.Primary) == true)
       {
         images.Add(new RemoteImageInfo
   {
            Url = BuildImageUrl(client.ServerConfig.Url, itemId, ImageType.Primary, remoteItem.ImageTags[ImageType.Primary]),
   Type = ImageType.Primary,
   ProviderName = Name
            });
       }

   // Add backdrop images
   if (remoteItem.BackdropImageTags != null && remoteItem.BackdropImageTags.Any())
      {
   for (int i = 0; i < remoteItem.BackdropImageTags.Count(); i++)
{
              images.Add(new RemoteImageInfo
      {
Url = BuildBackdropImageUrl(client.ServerConfig.Url, itemId, i),
  Type = ImageType.Backdrop,
       ProviderName = Name
         });
       }
   }

  // Add other image types
      var otherImageTypes = new[] { ImageType.Banner, ImageType.Thumb, ImageType.Logo, ImageType.Art };
    foreach (var imageType in otherImageTypes)
     {
     if (remoteItem.ImageTags?.ContainsKey(imageType) == true)
     {
     images.Add(new RemoteImageInfo
  {
   Url = BuildImageUrl(client.ServerConfig.Url, itemId, imageType, remoteItem.ImageTags[imageType]),
   Type = imageType,
  ProviderName = Name
        });
          }
  }

         return images;
          }
 catch (Exception ex)
 {
    _logger.LogError(ex, "Error getting images for federated item: {ItemName}", item.Name);
      return Enumerable.Empty<RemoteImageInfo>();
  }
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
     {
    return _httpClient.GetAsync(url, cancellationToken);
        }

        /// <summary>
     /// Builds an image URL for a remote item.
        /// </summary>
/// <param name="serverUrl">The server URL.</param>
  /// <param name="itemId">The item ID.</param>
   /// <param name="imageType">The image type.</param>
        /// <param name="imageTag">The image tag.</param>
   /// <returns>The image URL.</returns>
        private string BuildImageUrl(string serverUrl, string itemId, ImageType imageType, string imageTag)
        {
return $"{serverUrl.TrimEnd('/')}/Items/{itemId}/Images/{imageType}?tag={imageTag}";
}

        /// <summary>
    /// Builds a backdrop image URL for a remote item.
   /// </summary>
 /// <param name="serverUrl">The server URL.</param>
        /// <param name="itemId">The item ID.</param>
 /// <param name="index">The backdrop index.</param>
        /// <returns>The image URL.</returns>
    private string BuildBackdropImageUrl(string serverUrl, string itemId, int index)
     {
      return $"{serverUrl.TrimEnd('/')}/Items/{itemId}/Images/Backdrop/{index}";
        }

        /// <summary>
 /// Checks if an item is a federated item.
        /// </summary>
   /// <param name="item">The item to check.</param>
        /// <returns>True if federated.</returns>
        private bool IsFederatedItem(BaseItem item)
        {
        return !string.IsNullOrEmpty(item.Path) &&
    item.Path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase);
   }
    }
}