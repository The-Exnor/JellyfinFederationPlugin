using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Stream handler for federation content.
    /// </summary>
    public class FederationStreamHandler
    {
 private readonly ILogger<FederationStreamHandler> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient _httpClient;
      private FederationLibraryManager? _federationManager;

   /// <summary>
        /// Initializes a new instance of the <see cref="FederationStreamHandler"/> class.
        /// </summary>
 /// <param name="logger">Logger instance.</param>
 /// <param name="libraryManager">Library manager instance.</param>
     /// <param name="loggerFactory">Logger factory instance.</param>
        public FederationStreamHandler(
  ILogger<FederationStreamHandler> logger,
    ILibraryManager libraryManager,
    ILoggerFactory loggerFactory)
        {
  _logger = logger;
       _libraryManager = libraryManager;
    _loggerFactory = loggerFactory;
         _httpClient = new HttpClient
         {
       Timeout = TimeSpan.FromMinutes(30) // Long timeout for streaming
    };
   }

        /// <summary>
   /// Handles a stream request for federated content.
   /// </summary>
     /// <param name="federationPath">The federation path.</param>
      /// <param name="httpRequest">The HTTP request.</param>
        /// <param name="httpResponse">The HTTP response.</param>
 /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task representing the asynchronous operation.</returns>
        public async Task HandleStreamRequestAsync(
  string federationPath,
    HttpRequest httpRequest,
       HttpResponse httpResponse,
       CancellationToken cancellationToken)
        {
   try
       {
     _logger.LogInformation("Handling stream request for: {Path}", federationPath);

    // Parse federation path
        if (!FederationLibraryManager.TryParseFederationPath(federationPath, out var serverId, out var itemId))
{
       _logger.LogError("Invalid federation path: {Path}", federationPath);
       httpResponse.StatusCode = 400;
      return;
  }

 // Get federation manager
   _federationManager ??= new FederationLibraryManager(_libraryManager, _loggerFactory.CreateLogger<FederationLibraryManager>(), _loggerFactory);

      // Get client for server
    var client = _federationManager.GetClient(serverId);
      if (client == null)
          {
  _logger.LogError("Client not found for server ID: {ServerId}", serverId);
  httpResponse.StatusCode = 404;
             return;
      }

       // Get stream URL from remote server
  var streamUrl = await BuildStreamUrlAsync(client, itemId, httpRequest, cancellationToken);
        if (string.IsNullOrEmpty(streamUrl))
       {
        _logger.LogError("Failed to build stream URL for item {ItemId}", itemId);
     httpResponse.StatusCode = 500;
      return;
}

       _logger.LogInformation("Proxying stream from: {Url}", streamUrl);

   // Create request to remote server
   using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);

  // Copy relevant headers from original request
      CopyRequestHeaders(httpRequest, request);

            // Get response from remote server
using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
  _logger.LogError("Remote server returned error: {StatusCode}", response.StatusCode);
   httpResponse.StatusCode = (int)response.StatusCode;
   return;
      }

       // Copy response headers
       CopyResponseHeaders(response, httpResponse);

            // Stream the content
    await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await remoteStream.CopyToAsync(httpResponse.Body, cancellationToken);
       }
    catch (OperationCanceledException)
  {
    _logger.LogInformation("Stream request cancelled");
          }
      catch (Exception ex)
       {
      _logger.LogError(ex, "Error handling stream request");
    httpResponse.StatusCode = 500;
}
      }

     /// <summary>
   /// Builds a stream URL for a remote item.
/// </summary>
        /// <param name="client">The remote server client.</param>
   /// <param name="itemId">The item ID.</param>
