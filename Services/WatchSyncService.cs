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
/// actively playing.  The UserDataSaved events raised by our own writes are
/// ignored (_propagating), so a change only reaches the groups of the user who
/// made it: with groups {A,B} and {B,C}, A's change reaches B but not C.
/// _syncInProgress additionally guards against concurrent fan-out.
/// </summary>
public class WatchSyncService : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<WatchSyncService> _logger;

    private readonly HashSet<string> _syncInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncLock = new();

    // Set while this thread saves a propagated change; SaveUserData raises
    // UserDataSaved synchronously, and that nested event must not fan out again.
    [ThreadStatic]
    private static bool _propagating;
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
        // UpdateUserData is POST /UserItems/{id}/UserData, used by some clients and
        // offline-sync apps to set Played.
        if (e.SaveReason != UserDataSaveReason.PlaybackFinished &&
            e.SaveReason != UserDataSaveReason.TogglePlayed &&
            e.SaveReason != UserDataSaveReason.UpdateUserData)
        {
            return;
        }

        // Rewatching an already-watched episode makes Jellyfin fire PlaybackFinished
        // with Played=false when the viewer stops before the end.  That must never
        // unwatch the episode for the rest of the group — only a manual toggle (the
        // checkmark, i.e. TogglePlayed) is allowed to unwatch.  So ignore any
        // playback-driven unwatch entirely.  UpdateUserData carries the whole
        // user-data object (favourites, ratings, …), so only its watched state is trusted.
        if (e.SaveReason != UserDataSaveReason.TogglePlayed && !e.UserData.Played)
            return;

        if (_propagating)
            return;

        // An exception here would escape into Jellyfin's caller (e.g. abort a
        // season-wide "mark played" halfway), so contain everything.
        try
        {
            SyncToGroup(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WatchSync: error handling watch-state change for {Item}", e.Item?.Name);
        }
    }

    private void SyncToGroup(UserDataSaveEventArgs e)
    {

        if (e.Item is not Episode episode)
            return;

        var seriesId = episode.SeriesId;
        if (seriesId == Guid.Empty)
            return;

        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Connections.Count == 0)
            return;

        // Find every sync group that includes the triggering user and covers this series.
        // ToArray() snapshots the list first: the admin API may modify it concurrently.
        var connections = config.Connections
            .ToArray()
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
        // Mirror BaseItem.MarkPlayed / MarkUnplayed: a stale resume position would keep
        // the episode in "Continue Watching" with a partial progress bar.
        targetData.PlaybackPositionTicks = 0;

        if (e.UserData.Played)
        {
            targetData.PlayCount = Math.Max(targetData.PlayCount, 1);
            targetData.LastPlayedDate ??= e.UserData.LastPlayedDate;
        }
        else
        {
            targetData.PlayCount = 0;
            targetData.LastPlayedDate = null;
        }

        // Reuse the source reason (PlaybackFinished / TogglePlayed) rather than
        // Import so scrobbler plugins listening for UserDataSaved fire for this
        // user too.
        _propagating = true;
        try
        {
            _userDataManager.SaveUserData(
                targetUser,
                e.Item,
                targetData,
                e.SaveReason,
                CancellationToken.None);
        }
        finally
        {
            _propagating = false;
        }

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
