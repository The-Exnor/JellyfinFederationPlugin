using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Configuration;

namespace JellyfinFederationPlugin
{
    public class FederationService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public FederationService(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<List<MediaItem>> GetFederatedMediaAsync(string libraryName)
        {
            var mediaItems = new List<MediaItem>();
            var config = Plugin.Instance.Configuration;

            foreach (var server in config.FederatedServers)
            {
                try
                {
                    // Include port in URL construction if specified
                    var baseUrl = server.Port > 0 ? $"{server.ServerUrl}:{server.Port}" : server.ServerUrl;
                    var url = $"{baseUrl}/Libraries/{libraryName}/Items?api_key={server.ApiKey}";
                    _logger.LogInformation($"Fetching media from federated server: {server.ServerUrl}");

                    var response = await _httpClient.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        var items = JsonSerializer.Deserialize<List<MediaItem>>(content);
                        mediaItems.AddRange(items);
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to fetch media from {server.ServerUrl}. Status Code: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error fetching media from {server.ServerUrl}: {ex.Message}");
                }
            }

            return mediaItems;
        }

        public class MediaItem
        {
            public string Name { get; set; }
            public string Id { get; set; }
            public string MediaType { get; set; }
        }
    }
}