/// <param name="httpRequest">The HTTP request.</param>
   /// <param name="cancellationToken">Cancellation token.</param>
 /// <returns>The stream URL.</returns>
   private async Task<string> BuildStreamUrlAsync(
       RemoteServerClient client,
       string itemId,
        HttpRequest httpRequest,
     CancellationToken cancellationToken)
 {
     try
   {
 _logger.LogDebug("[Federation] Building stream URL for item: {ItemId}", itemId);
  
   // Get playback info to find media sources
    var playbackInfo = await client.GetPlaybackInfoAsync(itemId, cancellationToken: cancellationToken);
      if (playbackInfo?.MediaSources == null || playbackInfo.MediaSources.Count == 0)
    {
    _logger.LogWarning("[Federation] No media sources found for item: {ItemId}", itemId);
       return string.Empty;
 }

       // Use first media source
            var mediaSource = playbackInfo.MediaSources[0];
     var mediaSourceId = mediaSource.Id ?? itemId;
            
            _logger.LogDebug("[Federation] Using media source: {MediaSourceId}", mediaSourceId);

  // Check for transcoding parameters in query string
       var queryParams = new List<string>();

      // Add basic parameters
       queryParams.Add($"MediaSourceId={mediaSourceId}");
    queryParams.Add($"api_key={client.ServerConfig.ApiKey}");

       // Check if transcoding is requested
    if (httpRequest.Query.ContainsKey("MaxStreamingBitrate"))
         {
       queryParams.Add($"MaxStreamingBitrate={httpRequest.Query["MaxStreamingBitrate"]}");
    _logger.LogDebug("[Federation] Adding MaxStreamingBitrate parameter");
   }

   if (httpRequest.Query.ContainsKey("VideoCodec"))
 {
    queryParams.Add($"VideoCodec={httpRequest.Query["VideoCodec"]}");
    _logger.LogDebug("[Federation] Adding VideoCodec parameter");
      }

            if (httpRequest.Query.ContainsKey("AudioCodec"))
 {
      queryParams.Add($"AudioCodec={httpRequest.Query["AudioCodec"]}");
     _logger.LogDebug("[Federation] Adding AudioCodec parameter");
   }

   // Build stream URL
       var baseUrl = client.ServerConfig.Url.TrimEnd('/');
   var streamUrl = $"{baseUrl}/Videos/{itemId}/stream?{string.Join("&", queryParams)}";

       _logger.LogInformation("[Federation] Built stream URL: {Url}", streamUrl);
       return streamUrl;
    }
 catch (Exception ex)
{
            _logger.LogError(ex, "[Federation] Error building stream URL for item: {ItemId}", itemId);
    return string.Empty;
  }
   }

     /// <summary>
   /// Copies relevant headers from the original request to the remote request.
  /// </summary>
   /// <param name="source">Source HTTP request.</param>
   /// <param name="target">Target HTTP request message.</param>
 private void CopyRequestHeaders(HttpRequest source, HttpRequestMessage target)
        {
   // Copy range header for seeking support
       if (source.Headers.ContainsKey("Range"))
     {
     target.Headers.TryAddWithoutValidation("Range", source.Headers["Range"].ToString());
       }

          // Copy user agent
            if (source.Headers.ContainsKey("User-Agent"))
  {
target.Headers.TryAddWithoutValidation("User-Agent", source.Headers["User-Agent"].ToString());
        }
 }

        /// <summary>
        /// Copies relevant headers from the remote response to the client response.
        /// </summary>
        /// <param name="source">Source HTTP response message.</param>
      /// <param name="target">Target HTTP response.</param>
        private void CopyResponseHeaders(HttpResponseMessage source, HttpResponse target)
        {
  target.StatusCode = (int)source.StatusCode;

     // Copy content headers
    if (source.Content.Headers.ContentType != null)
     {
   target.ContentType = source.Content.Headers.ContentType.ToString();
 }

     if (source.Content.Headers.ContentLength != null)
            {
            target.ContentLength = source.Content.Headers.ContentLength;
     }

      if (source.Content.Headers.ContentRange != null)
            {
      target.Headers.Add("Content-Range", source.Content.Headers.ContentRange.ToString());
}

    // Copy accept-ranges header
 if (source.Headers.Contains("Accept-Ranges"))
      {
   target.Headers.Add("Accept-Ranges", string.Join(", ", source.Headers.GetValues("Accept-Ranges")));
     }
      }
    }
}