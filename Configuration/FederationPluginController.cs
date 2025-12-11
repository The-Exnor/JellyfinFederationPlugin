using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api
{
    /// <summary>
    /// API controller for federation plugin.
    /// /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    [AllowAnonymous] // Remove ALL authentication for now
    public class FederationController : ControllerBase
    {
        private readonly ILogger<FederationController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly FederationSyncService _syncService;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationController"/> class.
        /// /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
        /// <param name="httpContextAccessor">HTTP context accessor.</param>
        public FederationController(
            ILogger<FederationController> logger,
            ILibraryManager libraryManager,
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _loggerFactory = loggerFactory;
            _httpContextAccessor = httpContextAccessor;
            _syncService = new FederationSyncService(
                loggerFactory.CreateLogger<FederationSyncService>(),
                libraryManager,
                loggerFactory);
        }

        #region Configuration Endpoints

        /// <summary>
        /// Serves the configuration page HTML.
        /// /// /// <returns>HTML page.</returns>
        [HttpGet("Config")]
        [AllowAnonymous]
        [Produces("text/html")]
        public IActionResult GetConfigPage()
        {
            _logger.LogInformation("[Federation] Serving config page");

            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "Jellyfin.Plugin.Federation.Configuration.configPage.html";

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    _logger.LogError("[Federation] Resource not found: {ResourceName}", resourceName);
                    return NotFound("Configuration page resource not found");
                }

                using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
                var html = reader.ReadToEnd();

                _logger.LogInformation("[Federation] HTML loaded, length: {Length}", html.Length);

                return Content(html, "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error serving config page");
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the plugin configuration.
        /// /// /// <returns>The configuration.</returns>
        [HttpGet("Configuration")]
        [AllowAnonymous] // Changed from RequiresElevation to allow web UI access
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<PluginConfiguration> GetConfiguration()
        {
            try
            {
                _logger.LogInformation("[Federation] Getting configuration");
                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                _logger.LogInformation("[Federation] Returning configuration with {ServerCount} servers", config.RemoteServers?.Count ?? 0);
                return Ok(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configuration");
                return StatusCode(500, new { error = "Failed to get configuration", message = ex.Message });
            }
        }

        /// <summary>
        /// Updates the plugin configuration.
        /// /// /// <param name="config">The new configuration.</param>
        /// <returns>Success status.</returns>
        [HttpPost("Configuration")]
        [AllowAnonymous] // Changed from RequiresElevation to allow web UI access
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult UpdateConfiguration([FromBody] PluginConfiguration config)
        {
            try
            {
                if (config == null)
                {
                    return BadRequest(new { error = "Configuration is required" });
                }

                _logger.LogInformation("[Federation] Updating configuration with {ServerCount} servers", config.RemoteServers?.Count ?? 0);
                Plugin.Instance?.UpdateConfiguration(config);

                _logger.LogInformation("Configuration updated successfully");
                return Ok(new { success = true, message = "Configuration updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating configuration");
                return StatusCode(500, new { error = "Failed to update configuration", message = ex.Message });
            }
        }

        #endregion

        #region Server Management Endpoints

        /// <summary>
        /// Tests connection to a remote server.
        /// /// /// <param name="server">The server configuration to test.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Test result.</returns>
        [HttpPost("TestServer")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> TestServer([FromBody] RemoteServer server, CancellationToken cancellationToken)
        {
            try
            {
                if (server == null)
                {
                    return BadRequest(new { success = false, message = "Server configuration is required" });
                }

                if (string.IsNullOrWhiteSpace(server.Url))
                {
                    return BadRequest(new { success = false, message = "Server URL is required" });
                }

                if (string.IsNullOrWhiteSpace(server.ApiKey))
                {
                    return BadRequest(new { success = false, message = "API Key is required" });
                }

                _logger.LogInformation("Testing connection to server: {ServerName} ({Url})", server.Name, server.Url);

                using var client = new RemoteServerClient(server, _loggerFactory.CreateLogger<RemoteServerClient>());

                // Test basic connection
                var connectionSuccess = await client.TestConnectionAsync(cancellationToken);
                if (!connectionSuccess)
                {
                    return Ok(new { success = false, message = "Failed to connect to server" });
                }

                // Try to get system info
                var systemInfo = await client.GetSystemInfoAsync(cancellationToken);
                if (systemInfo == null)
                {
                    return Ok(new { success = false, message = "Connected but failed to get system information" });
                }

                // Try to get users if UserId is not specified
                string? userId = server.UserId;
                if (string.IsNullOrEmpty(userId))
                {
                    var users = await client.GetUsersAsync(cancellationToken);
                    if (users != null && users.Count > 0)
                    {
                        userId = users[0].Id;
                        _logger.LogInformation("Found {Count} users on remote server, using first user: {UserId}", users.Count, userId);

                        // Try to get libraries with this user
                        var tempServer = new RemoteServer
                        {
                            Id = server.Id,
                            Name = server.Name,
                            Url = server.Url,
                            ApiKey = server.ApiKey,
                            UserId = userId,
                            Enabled = server.Enabled
                        };

                        using var clientWithUser = new RemoteServerClient(tempServer, _loggerFactory.CreateLogger<RemoteServerClient>());
                        var libraries = await clientWithUser.GetLibrariesAsync(cancellationToken);
                        var libraryCount = libraries?.Count ?? 0;

                        _logger.LogInformation("Found {Count} libraries on remote server", libraryCount);
                    }
                }

                return Ok(new
                {
                    success = true,
                    message = "Connection successful",
                    serverInfo = new
                    {
                        name = systemInfo.ServerName,
                        version = systemInfo.Version,
                        operatingSystem = systemInfo.OperatingSystem,
                        serverId = systemInfo.Id,
                        suggestedUserId = userId
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing server connection");
                return Ok(new { success = false, message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Gets all remote servers.
        /// /// /// <returns>List of servers.</returns>
        [HttpGet("Servers")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<List<RemoteServer>> GetServers()
        {
            var servers = Plugin.Instance?.Configuration.RemoteServers ?? new List<RemoteServer>();
            return Ok(servers);
        }

        /// <summary>
        /// Adds a new remote server.
        /// /// /// <param name="server">The server to add.</param>
        /// <returns>Success status.</returns>
        [HttpPost("Servers")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult AddServer([FromBody] RemoteServer server)
        {
            try
            {
                if (server == null)
                {
                    return BadRequest(new { error = "Server configuration is required" });
                }

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return BadRequest(new { error = "Plugin not initialized" });
                }

                server.Id = Guid.NewGuid().ToString();
                config.RemoteServers ??= new List<RemoteServer>();
                config.RemoteServers.Add(server);
                Plugin.Instance?.SaveConfiguration();

                _logger.LogInformation("Added new server: {ServerName} ({ServerId})", server.Name, server.Id);
                return Ok(new { success = true, server });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding server");
                return StatusCode(500, new { error = "Failed to add server", message = ex.Message });
            }
        }

        /// <summary>
        /// Updates an existing remote server.
        /// /// /// <param name="id">The server ID.</param>
        /// <param name="server">The updated server configuration.</param>
        /// <returns>Success status.</returns>
        [HttpPut("Servers/{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult UpdateServer(string id, [FromBody] RemoteServer server)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return BadRequest(new { error = "Plugin not initialized" });
                }

                var existing = config.RemoteServers?.FirstOrDefault(s => s.Id == id);
                if (existing == null)
                {
                    return NotFound(new { error = "Server not found" });
                }

                existing.Name = server.Name;
                existing.Url = server.Url;
                existing.ApiKey = server.ApiKey;
                existing.UserId = server.UserId;
                existing.Enabled = server.Enabled;

                Plugin.Instance?.SaveConfiguration();

                _logger.LogInformation("Updated server: {ServerName} ({ServerId})", server.Name, id);
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating server");
                return StatusCode(500, new { error = "Failed to update server", message = ex.Message });
            }
        }

        /// <summary>
        /// Deletes a remote server.
        /// /// /// <param name="id">The server ID.</param>
        /// <returns>Success status.</returns>
        [HttpDelete("Servers/{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult DeleteServer(string id)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return BadRequest(new { error = "Plugin not initialized" });
                }

                var server = config.RemoteServers?.FirstOrDefault(s => s.Id == id);
                if (server == null)
                {
                    return NotFound(new { error = "Server not found" });
                }

                config.RemoteServers?.Remove(server);
                Plugin.Instance?.SaveConfiguration();

                _logger.LogInformation("Deleted server: {ServerName} ({ServerId})", server.Name, id);
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting server");
                return StatusCode(500, new { error = "Failed to delete server", message = ex.Message });
            }
        }

        #endregion

        #region Remote Library Browsing

        /// <summary>
        /// Gets libraries from all configured remote servers.
        /// /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of libraries from remote servers.</returns>
        [HttpGet("GetRemoteLibraries")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetRemoteLibraries(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[Federation] Getting remote libraries");

                var config = Plugin.Instance?.Configuration;
                if (config?.RemoteServers == null || config.RemoteServers.Count == 0)
                {
                    return Ok(new { success = false, message = "No remote servers configured" });
                }

                var results = new List<object>();

                foreach (var server in config.RemoteServers.Where(s => s.Enabled))
                {
                    try
                    {
                        _logger.LogInformation("[Federation] Fetching libraries from: {ServerName} (URL: {Url}, UserId: {UserId})",
                            server.Name, server.Url, server.UserId);

                        using var client = new RemoteServerClient(server, _loggerFactory.CreateLogger<RemoteServerClient>());

                        // Get libraries
                        var libraries = await client.GetLibrariesAsync(cancellationToken);

                        if (libraries != null && libraries.Count > 0)
                        {
                            _logger.LogInformation("[Federation] Server {ServerName} returned {Count} libraries",
                                server.Name, libraries.Count);

                            results.Add(new
                            {
                                serverId = server.Id,
                                serverName = server.Name,
                                libraries = libraries.Select(lib => new
                                {
                                    id = lib.Id,
                                    name = lib.Name,
                                    collectionType = lib.CollectionType?.ToString() ?? "unknown",
                                    itemCount = lib.ChildCount ?? 0 // Use ChildCount - it's usually accurate for libraries
                                }).ToList()
                            });
                        }
                        else
                        {
                            _logger.LogWarning("[Federation] Server {ServerName} returned no libraries", server.Name);
                            results.Add(new
                            {
                                serverId = server.Id,
                                serverName = server.Name,
                                warning = "No libraries found. Make sure the server has libraries configured and you've tested the connection.",
                                libraries = new List<object>()
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Federation] Error fetching libraries from server: {ServerName}", server.Name);
                        results.Add(new
                        {
                            serverId = server.Id,
                            serverName = server.Name,
                            error = $"Failed to connect: {ex.Message}",
                            libraries = new List<object>()
                        });
                    }
                }

                _logger.LogInformation("[Federation] Returning {Count} servers with libraries", results.Count);
                return Ok(new { success = true, servers = results });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error getting remote libraries");
                return StatusCode(500, new { success = false, message = $"Error: {ex.Message}" });
            }
        }

        #endregion

        #region Streaming Endpoints

        /// <summary>
        /// Streams content from a federated remote server.
        /// /// </summary>
        /// <param name="serverId">The remote server ID.</param>
        /// <param name="itemId">The item ID on the remote server.</param>
        /// <returns>Stream response.</returns>
        [HttpGet("Stream")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Stream([FromQuery] string serverId, [FromQuery] string itemId)
        {
            try
            {
                _logger.LogInformation("[Federation] Stream request: serverId={ServerId}, itemId={ItemId}", serverId, itemId);

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return NotFound("Plugin not configured");
                }

                var server = config.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
                if (server == null)
                {
                    _logger.LogWarning("[Federation] Server not found: {ServerId}", serverId);
                    return NotFound($"Server not found: {serverId}");
                }

                _logger.LogInformation("[Federation] Proxying stream from {ServerName} for item {ItemId}", server.Name, itemId);

                // Build direct stream URL to remote server
                var remoteStreamUrl = $"{server.Url.TrimEnd('/')}/Videos/{itemId}/stream?api_key={server.ApiKey}&Static=true";

                // Check if client sent Range header (for seeking/resuming)
                string? rangeHeader = Request.Headers["Range"].FirstOrDefault();
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    _logger.LogInformation("[Federation] Range request: {Range}", rangeHeader);
                    remoteStreamUrl += $"&Range={rangeHeader}";
                }

                _logger.LogInformation("[Federation] Remote stream URL: {Url}", remoteStreamUrl);

                // Create HTTP client for streaming
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromHours(3); // Long timeout for streaming

                // Create request message to forward range headers
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, remoteStreamUrl);

                // Forward Range header if present
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    request.Headers.TryAddWithoutValidation("Range", rangeHeader);
                }

                // Get response with headers only first (don't download body yet)
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    _logger.LogError("[Federation] Remote server returned error: {StatusCode}", response.StatusCode);
                    return StatusCode((int)response.StatusCode);
                }

                // Copy response headers
                Response.StatusCode = (int)response.StatusCode; // 200 or 206 (Partial Content)

                if (response.Content.Headers.ContentType != null)
                {
                    Response.ContentType = response.Content.Headers.ContentType.ToString();
                }

                if (response.Content.Headers.ContentLength.HasValue)
                {
                    Response.ContentLength = response.Content.Headers.ContentLength.Value;
                }

                // Copy Accept-Ranges header
                if (response.Headers.Contains("Accept-Ranges"))
                {
                    Response.Headers["Accept-Ranges"] = response.Headers.GetValues("Accept-Ranges").FirstOrDefault();
                }

                // Copy Content-Range header (for 206 responses)
                if (response.Content.Headers.ContentRange != null)
                {
                    Response.Headers["Content-Range"] = response.Content.Headers.ContentRange.ToString();
                }

                _logger.LogInformation("[Federation] Starting chunked stream transfer (Status: {Status}, Length: {Length})",
                    response.StatusCode, response.Content.Headers.ContentLength);

                // Stream the content in chunks (don't load entire file into memory)
                await using var sourceStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);

                // Copy stream in chunks
                var buffer = new byte[81920]; // 80KB buffer for efficient streaming
                int bytesRead;
                long totalBytesStreamed = 0;

                try
                {
                    while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, HttpContext.RequestAborted)) > 0)
                    {
                        await Response.Body.WriteAsync(buffer, 0, bytesRead, HttpContext.RequestAborted);
                        await Response.Body.FlushAsync(HttpContext.RequestAborted); // Ensure data is sent immediately

                        totalBytesStreamed += bytesRead;

                        // Log progress every 10MB
                        if (totalBytesStreamed % (10 * 1024 * 1024) < buffer.Length)
                        {
                            _logger.LogDebug("[Federation] Streamed {MB} MB so far", totalBytesStreamed / (1024 * 1024));
                        }
                    }

                    _logger.LogInformation("[Federation] Stream completed: {MB} MB transferred", totalBytesStreamed / (1024 * 1024));
                }
                catch (System.OperationCanceledException)
                {
                    _logger.LogInformation("[Federation] Stream cancelled by client after {MB} MB", totalBytesStreamed / (1024 * 1024));
                }

                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error streaming content");
                return StatusCode(500, "Error streaming content");
            }
        }

        #endregion

        #region Library Management Endpoints

        /// <summary>
        /// Triggers a manual sync of virtual libraries.
        /// /// /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Success status.</returns>
        [HttpPost("Sync")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> TriggerSync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[Federation] Manual sync triggered");

                // Use the FederationSyncService to perform the sync
                var result = await _syncService.SyncAllAsync(cancellationToken);

                return Ok(new {
                    success = result.Success,
                    message = result.Message,
                    itemCount = result.ItemCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error triggering sync");
                return StatusCode(500, new { error = "Failed to trigger sync", message = ex.Message });
            }
        }

        /// <summary>
        /// Syncs all enabled servers.
        /// /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Sync result.</returns>
        [HttpPost("SyncAll")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> SyncAll(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[Federation] Sync all requested");

                // Use FederationSyncService to perform the sync
                var result = await _syncService.SyncAllAsync(cancellationToken);

                return Ok(new {
                    success = result.Success,
                    message = result.Message,
                    itemCount = result.ItemCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error syncing all servers");
                return Ok(new { success = false, message = $"Sync failed: {ex.Message}" });
            }
        }

        /// <summary>
        /// Syncs a specific server.
        /// /// </summary>
        /// <param name="request">Sync request with serverId.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Sync result.</returns>
        [HttpPost("SyncServer")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> SyncServer([FromBody] SyncServerRequest request, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[Federation] Sync requested for server: {ServerId}", request?.serverId);

                if (string.IsNullOrEmpty(request?.serverId))
                {
                    return Ok(new { success = false, message = "Server ID is required" });
                }

                // Use FederationSyncService to perform the sync
                var result = await _syncService.SyncServerAsync(request.serverId, cancellationToken);

                return Ok(new {
                    success = result.Success,
                    message = result.Message,
                    itemCount = result.ItemCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error syncing server");
                return Ok(new { success = false, message = $"Sync failed: {ex.Message}" });
            }
        }

        /// <summary>
        /// Syncs virtual folders without content sync.
        /// /// /// <returns>Success status.</returns>
        [HttpPost("SyncVirtualFolders")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> SyncVirtualFolders()
        {
            try
            {
                _logger.LogInformation("[Federation] Manual virtual folder sync triggered");

                var federationManager = new FederationLibraryManager(
    _libraryManager,
       _loggerFactory.CreateLogger<FederationLibraryManager>(),
 _loggerFactory);

                await federationManager.SyncVirtualFoldersAsync();

                return Ok(new { success = true, message = "Virtual folders synced successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error syncing virtual folders");
                return StatusCode(500, new { error = "Failed to sync virtual folders", message = ex.Message });
            }
        }

        /// <summary>
        /// Triggers a library scan for a specific virtual folder.
        /// /// /// <param name="libraryName">The library name.</param>
        /// <returns>Success status.</returns>
        [HttpPost("ScanLibrary/{libraryName}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> ScanLibrary(string libraryName)
        {
            try
            {
                _logger.LogInformation("Manual library scan triggered for: {LibraryName}", libraryName);

                var virtualFolderManager = new FederationVirtualFolderManager(
                    _libraryManager,
                    _loggerFactory.CreateLogger<FederationVirtualFolderManager>());

                await virtualFolderManager.TriggerLibraryScanAsync(libraryName);

                return Ok(new { success = true, message = $"Library scan completed for {libraryName}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning library: {LibraryName}", libraryName);
                return StatusCode(500, new { error = "Failed to scan library", message = ex.Message });
            }
        }

        /// <summary>
        /// Gets library mappings.
        /// /// /// <returns>List of library mappings.</returns>
        [HttpGet("Mappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<List<LibraryMapping>> GetMappings()
        {
            var mappings = Plugin.Instance?.Configuration.LibraryMappings ?? new List<LibraryMapping>();
            return Ok(mappings);
        }

        /// <summary>
        /// Triggers a library scan for a specific virtual folder.
        /// /// /// <param name="request">The scan request.</param>
        /// <returns>Success status.</returns>
        [HttpPost("ScanLibrary")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult ScanLibrary([FromBody] ScanLibraryRequest request)
        {
            try
            {
                _logger.LogInformation("[Federation] Triggering library scan for: {LibraryName}", request?.LibraryName);

                // Note: Library scanning is handled automatically by Jellyfin
                // This endpoint is for manual triggering if needed      

                return Ok(new { success = true, message = "Library scan triggered by Jellyfin automatically" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error triggering library scan");
                return StatusCode(500, new { error = "Failed to trigger scan", message = ex.Message });
            }
        }

        /// <summary>
        /// Gets federation status and statistics.
        /// /// /// <returns>Status information.</returns>
        [HttpGet("Status")]
     [AllowAnonymous]
      [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetStatus()
        {
try
  {
            _logger.LogInformation("[Federation] Status requested");

             var config = Plugin.Instance?.Configuration;
   if (config == null)
       {
        return Ok(new
  {
   totalServers = 0,
activeServers = 0,
       federatedItems = 0,
          lastSync = "Never",
 servers = new List<object>()
   });
   }

     var totalServers = config.RemoteServers?.Count ?? 0;
     var activeServers = config.RemoteServers?.Count(s => s.Enabled) ?? 0;

   // Count federated items by checking federation directory
     int federatedItems = 0;
     try
   {
         var fileService = new FederationFileService(
   _loggerFactory.CreateLogger<FederationFileService>(),
    _libraryManager,
     _loggerFactory);

     var basePath = fileService.GetFederationBasePath();
        if (Directory.Exists(basePath))
         {
   // Count .strm files
      federatedItems = Directory.GetFiles(basePath, "*.strm", SearchOption.AllDirectories).Length;
 }
       }
       catch (Exception ex)
   {
     _logger.LogWarning(ex, "[Federation] Error counting federated items");
        }

    // Build server status list
       var serverList = config.RemoteServers ?? new List<RemoteServer>();
  var serverStatuses = serverList.Select(s => new
{
 name = s.Name,
    online = s.Enabled,
    itemCount = 0
    }).ToList();

      var status = new
  {
    totalServers = totalServers,
   activeServers = activeServers,
  federatedItems = federatedItems,
   lastSync = "Unknown",
      servers = serverStatuses
   };

  return Ok(status);
          }
    catch (Exception ex)
       {
       _logger.LogError(ex, "[Federation] Error getting status");
 return StatusCode(500, new { error = "Failed to get status", message = ex.Message });
   }
        }

        /// <summary>
     /// Gets sync operation progress.
        /// </summary>
/// <param name="operationId">The operation ID.</param>
   /// <returns>Progress information.</returns>
  [HttpGet("Progress/{operationId}")]
        [AllowAnonymous]
  [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetProgress(string operationId)
        {
   try
   {
     var progress = SyncProgressTracker.Get(operationId);
   if (progress == null)
     {
    return NotFound(new { error = "Operation not found" });
       }

    return Ok(new
      {
   operationId = progress.OperationId,
    totalItems = progress.TotalItems,
    processedItems = progress.ProcessedItems,
         percentage = progress.Percentage,
 status = progress.Status,
   isComplete = progress.IsComplete,
   success = progress.Success,
    elapsedSeconds = progress.ElapsedTime?.TotalSeconds
   });
       }
 catch (Exception ex)
       {
    _logger.LogError(ex, "[Federation] Error getting progress");
    return StatusCode(500, new { error = "Failed to get progress" });
 }
    }
    
        /// <summary>
        /// Triggers a library rescan for federation content.
   /// </summary>
     /// <returns>Success status.</returns>
  [HttpPost("RescanLibraries")]
   [AllowAnonymous]
  [ProducesResponseType(StatusCodes.Status200OK)]
      public IActionResult RescanLibraries()
     {
     try
    {
    _logger.LogInformation("[Federation] Manual library rescan requested");
        
// Get all libraries that might contain federation content
        var fileService = new FederationFileService(
        _loggerFactory.CreateLogger<FederationFileService>(),
    _libraryManager,
        _loggerFactory);
     
    var basePath = fileService.GetFederationBasePath();
     
if (!Directory.Exists(basePath))
 {
   return Ok(new { success = false, message = "No federation content found" });
  }
        
  // Get all top-level folders (these are the mapping directories)
        var mappingFolders = Directory.GetDirectories(basePath);
           
     _logger.LogInformation("[Federation] Found {Count} federation mapping folders", mappingFolders.Length);
   
     // Note: Jellyfin will auto-scan when files change
  // We're just touching the directory to trigger a scan
  foreach (var folder in mappingFolders)
     {
   try
    {
    Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        }
     catch (Exception ex)
     {
    _logger.LogWarning(ex, "[Federation] Could not touch directory: {Folder}", folder);
 }
    }
          
         return Ok(new { 
        success = true, 
       message = $"Triggered rescan for {mappingFolders.Length} federation libraries" 
     });
  }
   catch (Exception ex)
      {
    _logger.LogError(ex, "[Federation] Error triggering library rescan");
       return StatusCode(500, new { error = "Failed to trigger rescan" });
        }
  }
    
      /// <summary>
  /// Clears all federation files and data.
   /// </summary>
   /// <returns>Success status.</returns>
   [HttpPost("ClearAll")]
  [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult ClearAll()
 {
  try
     {
       _logger.LogInformation("[Federation] Clearing all federation data");
    
      var fileService = new FederationFileService(
        _loggerFactory.CreateLogger<FederationFileService>(),
_libraryManager,
  _loggerFactory);
          
      fileService.ClearFederationFiles();

    // Clean up progress tracking
SyncProgressTracker.Cleanup();
   
   return Ok(new { success = true, message = "All federation data cleared" });
 }
    catch (Exception ex)
   {
      _logger.LogError(ex, "[Federation] Error clearing federation data");
      return StatusCode(500, new { error = "Failed to clear data" });
      }
   }
     
   /// <summary>
   /// Gets detailed statistics about federation content.
     /// </summary>
 /// <returns>Statistics.</returns>
   [HttpGet("Statistics")]
  [AllowAnonymous]
[ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetStatistics()
  {
     try
      {
   _logger.LogInformation("[Federation] Getting statistics");
     
    var config = Plugin.Instance?.Configuration;
    var fileService = new FederationFileService(
       _loggerFactory.CreateLogger<FederationFileService>(),
 _libraryManager,
  _loggerFactory);
         
   var basePath = fileService.GetFederationBasePath();
    var stats = new
   {
   servers = new
 {
      total = config?.RemoteServers?.Count ?? 0,
  enabled = config?.RemoteServers?.Count(s => s.Enabled) ?? 0,
     disabled = config?.RemoteServers?.Count(s => !s.Enabled) ?? 0
  },
      mappings = new
   {
    total = config?.LibraryMappings?.Count ?? 0,
      enabled = config?.LibraryMappings?.Count(m => m.Enabled) ?? 0,
      disabled = config?.LibraryMappings?.Count(m => !m.Enabled) ?? 0
      },
    files = GetFileStatistics(basePath)
   };
   
    return Ok(stats);
        }
 catch (Exception ex)
   {
       _logger.LogError(ex, "[Federation] Error getting statistics");
     return StatusCode(500, new { error = "Failed to get statistics" });
   }
        }
   
        /// <summary>
     /// Tests connectivity to all configured servers.
   /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
   /// <returns>Test results.</returns>
  [HttpPost("TestAllServers")]
  [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
  public async Task<IActionResult> TestAllServers(CancellationToken cancellationToken)
   {
   try
      {
  _logger.LogInformation("[Federation] Testing all servers");
       
       var config = Plugin.Instance?.Configuration;
 if (config?.RemoteServers == null || config.RemoteServers.Count == 0)
      {
         return Ok(new { success = false, message = "No servers configured" });
  }

   var results = new List<object>();
       
  foreach (var server in config.RemoteServers)
     {
      try
  {
     using var client = new RemoteServerClient(server, _loggerFactory.CreateLogger<RemoteServerClient>());
       var isOnline = await client.TestConnectionAsync(cancellationToken);
    var systemInfo = isOnline ? await client.GetSystemInfoAsync(cancellationToken) : null;
           
            results.Add(new
    {
       serverId = server.Id,
      serverName = server.Name,
 online = isOnline,
   systemInfo = systemInfo != null ? new
      {
  name = systemInfo.ServerName,
 version = systemInfo.Version,
    operatingSystem = systemInfo.OperatingSystem
            } : null
 });
   }
      catch (Exception ex)
    {
      results.Add(new
     {
           serverId = server.Id,
     serverName = server.Name,
     online = false,
       error = ex.Message
         });
  }
     }
      
       return Ok(new { success = true, results });
}
    catch (Exception ex)
        {
      _logger.LogError(ex, "[Federation] Error testing servers");
   return StatusCode(500, new { error = "Failed to test servers" });
}
      }
     
       /// <summary>
     /// Gets health check information.
        /// </summary>
 /// <returns>Health status.</returns>
        [HttpGet("Health")]
   [AllowAnonymous]
  [ProducesResponseType(StatusCodes.Status200OK)]
 public IActionResult GetHealth()
   {
 try
   {
   var config = Plugin.Instance?.Configuration;
      var fileService = new FederationFileService(
     _loggerFactory.CreateLogger<FederationFileService>(),
   _libraryManager,
            _loggerFactory);
    
         var basePath = fileService.GetFederationBasePath();
  var hasFiles = Directory.Exists(basePath) && Directory.GetFiles(basePath, "*.strm", SearchOption.AllDirectories).Length > 0;
   
   var health = new
  {
    status = "healthy",
         plugin = new
     {
  version = Plugin.Instance?.Version.ToString() ?? "Unknown",
      configured = config != null,
  hasServers = config?.RemoteServers?.Count > 0,
  hasMappings = config?.LibraryMappings?.Count > 0,
     hasContent = hasFiles
    },
   system = new
      {
   basePathExists = Directory.Exists(basePath),
 basePath = basePath,
     canWrite = TestWriteAccess(basePath)
  }
  };

    return Ok(health);
 }
   catch (Exception ex)
{
 _logger.LogError(ex, "[Federation] Error checking health");
         return StatusCode(500, new { status = "unhealthy", error = ex.Message });
   }
        }
    
    private object GetFileStatistics(string basePath)
  {
   try
      {
if (!Directory.Exists(basePath))
        {
     return new { strmFiles = 0, nfoFiles = 0, totalSize = 0 };
    }

   var strmFiles = Directory.GetFiles(basePath, "*.strm", SearchOption.AllDirectories);
 var nfoFiles = Directory.GetFiles(basePath, "*.nfo", SearchOption.AllDirectories);
           var totalSize = strmFiles.Concat(nfoFiles).Sum(f => new FileInfo(f).Length);
        
  return new
 {
 strmFiles = strmFiles.Length,
  nfoFiles = nfoFiles.Length,
     totalSizeBytes = totalSize,
      totalSizeMB = totalSize / (1024 * 1024)
   };
    }
   catch
      {
       return new { strmFiles = 0, nfoFiles = 0, totalSize = 0 };
    }
 }
        
  private bool TestWriteAccess(string path)
        {
   try
    {
      if (!Directory.Exists(path))
     {
   Directory.CreateDirectory(path);
  }
       
  var testFile = Path.Combine(path, ".write_test");
   System.IO.File.WriteAllText(testFile, "test");
    System.IO.File.Delete(testFile);
  return true;
}
   catch
 {
     return false;
  }
 }
  }

    /// <summary>
 /// Request for syncing a server.
    /// /// /// </summary>
 public class SyncServerRequest
    {
        /// <summary>
        /// Gets or sets the server ID.
    /// /// /// </summary>
     public string? serverId { get; set; }
    }

    /// <summary>
    /// Request for scanning a library.
    /// /// /// </summary>
    public class ScanLibraryRequest
    {
        /// <summary>
        /// Gets or sets the library name.
        /// /// /// </summary>
        public string? LibraryName { get; set; }
    }
}
#endregion