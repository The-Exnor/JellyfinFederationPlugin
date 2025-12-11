using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Client for communicating with remote Jellyfin servers.
    /// </summary>
    public class RemoteServerClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly RemoteServer _server;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteServerClient"/> class.
     /// </summary>
        /// <param name="server">The remote server configuration.</param>
        /// <param name="logger">Logger instance.</param>
   public RemoteServerClient(RemoteServer server, ILogger logger)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
       _logger = logger ?? throw new ArgumentNullException(nameof(logger));

      _httpClient = new HttpClient
{
                BaseAddress = new Uri(server.Url.TrimEnd('/')),
                Timeout = TimeSpan.FromMinutes(5) // Increased from 30 seconds to handle large requests
            };

     // Set authentication header - Jellyfin uses X-Emby-Token
         _httpClient.DefaultRequestHeaders.Add("X-Emby-Token", server.ApiKey);
        }

        /// <summary>
     /// Gets the server configuration.
        /// </summary>
        public RemoteServer ServerConfig => _server;

        /// <summary>
        /// Gets items from the remote server.
        /// </summary>
        /// <param name="userId">The user ID on the remote server.</param>
        /// <param name="mediaType">The media type to filter (Movie, Series, etc.).</param>
        /// <param name="parentId">Parent folder ID (optional).</param>
        /// <param name="startIndex">Start index for paging.</param>
        /// <param name="limit">Number of items to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
   /// <returns>List of items.</returns>
        public async Task<List<BaseItemDto>> GetItemsAsync(
        string? userId = null,
  string? mediaType = null,
       string? parentId = null,
    int? startIndex = null,
       int? limit = null,
CancellationToken cancellationToken = default)
   {
  try
   {
 var userIdToUse = userId ?? _server.UserId;
        if (string.IsNullOrEmpty(userIdToUse))
 {
   _logger.LogWarning("No user ID specified for remote server {ServerName}", _server.Name);
    return new List<BaseItemDto>();
       }

          // Build query parameters
           var queryParams = new List<string>
 {
  "Recursive=true",
       "Fields=BasicSyncInfo,Path,MediaSources,Overview,Genres,Tags,Studios,People",
    "EnableImageTypes=Primary,Backdrop,Banner,Thumb"
    };

     if (!string.IsNullOrEmpty(mediaType))
    {
       queryParams.Add($"IncludeItemTypes={mediaType}");
  }

       if (!string.IsNullOrEmpty(parentId))
   {
  queryParams.Add($"ParentId={parentId}");
     }

       if (startIndex.HasValue)
    {
        queryParams.Add($"StartIndex={startIndex.Value}");
}

        if (limit.HasValue)
   {
     queryParams.Add($"Limit={limit.Value}");
     }

        var url = $"/Users/{userIdToUse}/Items?{string.Join("&", queryParams)}";

  _logger.LogDebug("[Federation] Requesting items from {ServerName}: {Url}", _server.Name, url);

    var response = await _httpClient.GetAsync(url, cancellationToken);
     response.EnsureSuccessStatusCode();

     var content = await response.Content.ReadAsStringAsync(cancellationToken);
   
 // Parse manually to handle string IDs and capture TotalRecordCount
      using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
    
     // Get TotalRecordCount if available
  if (root.TryGetProperty("TotalRecordCount", out var totalProp))
            {
       var totalCount = totalProp.GetInt32();
       _logger.LogInformation("[Federation] TotalRecordCount from API: {Count}", totalCount);
    }
            
            if (!root.TryGetProperty("Items", out var itemsElement))
  {
  _logger.LogWarning("[Federation] No Items property in response from {ServerName}", _server.Name);
    return new List<BaseItemDto>();
   }

            var items = new List<BaseItemDto>();
      
  foreach (var itemElement in itemsElement.EnumerateArray())
            {
    try
     {
     var item = new BaseItemDto();
           
    // Handle ID as string -> GUID
        if (itemElement.TryGetProperty("Id", out var idProp))
      {
    var idStr = idProp.GetString();
    item.Id = Guid.TryParse(idStr, out var guid) ? guid : Guid.NewGuid();
     }
      
    // Handle Name
     if (itemElement.TryGetProperty("Name", out var nameProp))
  {
          item.Name = nameProp.GetString();
        }
               
    // Handle Type
          if (itemElement.TryGetProperty("Type", out var typeProp))
     {
        var typeStr = typeProp.GetString();
            if (!string.IsNullOrEmpty(typeStr) && 
      Enum.TryParse<Jellyfin.Data.Enums.BaseItemKind>(typeStr, true, out var itemKind))
       {
            item.Type = itemKind;
     }
     }
  
         // Handle Overview
        if (itemElement.TryGetProperty("Overview", out var overviewProp) && 
           overviewProp.ValueKind != System.Text.Json.JsonValueKind.Null)
    {
    item.Overview = overviewProp.GetString();
       }
             
      // Handle CommunityRating
                    if (itemElement.TryGetProperty("CommunityRating", out var ratingProp) && 
        ratingProp.ValueKind == System.Text.Json.JsonValueKind.Number)
               {
  item.CommunityRating = (float?)ratingProp.GetDouble();
       }
              
// Handle OfficialRating
        if (itemElement.TryGetProperty("OfficialRating", out var officialRatingProp) &&
 officialRatingProp.ValueKind != System.Text.Json.JsonValueKind.Null)
         {
              item.OfficialRating = officialRatingProp.GetString();
   }
             
           // Handle PremiereDate
         if (itemElement.TryGetProperty("PremiereDate", out var premiereProp) &&
        premiereProp.ValueKind != System.Text.Json.JsonValueKind.Null)
    {
       if (DateTime.TryParse(premiereProp.GetString(), out var premiereDate))
          {
        item.PremiereDate = premiereDate;
   }
               }
          
    // Handle ProductionYear
   if (itemElement.TryGetProperty("ProductionYear", out var yearProp) &&
         yearProp.ValueKind == System.Text.Json.JsonValueKind.Number)
   {
  item.ProductionYear = yearProp.GetInt32();
           }
       
       // Handle RunTimeTicks
       if (itemElement.TryGetProperty("RunTimeTicks", out var runtimeProp) &&
     runtimeProp.ValueKind == System.Text.Json.JsonValueKind.Number)
           {
    item.RunTimeTicks = runtimeProp.GetInt64();
        }
       
      // Handle Genres
      if (itemElement.TryGetProperty("Genres", out var genresProp) &&
 genresProp.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
     var genres = new List<string>();
              foreach (var genre in genresProp.EnumerateArray())
             {
        if (genre.ValueKind == System.Text.Json.JsonValueKind.String)
        {
        var genreStr = genre.GetString();
        if (!string.IsNullOrEmpty(genreStr))
    {
 genres.Add(genreStr);
     }
         }
        }
  item.Genres = genres.ToArray();
   }
         
  // Handle Tags
    if (itemElement.TryGetProperty("Tags", out var tagsProp) &&
       tagsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
   {
         var tags = new List<string>();
     foreach (var tag in tagsProp.EnumerateArray())
    {
    if (tag.ValueKind == System.Text.Json.JsonValueKind.String)
{
      var tagStr = tag.GetString();
 if (!string.IsNullOrEmpty(tagStr))
      {
 tags.Add(tagStr);
     }
         }
    }
  item.Tags = tags.ToArray();
     }
 
        // Handle SeriesName (for Episodes)
    if (itemElement.TryGetProperty("SeriesName", out var seriesNameProp) &&
    seriesNameProp.ValueKind != System.Text.Json.JsonValueKind.Null)
     {
         item.SeriesName = seriesNameProp.GetString();
 }
     
       // Handle Season Number (ParentIndexNumber for Episodes)
      if (itemElement.TryGetProperty("ParentIndexNumber", out var parentIndexProp) &&
      parentIndexProp.ValueKind == System.Text.Json.JsonValueKind.Number)
     {
           item.ParentIndexNumber = parentIndexProp.GetInt32();
}
    
       // Handle Episode/Track Number (IndexNumber)
   if (itemElement.TryGetProperty("IndexNumber", out var indexProp) &&
 indexProp.ValueKind == System.Text.Json.JsonValueKind.Number)
{
     item.IndexNumber = indexProp.GetInt32();
     }
      
   // Handle Album (for Music)
            if (itemElement.TryGetProperty("Album", out var albumProp) &&
    albumProp.ValueKind != System.Text.Json.JsonValueKind.Null)
          {
   item.Album = albumProp.GetString();
      }
 
            // Handle AlbumArtist (for Music)
       if (itemElement.TryGetProperty("AlbumArtist", out var albumArtistProp) &&
   albumArtistProp.ValueKind != System.Text.Json.JsonValueKind.Null)
      {
      item.AlbumArtist = albumArtistProp.GetString();
      }
        
     // Handle Artists array (for Music/Books)
      if (itemElement.TryGetProperty("Artists", out var artistsProp) &&
 artistsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
       {
           var artists = new List<string>();
      foreach (var artist in artistsProp.EnumerateArray())
     {
    if (artist.ValueKind == System.Text.Json.JsonValueKind.String)
     {
     var artistStr = artist.GetString();
  if (!string.IsNullOrEmpty(artistStr))
    {
    artists.Add(artistStr);
 }
    }
    }
       item.Artists = artists.ToArray();
     }
    
         items.Add(item);
     }
       catch (Exception ex)
     {
   _logger.LogWarning(ex, "[Federation] Error parsing item from {ServerName}", _server.Name);
          }
  }

    _logger.LogInformation("[Federation] Retrieved {Count} items from remote server {ServerName}", 
    items.Count, _server.Name);

           return items;
 }
     catch (Exception ex)
     {
       _logger.LogError(ex, "Error getting items from remote server {ServerName}", _server.Name);
return new List<BaseItemDto>();
     }
        }

      /// <summary>
        /// Gets a specific item by ID from the remote server.
     /// </summary>
        /// <param name="itemId">The item ID.</param>
     /// <param name="userId">The user ID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The item, or null if not found.</returns>
        public async Task<BaseItemDto?> GetItemAsync(
   string itemId,
            string? userId = null,
            CancellationToken cancellationToken = default)
        {
    try
   {
    var userIdToUse = userId ?? _server.UserId;
   if (string.IsNullOrEmpty(userIdToUse))
    {
  _logger.LogWarning("No user ID specified for remote server {ServerName}", _server.Name);
            return null;
      }

   var url = $"/Users/{userIdToUse}/Items/{itemId}";

     _logger.LogDebug("Getting item {ItemId} from {ServerName}", itemId, _server.Name);

     var response = await _httpClient.GetAsync(url, cancellationToken);
  response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
         return JsonSerializer.Deserialize<BaseItemDto>(content, new JsonSerializerOptions
           {
    PropertyNameCaseInsensitive = true
       });
    }
            catch (Exception ex)
     {
    _logger.LogError(ex, "Error getting item {ItemId} from remote server {ServerName}", itemId, _server.Name);
      return null;
     }
        }

        /// <summary>
     /// Gets playback information for a specific item.
        /// </summary>
        /// <param name="itemId">The item ID.</param>
  /// <param name="userId">The user ID.</param>
/// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The playback info.</returns>
        public async Task<PlaybackInfoResponse?> GetPlaybackInfoAsync(
          string itemId,
       string? userId = null,
   CancellationToken cancellationToken = default)
        {
      try
            {
     var userIdToUse = userId ?? _server.UserId;
     if (string.IsNullOrEmpty(userIdToUse))
          {
       _logger.LogWarning("No user ID specified for remote server {ServerName}", _server.Name);
return null;
           }

  var url = $"/Items/{itemId}/PlaybackInfo?UserId={userIdToUse}";

    _logger.LogDebug("Getting playback info for item {ItemId} from {ServerName}", itemId, _server.Name);

     var response = await _httpClient.GetAsync(url, cancellationToken);
             response.EnsureSuccessStatusCode();

     var content = await response.Content.ReadAsStringAsync(cancellationToken);
   return JsonSerializer.Deserialize<PlaybackInfoResponse>(content, new JsonSerializerOptions
        {
             PropertyNameCaseInsensitive = true
  });
   }
            catch (Exception ex)
     {
                _logger.LogError(ex, "Error getting playback info for item {ItemId} from remote server {ServerName}", itemId, _server.Name);
                return null;
            }
        }

 /// <summary>
        /// Gets system information from the remote server.
        /// </summary>
 /// <param name="cancellationToken">Cancellation token.</param>
   /// <returns>System information.</returns>
        public async Task<SystemInfo?> GetSystemInfoAsync(CancellationToken cancellationToken = default)
    {
     try
    {
          var response = await _httpClient.GetAsync("/System/Info", cancellationToken);
           response.EnsureSuccessStatusCode();

      var content = await response.Content.ReadAsStringAsync(cancellationToken);
     return JsonSerializer.Deserialize<SystemInfo>(content, new JsonSerializerOptions
       {
   PropertyNameCaseInsensitive = true
         });
      }
catch (Exception ex)
       {
    _logger.LogError(ex, "Error getting system info from remote server {ServerName}", _server.Name);
  return null;
}
        }

     /// <summary>
        /// Gets users from the remote server.
      /// </summary>
      /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>List of users.</returns>
   public async Task<List<UserDto>?> GetUsersAsync(CancellationToken cancellationToken = default)
 {
      try
  {
     var response = await _httpClient.GetAsync("/Users", cancellationToken);
   response.EnsureSuccessStatusCode();

      var content = await response.Content.ReadAsStringAsync(cancellationToken);
   return JsonSerializer.Deserialize<List<UserDto>>(content, new JsonSerializerOptions
   {
    PropertyNameCaseInsensitive = true
      });
        }
     catch (Exception ex)
  {
          _logger.LogError(ex, "Error getting users from remote server {ServerName}", _server.Name);
    return null;
            }
        }

   /// <summary>
        /// Gets libraries (user views) from the remote server.
     /// </summary>
   /// <param name="cancellationToken">Cancellation token.</param>
/// <returns>List of libraries.</returns>
public async Task<List<BaseItemDto>?> GetLibrariesAsync(CancellationToken cancellationToken = default)
   {
      try
   {
      _logger.LogInformation("[Federation] GetLibrariesAsync called for server: {ServerName}", _server.Name);
   _logger.LogInformation("[Federation] Server UserId: {UserId}", _server.UserId ?? "NULL");
   
      var userIdToUse = _server.UserId;
       if (string.IsNullOrEmpty(userIdToUse))
     {
    _logger.LogWarning("[Federation] No UserId configured, attempting to get first user");
    // Try to get first user
          var users = await GetUsersAsync(cancellationToken);
     if (users == null || users.Count == 0)
    {
         _logger.LogWarning("No users found on remote server {ServerName}", _server.Name);
  return null;
      }
      userIdToUse = users[0].Id;
      _logger.LogInformation("[Federation] Using first user: {UserId}", userIdToUse);
      }

var url = $"/Users/{userIdToUse}/Views";
      _logger.LogInformation("[Federation] Requesting views from: {Url}", url);

   var response = await _httpClient.GetAsync(url, cancellationToken);
     
   _logger.LogInformation("[Federation] Response status: {StatusCode}", response.StatusCode);
         
   response.EnsureSuccessStatusCode();

   var content = await response.Content.ReadAsStringAsync(cancellationToken);
    _logger.LogInformation("[Federation] Response length: {Length} characters", content.Length);
     
 // Parse as JsonDocument to inspect and handle string IDs
 using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
    
 if (!root.TryGetProperty("Items", out var itemsElement))
   {
    _logger.LogWarning("[Federation] No Items property in response");
      return new List<BaseItemDto>();
  }

    var libraries = new List<BaseItemDto>();
     
    foreach (var item in itemsElement.EnumerateArray())
        {
    try
   {
   // Create a BaseItemDto and manually set properties
var library = new BaseItemDto();
 
   // Handle ID as string
      if (item.TryGetProperty("Id", out var idProp))
{
      library.Id = Guid.TryParse(idProp.GetString(), out var guid) ? guid : Guid.NewGuid();
      }
   
     // Handle Name
          if (item.TryGetProperty("Name", out var nameProp))
   {
     library.Name = nameProp.GetString();
       }
     
     // Handle CollectionType as string
      if (item.TryGetProperty("CollectionType", out var typeProp) && typeProp.ValueKind != System.Text.Json.JsonValueKind.Null)
  {
  var typeStr = typeProp.GetString();
    if (!string.IsNullOrEmpty(typeStr) && Enum.TryParse<Jellyfin.Data.Enums.CollectionType>(typeStr, true, out var collectionType))
       {
   library.CollectionType = collectionType;
      }
   }
  
      // Handle ChildCount
         if (item.TryGetProperty("ChildCount", out var countProp))
    {
     library.ChildCount = countProp.GetInt32();
     }
   
  libraries.Add(library);
     _logger.LogInformation("[Federation] Library parsed: {Name} (Id: {Id}, Type: {Type}, Count: {Count})", 
     library.Name, library.Id, library.CollectionType?.ToString() ?? "unknown", library.ChildCount ?? 0);
     }
catch (Exception itemEx)
  {
   _logger.LogError(itemEx, "[Federation] Error parsing library item");
 }
            }
  
       _logger.LogInformation("[Federation] Successfully parsed {Count} libraries", libraries.Count);
    return libraries;
   }
catch (Exception ex)
   {
       _logger.LogError(ex, "Error getting libraries from remote server {ServerName}", _server.Name);
  return null;
 }
     }

        /// <summary>
/// Builds a stream URL for a remote item.
   /// </summary>
    /// <param name="itemId">The item ID.</param>
/// <param name="mediaSourceId">The media source ID.</param>
        /// <param name="userId">The user ID.</param>
        /// <param name="container">Container format (optional).</param>
        /// <param name="audioCodec">Audio codec (optional).</param>
  /// <param name="videoCodec">Video codec (optional).</param>
   /// <param name="maxBitrate">Maximum bitrate (optional).</param>
    /// <returns>The stream URL.</returns>
        public string BuildStreamUrl(
   string itemId,
            string mediaSourceId,
        string? userId = null,
            string? container = null,
        string? audioCodec = null,
 string? videoCodec = null,
       int? maxBitrate = null)
     {
         var userIdToUse = userId ?? _server.UserId;
 var queryParams = new List<string>
    {
    $"MediaSourceId={mediaSourceId}",
      $"api_key={_server.ApiKey}",
    "Static=false"
 };

   if (!string.IsNullOrEmpty(container))
       {
        queryParams.Add($"Container={container}");
            }

if (!string.IsNullOrEmpty(audioCodec))
    {
   queryParams.Add($"AudioCodec={audioCodec}");
      }

            if (!string.IsNullOrEmpty(videoCodec))
 {
             queryParams.Add($"VideoCodec={videoCodec}");
            }

    if (maxBitrate.HasValue)
            {
                queryParams.Add($"MaxStreamingBitrate={maxBitrate.Value}");
      }

 return $"{_server.Url}/Videos/{itemId}/stream?{string.Join("&", queryParams)}";
     }

   /// <summary>
        /// Tests the connection to the remote server.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if successful, false otherwise.</returns>
        public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
   try
            {
  var response = await _httpClient.GetAsync("/System/Info/Public", cancellationToken);
                response.EnsureSuccessStatusCode();

         _logger.LogInformation("Successfully connected to remote server {ServerName}", _server.Name);
 return true;
   }
            catch (Exception ex)
       {
     _logger.LogError(ex, "Failed to connect to remote server {ServerName}", _server.Name);
    return false;
     }
    }

     /// <inheritdoc />
        public void Dispose()
        {
     _httpClient?.Dispose();
        }
    }

  /// <summary>
    /// Playback information response.
    /// </summary>
    public class PlaybackInfoResponse
 {
        /// <summary>
     /// Gets or sets the media sources.
        /// </summary>
        public List<MediaBrowser.Model.Dto.MediaSourceInfo>? MediaSources { get; set; }

        /// <summary>
        /// Gets or sets the play session ID.
        /// </summary>
        public string? PlaySessionId { get; set; }

 /// <summary>
   /// Gets or sets error code.
        /// </summary>
   public string? ErrorCode { get; set; }
    }

    /// <summary>
    /// System information.
    /// </summary>
    public class SystemInfo
  {
        /// <summary>
        /// Gets or sets the server name.
        /// </summary>
        public string? ServerName { get; set; }

        /// <summary>
        /// Gets or sets the version.
   /// </summary>
  public string? Version { get; set; }

        /// <summary>
        /// Gets or sets the operating system.
        /// </summary>
        public string? OperatingSystem { get; set; }

        /// <summary>
        /// Gets or sets the ID.
        /// </summary>
        public string? Id { get; set; }
}

    /// <summary>
    /// User DTO.
    /// </summary>
    public class UserDto
    {
        /// <summary>
  /// Gets or sets the user ID.
        /// </summary>
        public string? Id { get; set; }

    /// <summary>
        /// Gets or sets the user name.
        /// </summary>
     public string? Name { get; set; }

 /// <summary>
/// Gets or sets whether the user has password.
        /// </summary>
        public bool HasPassword { get; set; }

        /// <summary>
        /// Gets or sets whether the user has configured password.
      /// </summary>
        public bool HasConfiguredPassword { get; set; }
    }
}