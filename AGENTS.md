# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
# Run the full app (WPF shell + Kestrel API + compiled React)
dotnet run --project src/MusicBot.Desktop

# Build the full solution (also runs npm install && npm run build via MSBuild target)
dotnet build MusicBot.sln

# React dev server with hot-reload (requires backend running separately)
cd src/MusicBot.Web/clientapp && npm start   # http://localhost:5173

# Build React only
cd src/MusicBot.Web/clientapp && npm run build   # output → src/MusicBot.Web/wwwroot

# TypeScript type-check without building
cd src/MusicBot.Web/clientapp && npx tsc --noEmit

# NUKE build (generates installer via Velopack)
./build.cmd --target Pack --configuration Release --runtime win-x86 --version 1.2.3
```

**`wwwroot/` is fully generated** — never edit files there directly. The source is `clientapp/src/`.

**Overlay HTML files** live in `clientapp/public/overlays/` and are copied to `wwwroot/overlays/` at build time. They are standalone static pages, not part of the React bundle.

## Architecture

### Three-Project Solution

- **`MusicBot.Core`** — Pure domain: interfaces (`IQueueService`, `ILocalPlayerService`, `ISpotifyService`, `IChatAdapter`, `IMetadataService`, `ILocalLibraryService`) and models (`Song`, `QueueItem`, `QueueState`, `BotCommand`, `CommandResult`, `CachedTrack`, `PlayedSong`, `AutoQueueSong`). No external dependencies.
- **`MusicBot.Web`** — ASP.NET Core 9 API + React SPA. Contains all controllers, services, EF Core, SignalR hub. Compiled as a **library** (DLL) embedded into Desktop. Listens on `http://127.0.0.1:3050`.
- **`MusicBot.Desktop`** — WPF entry point. Starts Kestrel via `WebHost`, hosts the UI in WebView2. Contains `TikTokLoginWindow` (cookie-based auth via WebView2), `LogViewerWindow`, and tray icon logic.

### Local Playback (Not Spotify Streaming)

Audio is played locally via **NAudio/WASAPI**, not streamed from Spotify. The flow:

1. `CommandRouterService` receives a play command → calls `IQueueService.Enqueue(song)`
2. `PlaybackSyncService` (polls every 500ms) detects the queue advanced → calls `YtDlpDownloaderService.DownloadAsync(song)`
3. `YtDlpDownloaderService` runs `yt-dlp` as a subprocess, emits `download:progress` events via SignalR during download
4. Once downloaded, `LocalPlayerService` plays the file via WASAPI
5. `PlaybackSyncService` detects track end → auto-advances queue

Spotify is used only for **search metadata enrichment** (clean titles/covers) and optional OAuth login — not for actual audio playback.

### Multi-User Architecture

`UserContextManager` (singleton) maps `Guid userId → UserServices` in a `ConcurrentDictionary`. Each `UserServices` holds a user's `IQueueService` and `ISpotifyService`. In Desktop mode there is always exactly one user: `LocalUser.Id` (hardcoded GUID in `LocalUser.cs`).

Controllers call `_userContext.GetOrCreate(LocalUser.Id)` — do not hardcode user resolution differently.

### Authentication

Dual-scheme "Smart" policy: requests with `X-Api-Key` header use `ApiKeyAuthHandler`; others use JWT Bearer. Both schemes resolve to the same `ClaimsPrincipal` shape. Public queue endpoints (`/api/queue/~/...`) bypass auth entirely.

### Search Blending (`CommandsController.Search`)

YouTube (via yt-dlp subprocess, 7s timeout) is the **primary source** — results contain the actual video URI needed for download. iTunes + Spotify run in parallel for clean metadata. YouTube results are enriched with metadata where title/artist match; remaining slots filled with metadata-only results. The limit parameter is honored across the merged list.

### Background Services

All registered via `AddHostedService<T>()` in `WebHost.cs`:

| Service | What it does |
|---------|-------------|
| `PlaybackSyncService` | Polls player state every 500ms. Auto-advances queue, triggers downloads, fires SignalR broadcasts. |
| `SignalRBroadcastService` | Subscribes to internal queue/player change events and pushes to `OverlayHub`. |
| `QueuePersistenceService` | Saves/restores the in-memory queue to SQLite across restarts. |
| `YtDlpSetupService` | Ensures yt-dlp binary is available on startup. |
| `PlatformAutoConnectService` | Reconnects TikTok/Twitch/Kick on startup if previously connected. |

### SignalR (`OverlayHub` at `/hub/overlay`)

Clients call `JoinUserGroup(overlayToken)` after connecting. Events emitted by the server:

`queue:updated` · `download:started` · `download:progress` · `download:done` · `queue:download-failed` · `integration:event` · `platform:status` · `settings:updated` · `ticker:updated`

The `useSignalR` hook in the frontend handles all of these with automatic reconnect (backoff: 0s → 2s → 5s → 10s → 30s).

### Database

SQLite via EF Core. **No migrations** — schema is managed by `Database.EnsureCreated()` on startup. To apply schema changes in development: delete `musicbot.db` and restart.

Tables: `Users`, `ApiKeys`, `SpotifyTokens`, `CachedTracks`, `PlayedSongs`, `AutoQueueSongs`, `PersistedQueueItems`, `PlatformConfigs`, `TickerMessages`, `BannedSongs`, `QueueSettings`.

The **queue itself is in-memory** (`QueueService`). `QueuePersistenceService` snapshots it to `PersistedQueueItems`.

### Relay OAuth (`relay/`)

A Cloudflare Worker that proxies OAuth token exchange so `client_secret` values never exist in the desktop app. MusicBot sends an `X-Relay-Key` header; the relay adds `client_id`/`client_secret` and forwards to Spotify/Twitch/Kick. Configured via `Relay:Url` and `Relay:ApiKey` in user secrets.

### Frontend State

All real-time state flows through `useSignalR`. The `Dashboard` page is the single top-level component — it owns all handler callbacks (`handleSkip`, `handleRemove`, `handleReorder`, `handleBan`, `handleAddToAutoQueue`, etc.) and passes them down as props. There is no global state manager (no Redux/Zustand).

API calls are all in `services/api.ts`. TypeScript interfaces for all API responses are in `types/models.ts`.

### Configuration

`appsettings.json` contains all `ClientId` values and `Relay:Url`/`Relay:ApiKey` — these are public/distributed values bundled with the app. **Only `ClientSecret` values** must be set via `dotnet user-secrets --project src/MusicBot.Web` (they live in the Cloudflare relay and are never needed by end users; only the developer running the relay needs them locally).

## API Docs

Interactive: `http://127.0.0.1:3050/scalar/v1` · OpenAPI JSON: `http://127.0.0.1:3050/openapi/v1.json`
