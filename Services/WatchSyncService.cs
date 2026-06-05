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
/// Writes use UserDataSaveReason.Import so they do not re-trigger this handler.
/// The _syncInProgress set provides a secondary guard against concurrent fan-out.
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

        _userDataManager.SaveUserData(
            targetUser,
            e.Item,
            targetData,
            UserDataSaveReason.Import,
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
