using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Providers;
using Jellyfin.Plugin.Federation.Resolvers;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation
{
    /// <summary>
    /// Entry point for initializing federation services.
    /// </summary>
    public class FederationEntryPoint
    {
        private readonly ILogger<FederationEntryPoint> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
 /// Initializes a new instance of the <see cref="FederationEntryPoint"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
        public FederationEntryPoint(
         ILogger<FederationEntryPoint> logger,
   ILibraryManager libraryManager,
       ILoggerFactory loggerFactory)
        {
   _logger = logger;
            _libraryManager = libraryManager;
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Runs initialization tasks.
      /// </summary>
      /// <returns>A task representing the asynchronous operation.</returns>
 public Task RunAsync()
        {
            _logger.LogInformation("Federation Plugin Entry Point started");
    
         try
   {
        // Initialize federation library manager
     var federationManager = new FederationLibraryManager(
           _libraryManager,
   _loggerFactory.CreateLogger<FederationLibraryManager>(),
  _loggerFactory);

          federationManager.Initialize();

 _logger.LogInformation("Federation Plugin services initialized successfully");
            }
   catch (Exception ex)
   {
     _logger.LogError(ex, "Error initializing Federation Plugin services");
            }

      return Task.CompletedTask;
      }
    }
}