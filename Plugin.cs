using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation
{
    /// <summary>
    /// Jellyfin Federation Plugin - Aggregate content from multiple Jellyfin servers.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger<Plugin> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Application paths.</param>
        /// <param name="xmlSerializer">XML serializer.</param>
        /// <param name="logger">Logger instance.</param>
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _logger = logger;
            Instance = this;

            _logger.LogInformation("=== Jellyfin Federation Plugin v{Version} Initialized ===", Version);
        }

        /// <inheritdoc />
        public override string Name => "Jellyfin Federation";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("12345678-1234-1234-1234-123456789abc");

        /// <inheritdoc />
        public override string Description => "Aggregate content from multiple Jellyfin servers into unified virtual libraries.";

        /// <summary>
        /// Gets the plugin singleton instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            _logger.LogInformation("GetPages() called - Registering redirect page");

            // Return a simple redirect page that points to our API controller
            yield return new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.redirectPage.html"
            };
        }

        /// <summary>
        /// Gets the configuration page URL.
        /// </summary>
        /// <returns>The URL to the configuration page.</returns>
        public string GetConfigurationPageUrl()
        {
            return "/Plugins/Federation/ConfigPage";
        }
    }
}
