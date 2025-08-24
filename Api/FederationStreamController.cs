using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using JellyfinFederationPlugin.Streaming;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using System.IO;

namespace JellyfinFederationPlugin.Api
{
    [ApiController]
    [Route("Plugins/JellyfinFederationPlugin/Stream")]
    public class FederationStreamController : ControllerBase
    {
        private readonly FederationStreamingService _streamingService;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<FederationStreamController> _logger;

        public FederationStreamController(
            FederationStreamingService streamingService,
            ILibraryManager libraryManager,
            ILogger<FederationStreamController> logger)
        {
            _streamingService = streamingService;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        [HttpGet("{itemId}")]
        public async Task<IActionResult> GetStream(string itemId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    return BadRequest("Item ID is required");
                }

                // Find the federated item
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound("Item not found");
                }

                // Check if it's a federated item
                if (!item.Path?.StartsWith("federation://") == true)
                {
                    return BadRequest("Item is not a federated item");
                }

                // Get media source
                var mediaSource = await _streamingService.GetMediaSourceAsync(item);
                if (mediaSource == null)
                {
                    return NotFound("Media source not available");
                }

                // Get the stream
                var stream = await _streamingService.GetStreamAsync(mediaSource.Id, null);
                
                // Return the stream with appropriate content type
                var contentType = GetContentType(item.Path);
                return new FileStreamResult(stream, contentType)
                {
                    EnableRangeProcessing = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error streaming federated item: {itemId}");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("{itemId}/transcode")]
        public async Task<IActionResult> GetTranscodedStream(
            string itemId,
            string container = "mp4",
            string videoCodec = "h264",
            string audioCodec = "aac",
            int? videoBitrate = null,
            int? audioBitrate = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    return BadRequest("Item ID is required");
                }

                // Find the federated item
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound("Item not found");
                }

                // Check if it's a federated item
                if (!item.Path?.StartsWith("federation://") == true)
                {
                    return BadRequest("Item is not a federated item");
                }

                // Get media source
                var mediaSource = await _streamingService.GetMediaSourceAsync(item);
                if (mediaSource == null)
                {
                    return NotFound("Media source not available");
                }

                // Get the transcoded stream
                var stream = await _streamingService.GetTranscodedStreamAsync(
                    mediaSource.Id,
                    container,
                    videoCodec,
                    audioCodec,
                    videoBitrate,
                    audioBitrate);
                
                // Return the stream with appropriate content type
                var contentType = GetContentTypeForContainer(container);
                return new FileStreamResult(stream, contentType)
                {
                    EnableRangeProcessing = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error transcoding federated item: {itemId}");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("info/{itemId}")]
        public async Task<IActionResult> GetStreamInfo(string itemId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    return BadRequest("Item ID is required");
                }

                // Find the federated item
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound("Item not found");
                }

                // Check if it's a federated item
                if (!item.Path?.StartsWith("federation://") == true)
                {
                    return BadRequest("Item is not a federated item");
                }

                // Get media source
                var mediaSource = await _streamingService.GetMediaSourceAsync(item);
                if (mediaSource == null)
                {
                    return NotFound("Media source not available");
                }

                // Return stream information
                return Ok(new
                {
                    itemId = itemId,
                    mediaSourceId = mediaSource.Id,
                    name = mediaSource.Name,
                    protocol = mediaSource.Protocol.ToString(),
                    isRemote = mediaSource.IsRemote,
                    supportsDirectStream = mediaSource.SupportsDirectStream,
                    supportsTranscoding = mediaSource.SupportsTranscoding,
                    path = item.Path
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting stream info for federated item: {itemId}");
                return StatusCode(500, "Internal server error");
            }
        }

        private string GetContentType(string path)
        {
            var extension = Path.GetExtension(path)?.ToLowerInvariant();
            return extension switch
            {
                ".mp4" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".wmv" => "video/x-ms-wmv",
                ".flv" => "video/x-flv",
                ".webm" => "video/webm",
                ".m4v" => "video/x-m4v",
                ".3gp" => "video/3gpp",
                ".ts" => "video/mp2t",
                _ => "application/octet-stream"
            };
        }

        private string GetContentTypeForContainer(string container)
        {
            return container?.ToLowerInvariant() switch
            {
                "mp4" => "video/mp4",
                "mkv" => "video/x-matroska",
                "webm" => "video/webm",
                "avi" => "video/x-msvideo",
                "mov" => "video/quicktime",
                "ts" => "video/mp2t",
                _ => "application/octet-stream"
            };
        }
    }
}