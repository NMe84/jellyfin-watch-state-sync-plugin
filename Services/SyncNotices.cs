using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.WatchSync.Services;

/// <summary>
/// "While you were away" notices: episodes another user's watching marked as
/// watched for this user. Shown once by the web UI script, then cleared by the user.
/// Persisted as JSON in the plugin's data folder so they survive restarts.
/// </summary>
public static class SyncNotices
{
    // Episode labels kept per notice; the count keeps growing past this.
    private const int MaxLabels = 50;

    private static readonly object Lock = new();
    private static Dictionary<Guid, List<Notice>>? _notices;

    public class Notice
    {
        public Guid SeriesId { get; set; }
        public string SeriesName { get; set; } = string.Empty;
        public string FromUser { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<string> Episodes { get; set; } = new();
        public DateTime Updated { get; set; }
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "notices.json");

    /// <summary>Records that <paramref name="episode"/> was marked watched for <paramref name="userId"/>.</summary>
    public static void Add(Guid userId, string fromUser, Episode episode)
    {
        var label = episode.ParentIndexNumber is int s && episode.IndexNumber is int e
            ? $"S{s}E{e}"
            : episode.Name;

        lock (Lock)
        {
            var all = Load();
            if (!all.TryGetValue(userId, out var list))
                all[userId] = list = new List<Notice>();

            var notice = list.FirstOrDefault(n => n.SeriesId == episode.SeriesId && n.FromUser == fromUser);
            if (notice is null)
            {
                notice = new Notice { SeriesId = episode.SeriesId, SeriesName = episode.SeriesName, FromUser = fromUser };
                list.Add(notice);
            }

            if (notice.Episodes.Contains(label))
                return;

            notice.Count++;
            if (notice.Episodes.Count < MaxLabels)
                notice.Episodes.Add(label);
            notice.Updated = DateTime.UtcNow;
            Save(all);
        }
    }

    public static IReadOnlyList<Notice> Get(Guid userId)
    {
        lock (Lock)
        {
            return Load().TryGetValue(userId, out var list) ? list.ToList() : new List<Notice>();
        }
    }

    public static void Clear(Guid userId)
    {
        lock (Lock)
        {
            var all = Load();
            if (all.Remove(userId))
                Save(all);
        }
    }

    private static Dictionary<Guid, List<Notice>> Load()
    {
        if (_notices is not null)
            return _notices;

        try
        {
            _notices = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<Guid, List<Notice>>>(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // ponytail: a corrupt file only loses pending notices, never sync state.
            _notices = null;
        }

        return _notices ??= new Dictionary<Guid, List<Notice>>();
    }

    private static void Save(Dictionary<Guid, List<Notice>> all)
    {
        Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
    }
}
