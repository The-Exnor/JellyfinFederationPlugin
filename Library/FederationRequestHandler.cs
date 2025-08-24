using JellyfinFederationPlugin.Configuration;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using BaseItemDto = MediaBrowser.Model.Dto.BaseItemDto;
using System.Collections.Generic;
using System;
using System.Text.Json;
using System.Net;
using System.Threading;

namespace JellyfinFederationPlugin.Library
{
    public class FederationRequestHandler
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _rateLimitSemaphore;

        public FederationRequestHandler(ILogger logger, HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
            _rateLimitSemaphore = new SemaphoreSlim(5, 5); // Limit concurrent requests
            
            // Configure HTTP client with timeouts
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task<List<BaseItemDto>> GetFederatedLibrary(PluginConfiguration.FederatedServer server)
        {
            await _rateLimitSemaphore.WaitAsync();
            
            try
            {
                return await GetFederatedLibraryWithRetry(server, maxRetries: 3);
            }
            finally
            {
                _rateLimitSemaphore.Release();
            }
        }

        private async Task<List<BaseItemDto>> GetFederatedLibraryWithRetry(PluginConfiguration.FederatedServer server, int maxRetries)
        {
            var retryCount = 0;
            var backoffDelay = TimeSpan.FromSeconds(1);

            while (retryCount <= maxRetries)
            {
                try
                {
                    // Include port in URL construction if specified
                    var baseUrl = server.Port > 0 ? $"{server.ServerUrl}:{server.Port}" : server.ServerUrl;
                    var requestUrl = $"{baseUrl}/Items";
                    _logger.LogInformation($"Requesting library from federated server {requestUrl} (attempt {retryCount + 1})");

                    // Validate API key
                    if (string.IsNullOrWhiteSpace(server.ApiKey))
                    {
                        _logger.LogError($"API key is missing for server {server.ServerUrl}");
                        return new List<BaseItemDto>();
                    }

                    // Test connectivity first
                    if (retryCount == 0 && !await TestServerConnectivity(baseUrl))
                    {
                        _logger.LogWarning($"Server {server.ServerUrl} appears to be unreachable");
                        return new List<BaseItemDto>();
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                    
                    // Use proper Jellyfin authentication header
                    request.Headers.Add("X-Emby-Authorization", 
                        $"MediaBrowser Client=\"JellyfinFederation\", Device=\"FederationPlugin\", DeviceId=\"jellyfin-federation\", Version=\"1.0.0\", Token=\"{server.ApiKey}\"");
                    
                    // Add additional headers for better compatibility
                    request.Headers.Add("Accept", "application/json");
                    request.Headers.Add("User-Agent", "Jellyfin-Federation-Plugin/1.0.0");

                    var response = await _httpClient.SendAsync(request);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonString = await response.Content.ReadAsStringAsync();
                        
                        if (string.IsNullOrWhiteSpace(jsonString))
                        {
                            _logger.LogWarning($"Empty response from server {server.ServerUrl}");
                            return new List<BaseItemDto>();
                        }

                        var result = JsonSerializer.Deserialize<List<BaseItemDto>>(jsonString, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        _logger.LogInformation($"Successfully fetched {result?.Count ?? 0} items from {server.ServerUrl}");
                        return result ?? new List<BaseItemDto>();
                    }
                    else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        _logger.LogError($"Authentication failed for server {server.ServerUrl}. Check API key.");
                        return new List<BaseItemDto>();
                    }
                    else if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.LogWarning($"Items endpoint not found on server {server.ServerUrl}. Server may be incompatible.");
                        return new List<BaseItemDto>();
                    }
                    else
                    {
                        _logger.LogWarning($"Request failed for server {server.ServerUrl}. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                        
                        // Retry on server errors
                        if ((int)response.StatusCode >= 500 && retryCount < maxRetries)
                        {
                            throw new HttpRequestException($"Server error: {response.StatusCode}");
                        }
                        
                        return new List<BaseItemDto>();
                    }
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    _logger.LogWarning($"Timeout occurred for server {server.ServerUrl} (attempt {retryCount + 1})");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning($"HTTP request failed for server {server.ServerUrl} (attempt {retryCount + 1}): {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Unexpected error requesting library from server {server.ServerUrl} (attempt {retryCount + 1})");
                }

                retryCount++;
                
                if (retryCount <= maxRetries)
                {
                    _logger.LogInformation($"Retrying request to {server.ServerUrl} in {backoffDelay.TotalSeconds} seconds...");
                    await Task.Delay(backoffDelay);
                    backoffDelay = TimeSpan.FromSeconds(Math.Min(backoffDelay.TotalSeconds * 2, 30)); // Exponential backoff, max 30 seconds
                }
            }

            _logger.LogError($"Failed to fetch library from server {server.ServerUrl} after {maxRetries + 1} attempts");
            return new List<BaseItemDto>();
        }

        private async Task<bool> TestServerConnectivity(string baseUrl)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/System/Info/Public");
                request.Headers.Add("User-Agent", "Jellyfin-Federation-Plugin/1.0.0");
                
                using var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> ValidateServerConnection(PluginConfiguration.FederatedServer server)
        {
            try
            {
                var baseUrl = server.Port > 0 ? $"{server.ServerUrl}:{server.Port}" : server.ServerUrl;
                
                // Test public endpoint first
                if (!await TestServerConnectivity(baseUrl))
                {
                    return false;
                }

                // Test authenticated endpoint
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/System/Info");
                request.Headers.Add("X-Emby-Authorization", 
                    $"MediaBrowser Client=\"JellyfinFederation\", Device=\"FederationPlugin\", DeviceId=\"jellyfin-federation\", Version=\"1.0.0\", Token=\"{server.ApiKey}\"");
                
                using var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to validate connection to server {server.ServerUrl}");
                return false;
            }
        }
    }
}
