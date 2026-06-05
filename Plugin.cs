using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.WatchSync;

/// <summary>
/// Main plugin entry point. Exposes the admin UI page in the server sidebar.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public override Guid Id => new Guid("3d3a1d5a-8f15-4c1c-8da3-5f7e2b1a9c6d");
    public override string Name => "Watch State Sync";
    public override string Description => "Synchronize watch states between connected user pairs for linked TV shows.";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "watchsync",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "link",
                DisplayName = "Watch State Sync"
            }
        };
    }
}
