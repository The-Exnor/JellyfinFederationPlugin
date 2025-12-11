using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Provides media sources for federated content.
    /// </summary>
    public class FederationMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<FederationMediaSourceProvider> _logger;
        private readonly ILibraryManager _libraryManager;
   private readonly ILoggerFactory _loggerFactory;
        private FederationLibraryManager? _federationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationMediaSourceProvider"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
     public FederationMediaSourceProvider(
ILogger<FederationMediaSourceProvider> logger,
       ILibraryManager libraryManager,
         ILoggerFactory loggerFactory)
        {
     _logger = logger;
        _libraryManager = libraryManager;
            _loggerFactory = loggerFactory;
     }

    /// <inheritdoc />
      public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
      if (item == null || !IsFederatedItem(item))
 {
        return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
   }

       _logger.LogInformation("Getting media sources for federated item: {ItemName}", item.Name);
    return GetFederatedMediaSourcesAsync(item, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
        throw new NotImplementedException("Live stream opening is not supported for federated content");
        }

     private async Task<IEnumerable<MediaSourceInfo>> GetFederatedMediaSourcesAsync(BaseItem item, CancellationToken cancellationToken)
      {
      try
 {
     if (!FederationLibraryManager.TryParseFederationPath(item.Path, out var serverId, out var itemId))
                {
                    _logger.LogWarning("Failed to parse federation path: {Path}", item.Path);
  return Enumerable.Empty<MediaSourceInfo>();
         }

        _federationManager ??= new FederationLibraryManager(_libraryManager, _loggerFactory.CreateLogger<FederationLibraryManager>(), _loggerFactory);

            var client = _federationManager.GetClient(serverId);
  if (client == null)
       {
        _logger.LogWarning("Client not found for server ID: {ServerId}", serverId);
          return Enumerable.Empty<MediaSourceInfo>();
 }

   var playbackInfo = await client.GetPlaybackInfoAsync(itemId, cancellationToken: cancellationToken);
      if (playbackInfo?.MediaSources == null || playbackInfo.MediaSources.Count == 0)
    {
      _logger.LogWarning("No media sources found for item {ItemId}", itemId);
   return Enumerable.Empty<MediaSourceInfo>();
                }

      return playbackInfo.MediaSources.Select(ms => EnhanceMediaSource(ms, item, serverId, itemId)).ToList();
            }
            catch (Exception ex)
     {
         _logger.LogError(ex, "Error getting media sources for federated item");
       return Enumerable.Empty<MediaSourceInfo>();
            }
    }

private MediaSourceInfo EnhanceMediaSource(MediaSourceInfo mediaSource, BaseItem item, string serverId, string itemId)
   {
            var enhanced = new MediaSourceInfo
       {
     Id = mediaSource.Id ?? Guid.NewGuid().ToString(),
            Name = $"Federation: {mediaSource.Name ?? "Default"}",
           Path = $"federation://{serverId}/{itemId}/{mediaSource.Id}",
    Protocol = MediaProtocol.Http,
    Container = mediaSource.Container,
                Size = mediaSource.Size,
          Bitrate = mediaSource.Bitrate,
  VideoType = mediaSource.VideoType,
       RunTimeTicks = mediaSource.RunTimeTicks ?? item.RunTimeTicks,
        IsRemote = true,
         RequiresOpening = false,
     RequiresClosing = false,
       SupportsDirectStream = true,
                SupportsDirectPlay = true,
           SupportsTranscoding = true,
      Type = MediaSourceType.Default
 };

            if (mediaSource.MediaStreams != null && mediaSource.MediaStreams.Count > 0)
            {
              enhanced.MediaStreams = new List<MediaStream>(mediaSource.MediaStreams);
    }

            enhanced.RequiredHttpHeaders = new Dictionary<string, string>
            {
   ["X-Federation-Server"] = serverId,
        ["X-Federation-ItemId"] = itemId
            };

            return enhanced;
        }

        private bool IsFederatedItem(BaseItem item)
        {
    return !string.IsNullOrEmpty(item.Path) &&
          item.Path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase);
   }
    }
}