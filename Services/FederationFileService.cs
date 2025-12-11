using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Creates physical .strm and .nfo files that Jellyfin can scan naturally.
 /// </summary>
    public class FederationFileService
    {
        private readonly ILogger<FederationFileService> _logger;
  private readonly ILibraryManager _libraryManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly string _federationBasePath;

    /// <summary>
      /// Initializes a new instance of the <see cref="FederationFileService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
        public FederationFileService(
ILogger<FederationFileService> logger,
         ILibraryManager libraryManager,
            ILoggerFactory loggerFactory)
    {
         _logger = logger;
            _libraryManager = libraryManager;
     _loggerFactory = loggerFactory;
            
 // Base path for federation files - Jellyfin will scan this
       _federationBasePath = Path.Combine(
     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "jellyfin", "federation");
  
            _logger.LogInformation("[Federation] Federation files base path: {Path}", _federationBasePath);
     }

        /// <summary>
        /// Creates .strm and .nfo files for all configured mappings.
        /// </summary>
   /// <param name="operationId">Operation ID for progress tracking.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
      /// <returns>Number of files created.</returns>
  public async Task<int> CreateFederationFilesAsync(string? operationId = null, CancellationToken cancellationToken = default)
        {
      _logger.LogInformation("[Federation] Creating federation files");

       var config = Plugin.Instance?.Configuration;
   if (config?.LibraryMappings == null)
          {
    _logger.LogWarning("[Federation] No library mappings configured");
   return 0;
    }

    var enabledMappings = config.LibraryMappings.Where(m => m.Enabled).ToList();
     _logger.LogInformation("[Federation] Processing {Count} enabled mappings", enabledMappings.Count);

       int totalFiles = 0;
        int mappingIndex = 0;

   foreach (var mapping in enabledMappings)
       {
    try
  {
       mappingIndex++;
            if (!string.IsNullOrEmpty(operationId))
      {
        SyncProgressTracker.Update(operationId, mappingIndex, enabledMappings.Count, 
   $"Processing mapping {mappingIndex}/{enabledMappings.Count}: {mapping.LocalLibraryName}");
            }
            
   var fileCount = await CreateFilesForMappingAsync(mapping, config, operationId, cancellationToken);
    totalFiles += fileCount;
 }
catch (Exception ex)
    {
       _logger.LogError(ex, "[Federation] Error creating files for mapping: {Name}", mapping.LocalLibraryName);
  }
    }

        _logger.LogInformation("[Federation] Created {Count} total files", totalFiles);
    return totalFiles;
     }

      /// <summary>
      /// Creates .strm and .nfo files for a single mapping.
     /// </summary>
    private async Task<int> CreateFilesForMappingAsync(
   LibraryMapping mapping,
        PluginConfiguration config,
     string? operationId,
   CancellationToken cancellationToken)
  {
  _logger.LogInformation("[Federation] Creating files for mapping: {Name}", mapping.LocalLibraryName);

    // Create directory for this mapping (always create fresh)
var mappingPath = Path.Combine(_federationBasePath, SanitizeFileName(mapping.LocalLibraryName));
            
      // Clean up old files if directory exists
       if (Directory.Exists(mappingPath))
   {
   _logger.LogInformation("[Federation] Cleaning up existing directory: {Path}", mappingPath);
    try
            {
 Directory.Delete(mappingPath, recursive: true);
   }
       catch (Exception ex)
       {
            _logger.LogWarning(ex, "[Federation] Could not clean directory, will overwrite files");
 }
     }
       
   // Create fresh directory
 Directory.CreateDirectory(mappingPath);
  _logger.LogInformation("[Federation] Created directory: {Path}", mappingPath);

int fileCount = 0;
       
  // Track unknown episode/season numbers for auto-numbering
  var unknownEpisodeCounter = new Dictionary<string, int>(); // Key: "SeriesName_SeasonNum"
        var unknownSeasonCounter = new Dictionary<string, int>(); // Key: "SeriesName"

       // Process each remote source
  foreach (var source in mapping.RemoteLibrarySources ?? new List<RemoteLibrarySource>())
            {
        try
    {
           var server = config.RemoteServers?.FirstOrDefault(s => s.Id == source.ServerId);
 if (server == null)
     {
  _logger.LogWarning("[Federation] Server not found for ID: {ServerId}", source.ServerId);
    continue;
          }

                  if (!server.Enabled)
      {
         _logger.LogWarning("[Federation] Server is disabled: {ServerName}", server.Name);
    continue;
           }

_logger.LogInformation("[Federation] Fetching items from {ServerName} → {LibraryName} (LibraryId: {LibraryId}, MediaType: {MediaType})",
   source.ServerName, source.RemoteLibraryName, source.RemoteLibraryId, mapping.MediaType);

          // Important note about MediaType filtering
           if (mapping.MediaType == "Series")
        {
            _logger.LogWarning("[Federation] NOTE: MediaType='Series' only gets TV show containers, not episodes!");
      _logger.LogWarning("[Federation] To get all TV show content including episodes, use MediaType='Episode' or don't filter by type");
                  }

           using var client = new RemoteServerClient(server, _loggerFactory.CreateLogger<RemoteServerClient>());

    // First, get the total count by making a request with limit=0
 _logger.LogInformation("[Federation] Getting total item count for library...");
 
   if (!string.IsNullOrEmpty(operationId))
{
 SyncProgressTracker.Update(operationId, fileCount, fileCount + 1, 
      $"Checking library: {source.RemoteLibraryName}");
          }
     
    var countCheckItems = await client.GetItemsAsync(
userId: server.UserId,
             mediaType: mapping.MediaType,
   parentId: source.RemoteLibraryId,
    startIndex: 0,
          limit: 1, // Just get 1 item to check total
       cancellationToken: cancellationToken);

      // The API should tell us the total count - check logs for TotalRecordCount
        _logger.LogInformation("[Federation] Initial check returned {Count} items", countCheckItems?.Count ?? 0);

               // Fetch ALL items using pagination
    var allItems = new List<BaseItemDto>();
    var pageSize = 200; // Reduced from 500 to avoid timeouts on large requests
 var startIndex = 0;
            var hasMore = true;
        var pageNumber = 1;

                  while (hasMore)
               {
        _logger.LogInformation("[Federation] Fetching page {Page}: startIndex={StartIndex}, limit={PageSize}",
  pageNumber, startIndex, pageSize);
        
  // Update progress with page info
        if (!string.IsNullOrEmpty(operationId))
           {
 SyncProgressTracker.Update(operationId, fileCount + allItems.Count, fileCount + allItems.Count + pageSize,
      $"Fetching page {pageNumber} from {source.ServerName} ({allItems.Count} items so far)");
           }

         var pageItems = await client.GetItemsAsync(
        userId: server.UserId,
        mediaType: mapping.MediaType,
  parentId: source.RemoteLibraryId,
  startIndex: startIndex,
    limit: pageSize,
  cancellationToken: cancellationToken);

         if (pageItems == null)
{
  _logger.LogWarning("[Federation] GetItemsAsync returned null");
          hasMore = false;
          break;
           }

               if (pageItems.Count == 0)
           {
                _logger.LogInformation("[Federation] No more items returned (empty page)");
    hasMore = false;
      break;
            }

      allItems.AddRange(pageItems);
      _logger.LogInformation("[Federation] Retrieved {Count} items in page {Page} (total so far: {Total})",
      pageItems.Count, pageNumber, allItems.Count);

          // Check if we got fewer items than requested, meaning we've reached the end
  if (pageItems.Count < pageSize)
   {
                _logger.LogInformation("[Federation] Last page reached (got {Count} items, expected {PageSize})",
              pageItems.Count, pageSize);
         hasMore = false;
          }
    else
     {
 startIndex += pageSize;
          pageNumber++;

         // Safety check: don't loop forever
               if (pageNumber > 1000)
         {
          _logger.LogWarning("[Federation] Safety limit reached: 1000 pages (500,000 items). Stopping pagination.");
     hasMore = false;
            }
            }
 }

         _logger.LogInformation("[Federation] Pagination complete! Retrieved {Count} total items from {ServerName} in {Pages} pages",
              allItems.Count, source.ServerName, pageNumber);

          if (allItems.Count == 0)
         {
      _logger.LogWarning("[Federation] No items returned. This could mean:");
          _logger.LogWarning("  - The library is empty");
       _logger.LogWarning("  - The media type filter '{MediaType}' doesn't match library content", mapping.MediaType);
        _logger.LogWarning("  - The parentId '{ParentId}' is incorrect", source.RemoteLibraryId);
              _logger.LogWarning("  - The user doesn't have access to this library");
          _logger.LogWarning("  - Try checking the remote library directly to see what content type it contains");
    }

       // Create files for each item
         var failedItems = new List<string>();
  var itemIndex = 0;
  
  foreach (var item in allItems)
  {
  try
    {
    itemIndex++;
      
   // Update progress every 10 items
 if (!string.IsNullOrEmpty(operationId) && itemIndex % 10 == 0)
      {
     SyncProgressTracker.Update(operationId, fileCount + itemIndex, fileCount + allItems.Count,
      $"Creating files: {itemIndex}/{allItems.Count} from {source.ServerName}");
        }
   
  await CreateFilesForItemAsync(item, server, mappingPath, unknownEpisodeCounter, unknownSeasonCounter, cancellationToken);
 fileCount++;
  }
   catch (Exception ex)
  {
  _logger.LogError(ex, "[Federation] Error creating files for item: {ItemName} (ID: {ItemId})", 
 item.Name, item.Id);
      failedItems.Add($"{item.Name} ({item.Id})");
  }
  }
        
        if (failedItems.Count > 0)
  {
 _logger.LogWarning("[Federation] Failed to create files for {Count} items:", failedItems.Count);
     foreach (var failed in failedItems.Take(10)) // Log first 10
 {
       _logger.LogWarning("  - {Item}", failed);
   }
  if (failedItems.Count > 10)
  {
_logger.LogWarning("  ... and {Count} more", failedItems.Count - 10);
        }
  }
   }
    catch (Exception ex)
{
  _logger.LogError(ex, "[Federation] Error processing source: {SourceName} from server {ServerName}", 
  source.RemoteLibraryName, source.ServerName);
    }
  }

    _logger.LogInformation("[Federation] Created {Count} files for mapping: {Name}", 
     fileCount, mapping.LocalLibraryName);
  
  // Touch the mapping directory to trigger Jellyfin to scan it as a batch
try
   {
   Directory.SetLastWriteTimeUtc(mappingPath, DateTime.UtcNow);
       _logger.LogInformation("[Federation] Triggered library scan for: {Path}", mappingPath);
      }
        catch (Exception ex)
  {
     _logger.LogWarning(ex, "[Federation] Could not trigger library scan");
  }

    return fileCount;
        }

        /// <summary>
        /// Creates .strm and .nfo files for a single item.
   /// </summary>
  private async Task CreateFilesForItemAsync(
       BaseItemDto remoteItem,
      RemoteServer server,
    string basePath,
 Dictionary<string, int> unknownEpisodeCounter,
   Dictionary<string, int> unknownSeasonCounter,
      CancellationToken cancellationToken)
     {
    var itemName = SanitizeFileName(remoteItem.Name ?? "Unknown");
  var year = remoteItem.ProductionYear?.ToString() ?? "";
        
    // Determine file organization based on item type
       string itemPath;
   string fileName;
      
   // Get item type for logging
   var itemType = remoteItem.Type.ToString();
   _logger.LogDebug("[Federation] Creating files for {Type}: {Name}", itemType, remoteItem.Name);
         
     switch (remoteItem.Type)
        {
       case Jellyfin.Data.Enums.BaseItemKind.Episode:
 // TV Episodes: Show/Season/Episode structure
         var seriesName = SanitizeFileName(remoteItem.SeriesName ?? "Unknown Series");
          var seasonNumber = remoteItem.ParentIndexNumber;
        var episodeNumber = remoteItem.IndexNumber;
        
  // Auto-number unknown seasons/episodes
      if (!seasonNumber.HasValue || seasonNumber.Value < 0)
    {
     var seriesKey = seriesName;
      if (!unknownSeasonCounter.ContainsKey(seriesKey))
  {
        unknownSeasonCounter[seriesKey] = 1;
 }
     seasonNumber = unknownSeasonCounter[seriesKey]++;
        _logger.LogInformation("[Federation] Auto-assigned season number {Season} for series: {Series}", 
    seasonNumber, seriesName);
}
      
        if (!episodeNumber.HasValue || episodeNumber.Value < 0)
      {
  var episodeKey = $"{seriesName}_S{seasonNumber:D2}";
   if (!unknownEpisodeCounter.ContainsKey(episodeKey))
   {
 unknownEpisodeCounter[episodeKey] = 1;
  }
       episodeNumber = unknownEpisodeCounter[episodeKey]++;
   _logger.LogInformation("[Federation] Auto-assigned episode number {Episode} for {Series} Season {Season}", 
      episodeNumber, seriesName, seasonNumber);
 }
        
  // Handle special/unknown seasons
   string seasonFolder;
       if (seasonNumber == 0)
    {
 seasonFolder = "Specials"; // Season 0 = Specials
        }
   else
      {
     seasonFolder = $"Season {seasonNumber:D2}";
        }
     
       // Create: ShowName/Season 01/S01E01 - Episode Name.strm
    var showPath = Path.Combine(basePath, seriesName);
       var seasonPath = Path.Combine(showPath, seasonFolder);
    Directory.CreateDirectory(seasonPath);
    
  // Handle episode naming
        string episodePrefix = seasonNumber == 0 
    ? $"S00E{episodeNumber:D2}" 
   : $"S{seasonNumber:D2}E{episodeNumber:D2}";
     
   fileName = $"{episodePrefix} - {itemName}";
    itemPath = Path.Combine(seasonPath, fileName);
      break;
  
    case Jellyfin.Data.Enums.BaseItemKind.Season:
   // TV Seasons: Show/Season folder
     var seasonSeriesName = SanitizeFileName(remoteItem.SeriesName ?? remoteItem.Name ?? "Unknown Series");
     var seasonNum = remoteItem.IndexNumber;
          
    // Auto-number unknown seasons
   if (!seasonNum.HasValue || seasonNum.Value < 0)
     {
     var seriesKey = seasonSeriesName;
  if (!unknownSeasonCounter.ContainsKey(seriesKey))
 {
 unknownSeasonCounter[seriesKey] = 1;
     }
   seasonNum = unknownSeasonCounter[seriesKey]++;
  _logger.LogInformation("[Federation] Auto-assigned season number {Season} for series: {Series}", 
   seasonNum, seasonSeriesName);
      }
        
   var seasonFolderName = seasonNum == 0 ? "Specials" : $"Season {seasonNum:D2}";
     
   var seasonShowPath = Path.Combine(basePath, seasonSeriesName);
   itemPath = Path.Combine(seasonShowPath, seasonFolderName);
         Directory.CreateDirectory(itemPath);
 fileName = "season"; // Will create season.nfo
           break;
        
   case Jellyfin.Data.Enums.BaseItemKind.Series:
          // TV Series: Show folder
       itemPath = Path.Combine(basePath, itemName);
            Directory.CreateDirectory(itemPath);
      fileName = "tvshow"; // Will create tvshow.nfo
           break;
   
 case Jellyfin.Data.Enums.BaseItemKind.Movie:
            // Movies: Movie Name (Year).strm
       fileName = string.IsNullOrEmpty(year) ? itemName : $"{itemName} ({year})";
    itemPath = Path.Combine(basePath, fileName);
           break;
     
  case Jellyfin.Data.Enums.BaseItemKind.Audio:
   // Music: Artist/Album/Track.strm
 var artist = SanitizeFileName(remoteItem.AlbumArtist ?? remoteItem.Artists?.FirstOrDefault() ?? "Unknown Artist");
    var album = SanitizeFileName(remoteItem.Album ?? "Unknown Album");
 var trackNumber = remoteItem.IndexNumber ?? 0;
    
     var artistPath = Path.Combine(basePath, artist);
        var albumPath = Path.Combine(artistPath, album);
       Directory.CreateDirectory(albumPath);
     
          fileName = trackNumber > 0 ? $"{trackNumber:D2} - {itemName}" : itemName;
       itemPath = Path.Combine(albumPath, fileName);
              break;
        
    case Jellyfin.Data.Enums.BaseItemKind.MusicAlbum:
   // Music Albums: Artist/Album folder
       var albumArtist = SanitizeFileName(remoteItem.AlbumArtist ?? remoteItem.Artists?.FirstOrDefault() ?? "Unknown Artist");
       var albumName = SanitizeFileName(remoteItem.Name ?? "Unknown Album");
     
        var artistFolder = Path.Combine(basePath, albumArtist);
    itemPath = Path.Combine(artistFolder, albumName);
       Directory.CreateDirectory(itemPath);
          fileName = "album"; // Will create album.nfo
           break;
   
    case Jellyfin.Data.Enums.BaseItemKind.MusicVideo:
         // Music Videos: Artist/Video Name.strm
          var mvArtist = SanitizeFileName(remoteItem.Artists?.FirstOrDefault() ?? "Unknown Artist");
         var mvFolder = Path.Combine(basePath, mvArtist);
        Directory.CreateDirectory(mvFolder);
   
       fileName = string.IsNullOrEmpty(year) ? itemName : $"{itemName} ({year})";
  itemPath = Path.Combine(mvFolder, fileName);
     break;
           
        case Jellyfin.Data.Enums.BaseItemKind.Photo:
    case Jellyfin.Data.Enums.BaseItemKind.PhotoAlbum:
        // Photos: organized by date or album
    var photoDate = remoteItem.PremiereDate?.ToString("yyyy-MM-dd") ?? "Undated";
          var photoFolder = Path.Combine(basePath, photoDate);
  Directory.CreateDirectory(photoFolder);
   
          fileName = itemName;
  itemPath = Path.Combine(photoFolder, fileName);
  break;
        
    case Jellyfin.Data.Enums.BaseItemKind.Book:
    // Books: Author/Book Name.strm
             var author = SanitizeFileName(remoteItem.Artists?.FirstOrDefault() ?? "Unknown Author");
var bookFolder = Path.Combine(basePath, author);
   Directory.CreateDirectory(bookFolder);
        
          fileName = string.IsNullOrEmpty(year) ? itemName : $"{itemName} ({year})";
   itemPath = Path.Combine(bookFolder, fileName);
       break;
        
       default:
       // Generic items: just use name + year
          fileName = string.IsNullOrEmpty(year) ? itemName : $"{itemName} ({year})";
         itemPath = Path.Combine(basePath, fileName);
  break;
       }

    // Create .strm file (stream file)
    // Use HTTP URL to plugin's streaming endpoint
   var strmPath = $"{itemPath}.strm";
     
     // Build URL that Jellyfin's local server will handle
     var streamUrl = $"http://localhost:8096/Plugins/Federation/Stream?serverId={server.Id}&itemId={remoteItem.Id}";
        
  await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken);
 _logger.LogDebug("[Federation] Created .strm file: {Path}", strmPath);

     // Create .nfo file (metadata) - only for playable items
     if (remoteItem.Type != Jellyfin.Data.Enums.BaseItemKind.Season && 
            remoteItem.Type != Jellyfin.Data.Enums.BaseItemKind.Series &&
      remoteItem.Type != Jellyfin.Data.Enums.BaseItemKind.MusicAlbum &&
          remoteItem.Type != Jellyfin.Data.Enums.BaseItemKind.PhotoAlbum)
      {
      var nfoPath = $"{itemPath}.nfo";
   var nfoContent = CreateNfoContent(remoteItem, server);
  
    await File.WriteAllTextAsync(nfoPath, nfoContent, cancellationToken);
         _logger.LogDebug("[Federation] Created .nfo file: {Path}", nfoPath);
  }
        }

   /// <summary>
        /// Creates NFO XML content with metadata.
   /// </summary>
        private string CreateNfoContent(BaseItemDto remoteItem, RemoteServer server)
      {
 var nfo = new System.Text.StringBuilder();
  
      nfo.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
       
   switch (remoteItem.Type)
  {
  case Jellyfin.Data.Enums.BaseItemKind.Movie:
           nfo.AppendLine("<movie>");
  nfo.AppendLine($"  <title>{EscapeXml(remoteItem.Name)}</title>");
  nfo.AppendLine($"  <originaltitle>{EscapeXml(remoteItem.Name)}</originaltitle>");
    AddCommonMetadata(nfo, remoteItem);
     nfo.AppendLine("</movie>");
        break;
          
        case Jellyfin.Data.Enums.BaseItemKind.Episode:
     nfo.AppendLine("<episodedetails>");
    nfo.AppendLine($"  <title>{EscapeXml(remoteItem.Name)}</title>");
    nfo.AppendLine($"  <showtitle>{EscapeXml(remoteItem.SeriesName)}</showtitle>");
       if (remoteItem.ParentIndexNumber.HasValue)
     nfo.AppendLine($"  <season>{remoteItem.ParentIndexNumber}</season>");
  if (remoteItem.IndexNumber.HasValue)
     nfo.AppendLine($"  <episode>{remoteItem.IndexNumber}</episode>");
       AddCommonMetadata(nfo, remoteItem);
    nfo.AppendLine("</episodedetails>");
      break;
   
     case Jellyfin.Data.Enums.BaseItemKind.Series:
  nfo.AppendLine("<tvshow>");
     nfo.AppendLine($"  <title>{EscapeXml(remoteItem.Name)}</title>");
     AddCommonMetadata(nfo, remoteItem);
       nfo.AppendLine("</tvshow>");
   break;
      
  case Jellyfin.Data.Enums.BaseItemKind.Audio:
     nfo.AppendLine("<musicvideo>"); // Using musicvideo format for audio
       nfo.AppendLine($"  <title>{EscapeXml(remoteItem.Name)}</title>");
   if (!string.IsNullOrEmpty(remoteItem.Album))
     nfo.AppendLine($"  <album>{EscapeXml(remoteItem.Album)}</album>");
      if (remoteItem.Artists != null && remoteItem.Artists.Count > 0)
     nfo.AppendLine($"  <artist>{EscapeXml(remoteItem.Artists[0])}</artist>");
  if (remoteItem.IndexNumber.HasValue)
     nfo.AppendLine($"  <track>{remoteItem.IndexNumber}</track>");
  AddCommonMetadata(nfo, remoteItem);
       nfo.AppendLine("</musicvideo>");
        break;
     
     case Jellyfin.Data.Enums.BaseItemKind.MusicVideo:
    nfo.AppendLine("<musicvideo>");
     nfo.AppendLine($"  <title>{EscapeXml(remoteItem.Name)}</title>");
   if (remoteItem.Artists != null && remoteItem.Artists.Count > 0)
     nfo.AppendLine($"  <artist>{EscapeXml(remoteItem.Artists[0])}</artist>");
 AddCommonMetadata(nfo, remoteItem);
nfo.AppendLine("</musicvideo>");
    break;
     
   case Jellyfin.Data.Enums.BaseItemKind.Book:
       nfo.AppendLine("<book>");
    nfo.AppendLine($"  <title>{EscapeXml(remoteItem.Name)}</title>");
   if (remoteItem.Artists != null && remoteItem.Artists.Count > 0)
nfo.AppendLine($"  <author>{EscapeXml(remoteItem.Artists[0])}</author>");
  AddCommonMetadata(nfo, remoteItem);
  nfo.AppendLine("</book>");
 break;
        
        case Jellyfin.Data.Enums.BaseItemKind.Photo:
       nfo.AppendLine("<photo>");
            nfo.AppendLine($"  <title>{EscapeXml(remoteItem.Name)}</title>");
      if (remoteItem.PremiereDate.HasValue)
     nfo.AppendLine($"  <dateadded>{remoteItem.PremiereDate:yyyy-MM-dd}</dateadded>");
       AddCommonMetadata(nfo, remoteItem);
          nfo.AppendLine("</photo>");
          break;
        
   default:
        // Generic video format
       nfo.AppendLine("<movie>"); // Use movie format as generic
     nfo.AppendLine($"  <title>{EscapeXml(remoteItem.Name)}</title>");
 AddCommonMetadata(nfo, remoteItem);
     nfo.AppendLine("</movie>");
   break;
   }
       
  return nfo.ToString();
        }

      /// <summary>
      /// Adds common metadata fields to NFO.
        /// </summary>
        private void AddCommonMetadata(System.Text.StringBuilder nfo, BaseItemDto remoteItem)
        {
            if (remoteItem.ProductionYear.HasValue)
       nfo.AppendLine($"  <year>{remoteItem.ProductionYear}</year>");
        
            if (!string.IsNullOrEmpty(remoteItem.Overview))
                nfo.AppendLine($"  <plot>{EscapeXml(remoteItem.Overview)}</plot>");
      
            if (remoteItem.CommunityRating.HasValue)
        nfo.AppendLine($"  <rating>{remoteItem.CommunityRating}</rating>");
    
     if (!string.IsNullOrEmpty(remoteItem.OfficialRating))
            nfo.AppendLine($"  <mpaa>{EscapeXml(remoteItem.OfficialRating)}</mpaa>");
      
            if (remoteItem.PremiereDate.HasValue)
           nfo.AppendLine($"  <premiered>{remoteItem.PremiereDate:yyyy-MM-dd}</premiered>");
            
    if (remoteItem.RunTimeTicks.HasValue)
            {
          var minutes = TimeSpan.FromTicks(remoteItem.RunTimeTicks.Value).TotalMinutes;
     nfo.AppendLine($"  <runtime>{(int)minutes}</runtime>");
            }

         // Genres
            if (remoteItem.Genres != null)
    {
     foreach (var genre in remoteItem.Genres)
   {
         nfo.AppendLine($"  <genre>{EscapeXml(genre)}</genre>");
        }
    }

      // Studios
            if (remoteItem.Studios != null)
          {
          foreach (var studio in remoteItem.Studios)
    {
        nfo.AppendLine($"  <studio>{EscapeXml(studio.Name)}</studio>");
         }
            }

            // Tags
            if (remoteItem.Tags != null)
         {
foreach (var tag in remoteItem.Tags)
     {
               nfo.AppendLine($"  <tag>{EscapeXml(tag)}</tag>");
       }
   }

  // Federation metadata (for tracking source)
       nfo.AppendLine($"  <!-- Federation Source Info -->");
            nfo.AppendLine($"  <federationsource>{remoteItem.Id}</federationsource>");
        }

        /// <summary>
        /// Sanitizes a filename to remove invalid characters.
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
 var invalid = Path.GetInvalidFileNameChars();
  var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
return sanitized.TrimEnd('.');
     }

        /// <summary>
        /// Escapes XML special characters.
      /// </summary>
        private string EscapeXml(string? text)
     {
 if (string.IsNullOrEmpty(text))
      return string.Empty;

    return text
          .Replace("&", "&amp;")
        .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
         .Replace("'", "&apos;");
        }

     /// <summary>
/// Gets the federation files base path.
        /// </summary>
public string GetFederationBasePath() => _federationBasePath;

        /// <summary>
        /// Clears all federation files.
        /// </summary>
 public void ClearFederationFiles()
        {
            try
            {
    if (Directory.Exists(_federationBasePath))
       {
 _logger.LogInformation("[Federation] Clearing federation files from: {Path}", _federationBasePath);
           Directory.Delete(_federationBasePath, recursive: true);
        _logger.LogInformation("[Federation] Federation files cleared");
            }
        }
        catch (Exception ex)
            {
           _logger.LogError(ex, "[Federation] Error clearing federation files");
   }
        }
    }
}
