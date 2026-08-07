using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchSync.Services;

/// <summary>
/// Background service that keeps watch states in sync across all users in a sync group.
///
/// Flow:
///   1. Subscribe to IUserDataManager.UserDataSaved at startup.
///   2. On every PlaybackFinished or TogglePlayed event for an Episode, check whether
///      the affected user+series appears in any configured connection.
///   3. For each match, copy the Played state to ALL other users in the group.
///
/// Unwatch only propagates from a manual toggle (the checkmark).  A partial
/// rewatch fires PlaybackFinished with Played=false; that is ignored so it does
/// not unwatch the episode for the rest of the group.
///
/// Propagated writes reuse the SOURCE save reason (PlaybackFinished / TogglePlayed)
/// rather than Import, so scrobbler plugins (Trakt, Simkl, …) that listen for
/// UserDataSaved fire for every user in the group, not just the one who was
/// actively playing.  Re-entrancy is bounded by the equality check in ApplySync
/// (once the target already matches the source state, no further write occurs);
/// _syncInProgress additionally guards against concurrent fan-out.
/// </summary>
public class WatchSyncService : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<WatchSyncService> _logger;

    private readonly HashSet<string> _syncInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncLock = new();
    private bool _disposed;

    public WatchSyncService(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILogger<WatchSyncService> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _logger.LogInformation("WatchSync: service started, listening for watch-state changes");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // Only act on explicit played-state changes.  Import is our own write reason.
        if (e.SaveReason != UserDataSaveReason.PlaybackFinished &&
            e.SaveReason != UserDataSaveReason.TogglePlayed)
        {
            return;
        }

        // Rewatching an already-watched episode makes Jellyfin fire PlaybackFinished
        // with Played=false when the viewer stops before the end.  That must never
        // unwatch the episode for the rest of the group — only a manual toggle (the
        // checkmark, i.e. TogglePlayed) is allowed to unwatch.  So ignore any
        // playback-driven unwatch entirely.
        if (e.SaveReason == UserDataSaveReason.PlaybackFinished && !e.UserData.Played)
            return;

        if (e.Item is not Episode episode)
            return;

        var seriesId = episode.SeriesId;
        if (seriesId == Guid.Empty)
            return;

        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Connections.Count == 0)
            return;

        // Find every sync group that includes the triggering user and covers this series.
        var connections = config.Connections
            .Where(c =>
                c.SeriesId == seriesId &&
                c.Users.Any(u => u.Id == e.UserId))
            .ToList();

        if (connections.Count == 0)
            return;

        foreach (var connection in connections)
        {
            // Propagate to every other member of the group.
            foreach (var target in connection.Users.Where(u => u.Id != e.UserId))
            {
                var lockKey = $"{e.Item.Id}:{target.Id}";

                lock (_syncLock)
                {
                    if (!_syncInProgress.Add(lockKey))
                        continue;
                }

                try
                {
                    ApplySync(e, target.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "WatchSync: unhandled error syncing {Item} to user {UserId}",
                        e.Item.Name,
                        target.Id);
                }
                finally
                {
                    lock (_syncLock)
                    {
                        _syncInProgress.Remove(lockKey);
                    }
                }
            }
        }
    }

    private void ApplySync(UserDataSaveEventArgs e, Guid targetUserId)
    {
        var targetUser = _userManager.GetUserById(targetUserId);
        if (targetUser is null)
        {
            _logger.LogWarning(
                "WatchSync: target user {UserId} not found — skipping sync for {Item}",
                targetUserId,
                e.Item.Name);
            return;
        }

        var targetData = _userDataManager.GetUserData(targetUser, e.Item);

        if (targetData.Played == e.UserData.Played)
            return;

        targetData.Played = e.UserData.Played;

        if (e.UserData.Played)
        {
            targetData.PlayCount = Math.Max(targetData.PlayCount, 1);
            targetData.LastPlayedDate ??= e.UserData.LastPlayedDate;
        }

        // Reuse the source reason (PlaybackFinished / TogglePlayed) rather than
        // Import so scrobbler plugins listening for UserDataSaved fire for this
        // user too.  The equality check above stops the resulting re-entrant
        // UserDataSaved from looping: once the target matches the source state,
        // the next pass returns before writing.
        _userDataManager.SaveUserData(
            targetUser,
            e.Item,
            targetData,
            e.SaveReason,
            CancellationToken.None);

        _logger.LogInformation(
            "WatchSync: {Item} → {State} for '{TargetUser}' (mirrored from user {SourceUserId})",
            e.Item.Name,
            e.UserData.Played ? "watched" : "unwatched",
            targetUser.Username,
            e.UserId);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
