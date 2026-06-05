using System.Collections.Generic;
using Jellyfin.Plugin.WatchSync.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WatchSync;

/// <summary>
/// Plugin configuration; Jellyfin persists this to XML automatically on every SaveConfiguration() call.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public List<UserConnection> Connections { get; set; } = new();
}
