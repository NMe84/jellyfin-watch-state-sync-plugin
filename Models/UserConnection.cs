using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.WatchSync.Models;

/// <summary>
/// A sync group: two or more users whose watch states for a specific TV series are kept
/// in sync.  Users are stored in sorted (ascending) GUID order so that the same group
/// always has the same canonical representation regardless of insertion order.
/// </summary>
public class UserConnection
{
    [JsonProperty("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonProperty("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonProperty("seriesName")]
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// All users in this sync group.  Always sorted by <see cref="UserEntry.Id"/> ascending.
    /// </summary>
    [JsonProperty("users")]
    public List<UserEntry> Users { get; set; } = new();
}
