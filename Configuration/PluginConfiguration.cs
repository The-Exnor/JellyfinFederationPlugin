using MediaBrowser.Model.Plugins;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Plugin configuration for federation settings.
    /// </summary>
 public class PluginConfiguration : BasePluginConfiguration
  {
        /// <summary>
        /// Gets or sets the list of remote Jellyfin servers.
   /// </summary>
        public List<RemoteServer> RemoteServers { get; set; } = new List<RemoteServer>();

  /// <summary>
        /// Gets or sets the virtual library mappings.
        /// </summary>
        public List<LibraryMapping> LibraryMappings { get; set; } = new List<LibraryMapping>();
}

    /// <summary>
    /// Represents a remote Jellyfin server configuration.
    /// </summary>
    public class RemoteServer
    {
        /// <summary>
        /// Gets or sets the unique identifier for this server.
        /// </summary>
        public string Id { get; set; } = System.Guid.NewGuid().ToString();

        /// <summary>
        /// Gets or sets the friendly name for this server.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
  /// Gets or sets the server URL (e.g., http://remote-jellyfin:8096).
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the API key for authentication.
        /// </summary>
     public string ApiKey { get; set; } = string.Empty;

        /// <summary>
 /// Gets or sets a value indicating whether this server is enabled.
  /// </summary>
      public bool Enabled { get; set; } = true;

        /// <summary>
   /// Gets or sets the user ID to authenticate as on the remote server.
        /// </summary>
        public string UserId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a mapping between remote libraries and local virtual libraries.
    /// </summary>
    public class LibraryMapping
    {
   /// <summary>
        /// Gets or sets the local library name (shadow library).
        /// </summary>
    public string LocalLibraryName { get; set; } = string.Empty;

        /// <summary>
  /// Gets or sets the media type (Movie, Series, MusicVideo, etc.).
        /// </summary>
  public string MediaType { get; set; } = "Movie";

        /// <summary>
 /// Gets or sets the list of remote server IDs to pull content from.
        /// </summary>
     public List<string> RemoteServerIds { get; set; } = new List<string>();

        /// <summary>
   /// Gets or sets the list of specific remote library sources.
        /// </summary>
   public List<RemoteLibrarySource> RemoteLibrarySources { get; set; } = new List<RemoteLibrarySource>();

        /// <summary>
        /// Gets or sets a value indicating whether this mapping is enabled.
      /// </summary>
        public bool Enabled { get; set; } = true;
  }

    /// <summary>
  /// Represents a specific remote library source.
    /// </summary>
 public class RemoteLibrarySource
    {
   /// <summary>
     /// Gets or sets the remote server ID.
   /// </summary>
  public string ServerId { get; set; } = string.Empty;

        /// <summary>
  /// Gets or sets the remote server name (for display).
        /// </summary>
        public string ServerName { get; set; } = string.Empty;

   /// <summary>
    /// Gets or sets the remote library ID.
   /// </summary>
 public string RemoteLibraryId { get; set; } = string.Empty;

        /// <summary>
   /// Gets or sets the remote library name (for display).
        /// </summary>
   public string RemoteLibraryName { get; set; } = string.Empty;
    }
}
