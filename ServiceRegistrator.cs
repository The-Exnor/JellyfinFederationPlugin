using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using JellyfinFederationPlugin.Library;
using JellyfinFederationPlugin.Api;
using JellyfinFederationPlugin.Services;
using JellyfinFederationPlugin.Streaming;
using JellyfinFederationPlugin.Caching;
using JellyfinFederationPlugin.Failover;
using JellyfinFederationPlugin.Bandwidth;

namespace JellyfinFederationPlugin
{
    public class ServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Core federation services
            serviceCollection.AddHttpClient<FederationRequestHandler>();
            serviceCollection.AddSingleton<FederationLibraryService>();
            serviceCollection.AddSingleton<PlaybackProxyService>();
            serviceCollection.AddHttpClient<FederationService>();
            
            // Library integration services
            serviceCollection.AddSingleton<FederationLibraryMonitor>();
            serviceCollection.AddSingleton<ILibraryMonitor>(provider => 
                provider.GetRequiredService<FederationLibraryMonitor>());
            
            // Advanced services
            serviceCollection.AddSingleton<FederationCacheService>();
            serviceCollection.AddHttpClient<FederationFailoverService>();
            serviceCollection.AddSingleton<FederationFailoverService>();
            serviceCollection.AddSingleton<FederationBandwidthManager>();
            
            // Enhanced streaming services
            serviceCollection.AddSingleton<FederationStreamingService>(); // Keep for backward compatibility
            serviceCollection.AddHttpClient<EnhancedFederationStreamingService>();
            serviceCollection.AddSingleton<EnhancedFederationStreamingService>();
            
            // Background services
            serviceCollection.AddSingleton<FederationSyncService>();
        }
    }
}