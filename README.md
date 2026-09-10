# <img src="https://raw.githubusercontent.com/NMe84/jellyfin-watch-state-sync-plugin/master/sync.png" height="32"> Jellyfin Watch State Sync Plugin

A native Jellyfin plugin that synchronises watch states between connected user groups for specific TV shows.

## Use case

When you're watching certain shows together with your partner or family, Jellyfin doesn't really cater to that by default. You can either use a shared account for shared watching or you have to deal with mismatching watch states between users.

This plugin allows you to connect two or more users together for specific shows. When you do, any change to watch states for that show will be synchronized between all connected users. It will no longer matter on which account you watch a show, you'll always be up to date!

## Features

- **Admin UI** – "Watch State Sync" entry in the Jellyfin admin sidebar.
  - List all connections, grouped by sync group (`Alice & Bob & Carol`).
  - Add, edit, or remove individual connections (sync group + show).
- **N-way sync** – when any connected user watches or marks an episode as (un)watched, all other users in the group are updated automatically.
- **Initial merge** – when a connection is first created, episode watch states are merged with a logical OR: any episode already watched by any member is immediately marked as watched for all others.
- **Persistence** – connections are stored in Jellyfin's standard plugin XML configuration; they survive server restarts.
- **Sync button** – a link icon button appears in the detail page action bar (alongside Play and Favourite) for any show, season, or episode you are synced on. Clicking it shows which users you are synced with. Requires the [JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) plugin. **If you don't need this feature, you don't need to install any other plugins to make this one work!**

---

## Requirements

| Requirement | Version |
|---|---|
| Jellyfin Server | **10.11.9+** or **Jellyfin 12** — one release ships builds for both |
| .NET SDK (build only) | 9.0 (Jellyfin 10.x) / 10.0 (Jellyfin 12) |
| [JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) | any (optional, for the sync button) |

---

## Building

1. [Install the .NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
2. Clone / open this repository
3. Run:

```bash
dotnet restore
dotnet build -c Release
```

The output DLL is at:
`bin/Release/net9.0/Jellyfin.Plugin.WatchSync.dll`

---

## Installation

### Via Jellyfin plugin catalog (recommended)

1. In your Jellyfin dashboard go to **Administration → Plugins → Catalog**.
2. Click the ⚙️ settings icon and then the **+** button to add a new repository.
3. Give it a name (e.g. `NMe84 Plugins`) and enter the URL:
   ```
   https://raw.githubusercontent.com/NMe84/jellyfin-plugins/gh-pages/manifest.json
   ```
4. Click **Save**.
5. Find **Watch State Sync** in the Catalog and click **Install**.
6. Restart Jellyfin to activate the plugin.
7. Navigate to **Administration → Watch State Sync** in the sidebar.

### Manual installation (advanced)

1. Build the project (see [Building](#building) above).
2. On your Jellyfin server, create a plugin directory named `WatchStateSync_<version>`,
   e.g. `/var/lib/jellyfin/plugins/WatchStateSync_1.0.0.0/`.
3. Copy `Jellyfin.Plugin.WatchSync.dll` into that directory.
4. Restart Jellyfin.

> **Tip:** The exact plugins path depends on your installation type. Check the *Plugins* page in the Jellyfin admin dashboard for the configured path.

---

## Admin UI

Open **Administration → Watch State Sync**. The **+ Add** button at the top opens a form where you select two or more users and a TV show. Each connection can be edited or deleted individually using the ✏️ and 🗑️ buttons.

| Connection | Actions |
|---|---|
| 🔗 **Alice & Bob** | |
| &emsp;Breaking Bad | ✏️ 🗑️ |
| &emsp;The Wire | ✏️ 🗑️ |
| 🔗 **Alice, Bob & Carol** | |
| &emsp;Chernobyl | ✏️ 🗑️ |

---

## Sync button (optional)

When the [Jellyfin JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) plugin is installed, a link icon (🔗) button is automatically added to the action bar on series, season, and episode detail pages for any item you are synced on. Clicking it shows a popup listing the users you are synced with for that show.

Watch State Sync detects the injector at startup via reflection and registers the script automatically — no manual steps needed. The button appears after a browser refresh.

The integration is fully opt-in: if the injector is not installed, Watch State Sync logs a debug message and moves on. All other features work regardless.

---

## How sync works

```
UserDataSaved event fires
  → reason is PlaybackFinished or TogglePlayed
  → item is an Episode
  → episode's series is listed in a connection where the triggering user appears
  → for each other user in the sync group:
      read their current UserData for that episode
      if Played state differs → write new state with reason=Import
      (Import reason is ignored by this handler → no loop)
```

Reentrancy is additionally guarded by an in-flight `HashSet<itemId:userId>` that prevents
double-writes if Jellyfin somehow fires multiple events for the same item concurrently.

---

## REST API

All endpoints require admin authentication (`RequiresElevation`) except `/connections/item/…/user/…`,
which requires any authenticated user (used by the sync button script).

| Method | Path | Description |
|--------|------|-------------|
| GET | `/WatchSync/connections` | List all connections |
| POST | `/WatchSync/connections` | Add a connection `{ users: [{id}], seriesId }` |
| PUT | `/WatchSync/connections/{id}` | Update a connection |
| DELETE | `/WatchSync/connections/{id}` | Delete one connection |
| GET | `/WatchSync/progress` | Episode watched/total counts for all connections |
| GET | `/WatchSync/users` | List all users |
| GET | `/WatchSync/series` | List all TV series |
| GET | `/WatchSync/connections/item/{itemId}/user/{userId}` | Get synced users for an item |

---

## Notes on Jellyfin package versions

The project compiles against **Jellyfin 10.11.10** packages (net9.0) for Jellyfin 10.x and **Jellyfin 12.0.0** packages (net10.0) for Jellyfin 12, and requires **10.11.9 or newer** at runtime on the 10.x line (`GetUsers()` was introduced in 10.11.9 as part of the EF Core refactor). If you're on a different version:

1. Open `Jellyfin.Plugin.WatchSync.csproj`.
2. Update the `Version` attribute on both `Jellyfin.Controller` and `Jellyfin.Model` package references.
3. Update `targetAbi` in `build.yaml` to match.

Available versions can be found on [NuGet](https://www.nuget.org/packages/Jellyfin.Controller).

---

## License

[MIT](LICENSE).
