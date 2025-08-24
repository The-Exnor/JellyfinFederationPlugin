using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Linq;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Library;
using JellyfinFederationPlugin.Configuration;

namespace JellyfinFederationPlugin.Api
{
    public class PlaybackProxyService
    {
        private readonly FederationRequestHandler _requestHandler;
        private readonly ILogger _logger;

        public PlaybackProxyService(FederationRequestHandler requestHandler, ILogger logger)
        {
            _requestHandler = requestHandler;
            _logger = logger;
        }

        public async Task<StreamInfo> GetFederatedStream(BaseItem item)
        {
            if (item.Path.StartsWith("remote://"))
            {
                var mediaIdWithServerInfo = item.Path.Replace("remote://", "");

                // Parse the remote path to extract server and media ID
                // Format: "serverUrl|mediaId"
                var parts = mediaIdWithServerInfo.Split('|');
                if (parts.Length < 2)
                {
                    _logger.LogError($"Invalid remote path format: {item.Path}. Expected format: remote://serverUrl|mediaId");
                    return null;
                }

                var serverUrl = parts[0];
                var mediaId = parts[1];

                if (Guid.TryParse(mediaId, out var mediaGuid))
                {
                    // Find the federated server from configuration
                    var config = Plugin.Instance.Configuration;
                    var federatedServer = config.FederatedServers.FirstOrDefault(s => s.ServerUrl == serverUrl);

                    if (federatedServer == null)
                    {
                        _logger.LogError($"Federated server not found in configuration: {serverUrl}");
                        return null;
                    }

                    var federatedLibraryItems = await _requestHandler.GetFederatedLibrary(federatedServer);

                    if (federatedLibraryItems != null)
                    {
                        var federatedItem = federatedLibraryItems.FirstOrDefault(x => x.Id == mediaGuid);
                        if (federatedItem != null)
                        {
                            // Include port in URL construction if specified
                            var baseUrl = federatedServer.Port > 0 ? $"{federatedServer.ServerUrl}:{federatedServer.Port}" : federatedServer.ServerUrl;
                            var streamUrl = $"{baseUrl}/Items/{federatedItem.Id}/Playback";

                            return new StreamInfo
                            {
                                MediaSource = new MediaSourceInfo
                                {
                                    Path = streamUrl,
                                    Protocol = MediaProtocol.Http
                                },
                                DeviceProfile = new DeviceProfile
                                {
                                    Name = "Federated Device",
                                    MaxStreamingBitrate = 100000000
                                }
                            };
                        }
                        else
                        {
                            _logger.LogWarning($"Federated item not found with ID: {mediaGuid} on server: {serverUrl}");
                        }
                    }
                }
                else
                {
                    _logger.LogError($"Failed to parse mediaId '{mediaId}' as a Guid.");
                }
            }

            return null;
        }
    }
}
