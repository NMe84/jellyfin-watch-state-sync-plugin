using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WatchSync.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WatchSync.Controllers;

[ApiController]
[Route("WatchSync")]
[Produces("application/json")]
public class WatchSyncController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;

    public WatchSyncController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    // Jellyfin 10.11.x uses System.Text.Json (not Newtonsoft) for API responses,
    // which ignores [JsonProperty] attributes and defaults to PascalCase.
    // We project to anonymous types with explicit lowercase names so the output
    // is always what the JavaScript expects, regardless of serialiser config.
    private static object ToDto(UserConnection c) => new
    {
        id         = c.Id.ToString(),
        seriesId   = c.SeriesId.ToString(),
        seriesName = c.SeriesName,
        users      = c.Users.Select(u => new { id = u.Id.ToString(), name = u.Name }).ToList()
    };

    // -------------------------------------------------------------------------
    // Connection CRUD
    // -------------------------------------------------------------------------

    /// <summary>Returns all configured connections.</summary>
    [HttpGet("connections")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetConnections()
        => Ok(Config.Connections.Select(ToDto));

    /// <summary>
    /// Creates a new sync group. Supply a <see cref="UserConnection"/> with at least two
    /// entries in <c>Users</c> (only <c>Id</c> is required per entry) and the target
    /// <c>SeriesId</c>.
    /// </summary>
    [HttpPost("connections")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<UserConnection> AddConnection([FromBody] UserConnection connection)
    {
        var validationError = ValidateConnection(connection);
        if (validationError is not null)
            return BadRequest(validationError);

        NormalizeUsers(connection);

        if (IsDuplicate(connection, excludeId: null))
            return Conflict("A connection with this exact user group and series already exists.");

        connection.Id = Guid.NewGuid();
        EnrichDisplayNames(connection);

        Config.Connections.Add(connection);
        Plugin.Instance!.SaveConfiguration();

        MergeInitialWatchStates(connection);

        return CreatedAtAction(nameof(GetConnections), new { id = connection.Id }, ToDto(connection));
    }

    /// <summary>Updates an existing sync group and re-runs the initial watch-state merge.</summary>
    [HttpPut("connections/{id:guid}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserConnection> UpdateConnection(Guid id, [FromBody] UserConnection updated)
    {
        var validationError = ValidateConnection(updated);
        if (validationError is not null)
            return BadRequest(validationError);

        var existing = Config.Connections.FirstOrDefault(c => c.Id == id);
        if (existing is null)
            return NotFound();

        NormalizeUsers(updated);

        if (IsDuplicate(updated, excludeId: id))
            return Conflict("A connection with this exact user group and series already exists.");

        existing.Users = updated.Users;
        existing.SeriesId = updated.SeriesId;
        EnrichDisplayNames(existing);

        Plugin.Instance!.SaveConfiguration();

        MergeInitialWatchStates(existing);

        return Ok(ToDto(existing));
    }

    /// <summary>Deletes a single connection by ID.</summary>
    [HttpDelete("connections/{id:guid}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteConnection(Guid id)
    {
        var removed = Config.Connections.RemoveAll(c => c.Id == id);
        if (removed == 0)
            return NotFound();

        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    // -------------------------------------------------------------------------
    // Progress
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns watched/total episode counts for every configured connection.
    /// Because all members of a sync group are kept in sync, we only need to
    /// check one user's play state — we use the first user in the group.
    /// </summary>
    [HttpGet("progress")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetConnectionsProgress()
    {
        var result = new List<object>();

        foreach (var connection in Config.Connections)
        {
            var firstUser = connection.Users.FirstOrDefault();
            if (firstUser is null)
            {
                result.Add(new { connectionId = connection.Id.ToString(), watched = 0, total = 0 });
                continue;
            }

            var user = _userManager.GetUserById(firstUser.Id);
            if (user is null)
            {
                result.Add(new { connectionId = connection.Id.ToString(), watched = 0, total = 0 });
                continue;
            }

            // GetUserDataBatch was removed in 10.11.x; use two filtered queries instead.
            // IsMissing = false excludes virtual/stub episodes that Jellyfin adds for
            // expected-but-not-yet-downloaded content (e.g. an upcoming season), which
            // would otherwise inflate the total count.
            var total = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                ParentId         = connection.SeriesId,
                Recursive        = true,
                IsMissing        = false
            }).Count;

            var watched = total > 0
                ? _libraryManager.GetItemList(new InternalItemsQuery
                  {
                      IncludeItemTypes = new[] { BaseItemKind.Episode },
                      ParentId         = connection.SeriesId,
                      Recursive        = true,
                      IsPlayed         = true,
                      IsMissing        = false,
                      User             = user
                  }).Count
                : 0;

            result.Add(new { connectionId = connection.Id.ToString(), watched, total });
        }

        return Ok(result);
    }

    // -------------------------------------------------------------------------
    // Helpers for the admin UI dropdowns
    // -------------------------------------------------------------------------

    /// <summary>Returns all Jellyfin users (for the connection form selects).</summary>
    [HttpGet("users")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetUsers()
    {
        var users = _userManager.GetUsers()
            .Select(u => new { id = u.Id, name = u.Username })
            .OrderBy(u => u.name)
            .ToList();

        return Ok(users);
    }

    /// <summary>Returns all TV series in the library (for the connection form select).</summary>
    [HttpGet("series")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetSeries()
    {
        // Filter at the database level so only Series entities are deserialised.
        // Querying all items throws when the library contains entries whose type
        // cannot be deserialised (e.g. left-over items from uninstalled plugins).
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true
        };

        var series = _libraryManager.GetItemList(query)
            .OfType<Series>()
            .OrderBy(s => s.SortName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new { id = s.Id, name = s.Name })
            .ToList();

        return Ok(series);
    }

    // -------------------------------------------------------------------------
    // Called by the chain-icon client-side script (accessible to all users)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns all users that are synced with <paramref name="userId"/> for the series
    /// that contains <paramref name="itemId"/> (series, season, or episode).
    /// </summary>
    [HttpGet("connections/item/{itemId:guid}/user/{userId:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetConnectionsForItem(Guid itemId, Guid userId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
            return Ok(Array.Empty<object>());

        var seriesId = item switch
        {
            Episode ep     => ep.SeriesId,
            Season season  => season.SeriesId,
            Series series  => series.Id,
            _              => item.Id
        };

        var connected = Config.Connections
            .Where(c => c.SeriesId == seriesId && c.Users.Any(u => u.Id == userId))
            .SelectMany(c => c.Users.Where(u => u.Id != userId))
            .GroupBy(u => u.Id)
            .Select(g => new { userId = g.Key, userName = g.First().Name })
            .ToList();

        return Ok(connected);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static string? ValidateConnection(UserConnection connection)
    {
        if (connection.Users.Count < 2)
            return "A sync group must contain at least two users.";

        var ids = connection.Users.Select(u => u.Id).ToList();

        if (ids.Distinct().Count() != ids.Count)
            return "A sync group cannot contain duplicate users.";

        return null;
    }

    /// <summary>Sorts users by ID so identical groups always have the same canonical order.</summary>
    private static void NormalizeUsers(UserConnection connection)
    {
        connection.Users = connection.Users.OrderBy(u => u.Id).ToList();
    }

    private static bool IsDuplicate(UserConnection connection, Guid? excludeId)
    {
        var sorted = connection.Users.Select(u => u.Id).OrderBy(id => id).ToList();

        return Config.Connections.Any(c =>
            c.Id != (excludeId ?? Guid.Empty) &&
            c.SeriesId == connection.SeriesId &&
            c.Users.Select(u => u.Id).OrderBy(id => id).SequenceEqual(sorted));
    }

    /// <summary>
    /// One-time OR-merge when a connection is created or edited: for every episode in
    /// the series, if ANY user in the group has watched it, mark it as watched for ALL
    /// users who have not.
    /// </summary>
    private void MergeInitialWatchStates(UserConnection connection)
    {
        var users = connection.Users
            .Select(u => _userManager.GetUserById(u.Id))
            .Where(u => u is not null)
            .ToList();

        if (users.Count < 2)
            return;

        var episodes = _libraryManager
            .GetItemList(new InternalItemsQuery { ParentId = connection.SeriesId, Recursive = true })
            .OfType<Episode>()
            .ToList();

        foreach (var episode in episodes)
        {
            var states = users
                .Select(u => new { User = u!, Data = _userDataManager.GetUserData(u!, episode) })
                .ToList();

            if (!states.Any(s => s.Data.Played))
                continue;

            // Use the first watched entry as the source for timestamps.
            var reference = states.First(s => s.Data.Played);

            foreach (var s in states.Where(s => !s.Data.Played))
            {
                s.Data.Played = true;
                s.Data.PlayCount = Math.Max(s.Data.PlayCount, 1);
                s.Data.LastPlayedDate ??= reference.Data.LastPlayedDate;
                _userDataManager.SaveUserData(
                    s.User,
                    episode,
                    s.Data,
                    UserDataSaveReason.Import,
                    CancellationToken.None);
            }
        }
    }

    private void EnrichDisplayNames(UserConnection connection)
    {
        foreach (var entry in connection.Users)
        {
            entry.Name = _userManager.GetUserById(entry.Id)?.Username ?? entry.Id.ToString();
        }

        var series = _libraryManager.GetItemById(connection.SeriesId);
        connection.SeriesName = series?.Name ?? connection.SeriesId.ToString();
    }
}
