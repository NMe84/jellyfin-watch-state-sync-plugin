using System;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.WatchSync.Models;

/// <summary>
/// A single user in a sync group, with a cached display name so the UI does not
/// need extra lookups on every render.
/// </summary>
public class UserEntry
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}
