using Jellyfin.Plugin.WatchSync.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WatchSync;

/// <summary>
/// Registers plugin services into Jellyfin's DI container at startup.
/// Jellyfin discovers this class automatically via reflection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<WatchSyncService>();
        serviceCollection.AddHostedService<ChainIconScriptService>();
    }
}
