# MusicBot

Bot de solicitudes de canciones para streamers. Gestiona una cola en tiempo real, descarga audio con yt-dlp, se integra con TikTok Live, Twitch y Kick, y expone overlays para OBS.

**Stack:** ASP.NET Core 9 + WPF (Desktop shell) + React 19 + TypeScript + SQLite + SignalR + NAudio

---

## Índice

- [Requisitos previos](#requisitos-previos)
- [Configuración inicial](#configuración-inicial)
- [Comandos de desarrollo](#comandos-de-desarrollo)
- [Arquitectura](#arquitectura)
- [Estructura del proyecto](#estructura-del-proyecto)
- [Backend — Servicios](#backend--servicios)
- [Backend — API Reference](#backend--api-reference)
- [Frontend — Componentes](#frontend--componentes)
- [Base de datos](#base-de-datos)
- [Real-time (SignalR)](#real-time-signalr)
- [Relay OAuth](#relay-oauth)
- [Build y distribución](#build-y-distribución)
- [Flujo de contribución](#flujo-de-contribución)

---

## Requisitos previos

| Herramienta | Versión mínima | Propósito |
|-------------|---------------|-----------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 9.0 | Compilar y ejecutar el backend/desktop |
| [Node.js](https://nodejs.org) | 20 LTS | Compilar el frontend React |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | última | Descargar audio de YouTube (debe estar en PATH o en `MusicLibrary:YtDlpPath`) |
| Windows 10/11 x86-64 | — | WPF + WebView2 + WASAPI solo funcionan en Windows |

> **WebView2** se incluye con Windows 11 y se instala automáticamente en Windows 10 si no está presente.

---

## Configuración inicial

### 1. Clonar el repositorio

```bash
git clone https://github.com/overabstractor/MusicBot.git
cd MusicBot
```

### 2. Credenciales de plataformas (user secrets)

**Nunca** pongas credenciales en `appsettings.json`. Usa el gestor de user secrets de .NET:

```bash
# Spotify — https://developer.spotify.com/dashboard
dotnet user-secrets set "Spotify:ClientId"     "TU_CLIENT_ID"     --project src/MusicBot.Web
dotnet user-secrets set "Spotify:ClientSecret" "TU_CLIENT_SECRET" --project src/MusicBot.Web

# Twitch — https://dev.twitch.tv/console/apps
dotnet user-secrets set "Twitch:ClientId"     "TU_CLIENT_ID"     --project src/MusicBot.Web
dotnet user-secrets set "Twitch:ClientSecret" "TU_CLIENT_SECRET" --project src/MusicBot.Web

# Kick — https://kick.com/settings/developer
dotnet user-secrets set "Kick:ClientId"     "TU_CLIENT_ID"     --project src/MusicBot.Web
dotnet user-secrets set "Kick:ClientSecret" "TU_CLIENT_SECRET" --project src/MusicBot.Web

# Relay OAuth (ver sección Relay OAuth)
dotnet user-secrets set "Relay:Url"    "https://tu-relay.workers.dev" --project src/MusicBot.Web
dotnet user-secrets set "Relay:ApiKey" "TU_RELAY_API_KEY"             --project src/MusicBot.Web
```

### 3. Instalar dependencias del frontend

```bash
cd src/MusicBot.Web/clientapp
npm install
```

---

## Comandos de desarrollo

```bash
# Ejecutar la app completa (WPF + API + React compilado)
dotnet run --project src/MusicBot.Desktop

# Compilar la solución completa (también compila el frontend automáticamente)
dotnet build MusicBot.sln

# Frontend en modo dev con hot-reload (requiere que el backend esté corriendo)
cd src/MusicBot.Web/clientapp
npm start                   # http://localhost:5173 — proxy a http://127.0.0.1:3050

# Solo compilar el frontend
npm run build               # salida a src/MusicBot.Web/wwwroot

# Tests del frontend
npm test
```

> Al correr `dotnet build`, el MSBuild target del csproj ejecuta `npm install && npm run build` automáticamente, copiando el resultado a `wwwroot`.

### URLs en desarrollo

| URL | Qué es |
|-----|--------|
| `http://127.0.0.1:3050` | API y SPA en producción (dentro del Desktop) |
| `http://localhost:5173` | Dev server de Vite con hot-reload |
| `http://127.0.0.1:3050/scalar/v1` | Docs interactivos de la API (Scalar UI) |
| `http://127.0.0.1:3050/openapi/v1.json` | OpenAPI spec JSON |
| `http://127.0.0.1:3050/hub/overlay` | WebSocket SignalR para overlays |

---

## Arquitectura

```
┌─────────────────────────────────────────────────────────────┐
│  MusicBot.Desktop  (WPF + WebView2)                         │
│  ┌─────────────────────────────────────────────────────┐    │
│  │  MusicBot.Web  (ASP.NET Core 9 — Kestrel)           │    │
│  │  ┌──────────────┐  ┌──────────┐  ┌───────────────┐  │    │
│  │  │  Controllers │  │ Services │  │  SignalR Hub  │  │    │
│  │  └──────┬───────┘  └────┬─────┘  └───────┬───────┘  │    │
│  │         │               │                │           │    │
│  │  ┌──────▼───────────────▼────────────────▼───────┐  │    │
│  │  │           MusicBot.Core                       │  │    │
│  │  │  IQueueService · ISpotifyService · IChatAdapter│  │    │
│  │  └───────────────────────────────────────────────┘  │    │
│  │                         │                           │    │
│  │  ┌──────────────────────▼───────────────────────┐  │    │
│  │  │  SQLite (EF Core) · wwwroot (React SPA)      │  │    │
│  │  └──────────────────────────────────────────────┘  │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
         │                                   │
         ▼                                   ▼
  Browser/OBS Overlay              Cloudflare Worker
  (SignalR client)                 (OAuth Relay)
```

### Principio clave: aislamiento por usuario

Cada usuario registrado tiene su propio conjunto de servicios:

```
UserContextManager (singleton)
  └── ConcurrentDictionary<Guid, UserServices>
        └── UserServices
              ├── IQueueService    (cola en memoria)
              └── ISpotifyService  (conexión OAuth de Spotify)
```

Los controladores resuelven el usuario desde el JWT/API key y llaman a `UserContextManager.GetOrCreate(userId)`.

### Flujo de una solicitud de canción

```
Chat (TikTok/Twitch/Kick/Web)
  → IChatAdapter.OnCommandReceived()
  → CommandRouterService.HandleAsync(BotCommand)
  → IQueueService.Enqueue(song)
  → YtDlpDownloaderService (descarga en background)
  → PlaybackSyncService (detecta que la cola avanzó)
  → LocalPlayerService (reproduce con NAudio/WASAPI)
  → SignalRBroadcastService → OverlayHub → Overlays OBS
```

---

## Estructura del proyecto

```
MusicBot/
├── src/
│   ├── MusicBot.Core/              # Dominio puro — sin dependencias externas
│   │   ├── Interfaces/
│   │   │   ├── IChatAdapter.cs
│   │   │   ├── ILocalLibraryService.cs
│   │   │   ├── ILocalPlayerService.cs
│   │   │   ├── IMetadataService.cs
│   │   │   ├── IQueueService.cs
│   │   │   └── ISpotifyService.cs
│   │   ├── Models/
│   │   │   ├── Song.cs             # Entidad base: uri, title, artist, coverUrl, durationMs
│   │   │   ├── QueueItem.cs        # Song + requestedBy + platform + addedAt
│   │   │   ├── QueueState.cs       # nowPlaying + upcoming[]
│   │   │   ├── AppUser.cs          # Usuario registrado
│   │   │   ├── BotCommand.cs       # Comando de chat (play/skip/bump...)
│   │   │   ├── CommandResult.cs    # Resultado de un comando
│   │   │   ├── CachedTrack.cs      # Track descargado en disco
│   │   │   ├── PlayedSong.cs       # Historial de reproducciones
│   │   │   ├── AutoQueueSong.cs    # Pool de autocola
│   │   │   ├── PersistedQueueItem.cs
│   │   │   ├── SpotifyToken.cs
│   │   │   └── UserApiKey.cs
│   │   └── Services/
│   │       └── QueueService.cs     # Implementación en memoria de IQueueService
│   │
│   ├── MusicBot.Web/               # API + SPA + lógica de negocio
│   │   ├── Controllers/            # 16 controladores (ver API Reference)
│   │   ├── Services/               # 30+ servicios (ver sección Servicios)
│   │   ├── Hubs/
│   │   │   └── OverlayHub.cs       # SignalR hub para overlays OBS
│   │   ├── Data/
│   │   │   ├── MusicBotDbContext.cs
│   │   │   └── PlatformConfig.cs
│   │   ├── clientapp/              # React 19 + TypeScript (Vite)
│   │   │   ├── src/
│   │   │   │   ├── pages/
│   │   │   │   │   └── Dashboard.tsx
│   │   │   │   ├── components/     # 13 componentes (ver sección Frontend)
│   │   │   │   ├── hooks/
│   │   │   │   │   ├── useSignalR.ts
│   │   │   │   │   └── useTheme.ts
│   │   │   │   ├── services/
│   │   │   │   │   └── api.ts      # Cliente HTTP para todos los endpoints
│   │   │   │   ├── types/
│   │   │   │   │   └── models.ts   # Interfaces TypeScript de la API
│   │   │   │   ├── App.css         # Estilos globales (dark/light theme)
│   │   │   │   ├── App.tsx
│   │   │   │   ├── index.tsx
│   │   │   │   └── utils.ts
│   │   │   └── public/
│   │   │       └── overlays/       # HTML/CSS/JS para overlays OBS (estáticos)
│   │   ├── wwwroot/                # Salida compilada de Vite (generado — no editar)
│   │   ├── WebHost.cs              # Registro de servicios y pipeline HTTP
│   │   ├── LocalUser.cs            # Usuario local único (modo desktop)
│   │   ├── AppEvents.cs            # Eventos estáticos entre Web y Desktop
│   │   ├── GlobalUsings.cs
│   │   └── appsettings.json
│   │
│   └── MusicBot.Desktop/           # Shell WPF
│       ├── Program.cs              # Entry point + Velopack updater
│       ├── App.xaml.cs
│       ├── MainWindow.xaml.cs      # WebView2 apuntando a Kestrel
│       ├── LogViewerWindow.xaml.cs
│       ├── TikTokLoginWindow.xaml.cs # Login de TikTok via WebView2
│       ├── TrayLifetime.cs         # Ícono en bandeja del sistema
│       └── LogSink.cs              # Serilog sink → ventana de logs
│
├── relay/                          # Cloudflare Worker (OAuth proxy)
│   ├── src/index.ts
│   ├── wrangler.toml
│   └── README.md                   # Instrucciones de despliegue del relay
│
├── build/
│   └── Build.cs                    # NUKE build (Clean/Compile/Publish/Pack)
│
├── docs/                           # Páginas web públicas (GitHub Pages)
│   ├── index.html
│   ├── tos.html
│   └── privacy.html
│
├── MusicBot.sln
├── CLAUDE.md                       # Guía para Claude Code
└── README.md                       # Este archivo
```

---

## Backend — Servicios

### Núcleo

| Servicio | Tipo | Descripción |
|----------|------|-------------|
| `UserContextManager` | Singleton | Registro central de usuarios activos. Mapea `Guid userId → UserServices`. |
| `QueueService` | Por usuario | Cola en memoria: enqueue, dequeue, reorder, skip, revoke. |
| `CommandRouterService` | Singleton | Despacha `BotCommand` (play/skip/bump) al servicio correcto del usuario. |
| `PlaybackSyncService` | BackgroundService | Sondea el estado de reproducción cada 500ms. Auto-avanza la cola cuando termina una canción o se detecta skip externo. |
| `SignalRBroadcastService` | BackgroundService | Suscribe cambios de cola/reproductor y los publica al hub `OverlayHub`. |
| `QueuePersistenceService` | BackgroundService | Persiste y restaura la cola desde SQLite entre reinicios. |

### Descarga y reproducción

| Servicio | Descripción |
|----------|-------------|
| `YtDlpDownloaderService` | Gestiona un proceso `yt-dlp`. Busca en YouTube, descarga audio, emite eventos de progreso via SignalR. |
| `YtDlpSetupService` | BackgroundService que verifica la disponibilidad de yt-dlp al arrancar. |
| `LocalPlayerService` | Reproduce audio via WASAPI (NAudio). Play/pause/stop/seek/volume/cambio de dispositivo. |
| `LocalLibraryService` | Gestiona el catálogo de tracks descargados en disco. |
| `ItunesMetadataService` | Busca metadatos limpios (portada, duración) en la API pública de iTunes. |

### Integración con plataformas

| Servicio | Descripción |
|----------|-------------|
| `TikTokService` | Escucha el chat de TikTok Live via `TikTokLive_Sharp`. Detecta comandos `!play`, regalos (coins) y suscripciones. |
| `TwitchService` | Bot de Twitch via `TwitchLib`. Escucha `!play` en el chat. |
| `KickService` | Integración con Kick.com via `KickChatSpy`. |
| `ChatResponseService` | Formatea y envía respuestas al chat (confirmación de canción agregada, errores). |
| `PlatformConnectionManager` | Gestiona el ciclo de vida (conectar/desconectar) de cada plataforma. |
| `PlatformAutoConnectService` | BackgroundService que reconecta automáticamente al arrancar. |
| `IntegrationStatusTracker` | Emite el estado de conexión de cada plataforma al frontend via SignalR. |

### Auth de plataformas (OAuth)

| Servicio | Descripción |
|----------|-------------|
| `SpotifyService` | OAuth con Spotify. Gestiona tokens, refresh, y búsqueda de canciones. |
| `TwitchAuthService` | Maneja el callback OAuth de Twitch. |
| `KickAuthService` | Maneja el callback OAuth de Kick con PKCE. |
| `TikTokAuthService` | Login basado en cookies de sesión (WebView2 en Desktop). |

### Cola avanzada

| Servicio | Descripción |
|----------|-------------|
| `AutoQueueService` | Pool de hasta 100 canciones que se reproducen automáticamente cuando la cola está vacía. |
| `QueueSettingsService` | Persiste la configuración de la cola (límites, votación, comprobación de presencia). |
| `BannedSongService` | Blacklist de canciones que los usuarios no pueden solicitar. |
| `PresenceCheckService` | Confirma que el usuario solicitante sigue en el chat antes de reproducir. |
| `KickVoteService` | Suma y valida votos de skip (`!si`/`!no`). |
| `TickerMessageService` | Lista de mensajes en memoria para el ticker de overlays. |

---

## Backend — API Reference

La API completa está disponible en formato interactivo en `/scalar/v1` cuando el servidor está corriendo.

### Autenticación

La API usa un esquema "Smart" que acepta dos mecanismos:
- **JWT Bearer** — `Authorization: Bearer <token>` (obtenido en `/api/auth/login`)
- **API Key** — `X-Api-Key: <key>` (creada desde el panel de configuración)

Los endpoints de cola pública (`/api/queue/~/...`) no requieren autenticación.

### Endpoints principales

#### Auth & OAuth — `/api/auth`

| Método | Ruta | Descripción |
|--------|------|-------------|
| `GET` | `/me` | Usuario actual + estado de Spotify |
| `GET` | `/spotify` | Inicia flujo OAuth con Spotify |
| `GET` | `/spotify/callback` | Callback de Spotify |
| `DELETE` | `/spotify` | Desconectar Spotify |
| `GET` | `/twitch` | Inicia flujo OAuth con Twitch |
| `GET` | `/twitch/callback` | Callback de Twitch |
| `DELETE` | `/twitch` | Desconectar Twitch |
| `GET` | `/kick` | Inicia flujo OAuth con Kick |
| `GET` | `/kick/callback` | Callback de Kick |
| `DELETE` | `/kick` | Desconectar Kick |
| `POST` | `/tiktok/start` | Inicia sesión TikTok (abre WebView2) |

#### Cola — `/api/queue`

| Método | Ruta | Descripción |
|--------|------|-------------|
| `GET` | `/` | Estado completo de la cola |
| `GET` | `/now-playing` | Canción actual |
| `GET` | `/~/now-playing` | Now playing público (sin auth) |
| `DELETE` | `/item` | Eliminar canción de la cola |
| `POST` | `/move` | Reordenar (drag & drop) |
| `POST` | `/play-now` | Insertar al frente de la cola |
| `POST` | `/enqueue` | Agregar al final de la cola |
| `POST` | `/import-playlist` | Importar playlist de YouTube/Spotify |
| `POST` | `/start-auto` | Forzar inicio de autocola |

#### Comandos — `/api`

| Método | Ruta | Descripción |
|--------|------|-------------|
| `GET` | `/search?q=&limit=` | Búsqueda mezclada YouTube + iTunes + Spotify |
| `POST` | `/play` | Buscar y encolar una canción |
| `POST` | `/skip` | Saltar la canción actual |
| `POST` | `/pause` | Pausar reproducción |
| `POST` | `/resume` | Reanudar reproducción |
| `POST` | `/vote` | Emitir voto de skip (`!si`/`!no`) |
| `POST` | `/queue/gift-bump` | Simular bump por regalo (coins) |

#### Reproductor — `/api/player`

| Método | Ruta | Descripción |
|--------|------|-------------|
| `POST` | `/volume` | Cambiar volumen (0.0–1.0) |
| `POST` | `/seek` | Saltar a posición (ms) |
| `GET` | `/devices` | Listar dispositivos de audio WASAPI |
| `POST` | `/device` | Cambiar dispositivo de salida |

#### Autocola — `/api/autoqueue`

| Método | Ruta | Descripción |
|--------|------|-------------|
| `GET` | `/` | Listar canciones del pool |
| `POST` | `/` | Agregar canción al pool |
| `DELETE` | `/{uri}` | Eliminar canción del pool |
| `DELETE` | `/` | Vaciar el pool |
| `POST` | `/import` | Importar playlist al pool |

#### Otros

| Ruta | Descripción |
|------|-------------|
| `GET /api/history` | Historial de canciones reproducidas |
| `DELETE /api/history` | Limpiar historial |
| `GET /api/library` | Librería de tracks descargados |
| `DELETE /api/library/{id}` | Eliminar track de la librería |
| `GET /api/banned` | Canciones baneadas |
| `POST /api/banned` | Banear canción |
| `GET /api/settings` | Configuración de la cola |
| `PUT /api/settings` | Actualizar configuración |
| `GET /api/platforms` | Estado de plataformas conectadas |
| `PUT /api/platforms/{platform}` | Guardar config de plataforma |
| `GET /api/ticker` | Mensajes del ticker |
| `POST /api/ticker` | Crear mensaje |
| `GET /health` | Health check |

---

## Frontend — Componentes

La SPA es una aplicación de página única con una página principal (`Dashboard`) y navegación por tabs.

### Dashboard (`pages/Dashboard.tsx`)

Página principal. Gestiona el estado global (cola, now playing, settings, tabs) y pasa handlers a los componentes hijos.

**Tabs disponibles:** Cola · Historial · Librería · Auto Cola · Plataformas · Overlays · Ajustes · Mensajes

### Componentes

| Componente | Props clave | Descripción |
|------------|-------------|-------------|
| `NowPlaying` | `state`, `onSkip`, `onPause`, `onResume`, `onAddToAutoQueue` | Tarjeta del reproductor. Barra de progreso con seek, control de volumen, selector de dispositivo de audio. |
| `QueueList` | `items`, `onRemove`, `onReorder`, `onBan`, `onAddToAutoQueue` | Lista de la cola con drag & drop para reordenar. Muestra progreso de descarga por canción. |
| `AddSong` | `onAdded` | Caja de búsqueda. Muestra resultados con botones "▶ Ahora" y "+ Cola". |
| `SongHistory` | `refreshKey`, `onAddToAutoQueue` | Historial filtrable. Permite reproducir, encolar y agregar a autocola desde el historial. |
| `AutoQueuePanel` | `nowPlaying` | Pool de autocola: buscar/agregar canciones, importar playlists, acceso rápido a la canción en curso. |
| `Library` | — | Librería de tracks descargados. Muestra estadísticas de reproducción y permite eliminar archivos. |
| `PlatformConnections` | `tiktokEvents`, `twitchEvents`, `kickEvents` | Conectar/desconectar TikTok, Twitch y Kick. Log de eventos de chat en tiempo real. |
| `OverlayLinks` | — | URLs públicas para añadir como fuente en OBS. |
| `SettingsPanel` | — | Ajustes de cola, votación, presencia, autocola y desktop. |
| `QueueToolsModal` | `open`, `onClose`, simulación props | Modal con importación de playlist y herramientas de simulación (votos, regalos). |
| `TickerMessages` | — | CRUD de mensajes del ticker del overlay. |
| `StatusBar` | `connected`, estados de plataformas | Indicadores de estado en la cabecera. |

### Hooks

#### `useSignalR(overlayToken)`

Conecta al hub `/hub/overlay` y mantiene el estado en tiempo real. Devuelve:

```typescript
{
  nowPlaying: NowPlayingState | null,
  appQueue: QueueItem[],
  connected: boolean,
  tiktokStatus: PlatformState | null,
  twitchStatus: PlatformState | null,
  kickStatus: PlatformState | null,
  integrationEvents: IntegrationEvent[],
  queueSettings: QueueSettings,
  tickerMessages: TickerMessage[],
  queueUpdateCount: number,
  downloadStates: Record<string, DownloadState>,
  downloadErrors: DownloadError[],
  dismissDownloadError: (id: string) => void,
}
```

Gestiona reconexión automática con backoff `[0s, 2s, 5s, 10s, 30s]`.

#### `useTheme()`

Toggle de tema claro/oscuro persistido en `localStorage`.

### Cliente HTTP (`services/api.ts`)

Wrapper sobre `fetch` con URL base desde `VITE_API_URL` (o relativo en producción). Incluye métodos para cada endpoint de la API.

```typescript
// Ejemplos de uso
await api.search("queen bohemian", 10);
await api.play("bohemian rhapsody queen", "Admin", "web");
await api.skip("Admin");
await api.addAutoQueueSong({ spotifyUri, title, artist, durationMs });
await api.importPlaylist("https://www.youtube.com/playlist?list=...", "Admin");
```

---

## Base de datos

SQLite via EF Core. El archivo `musicbot.db` se crea automáticamente con `Database.EnsureCreated()` al arrancar — **no hay migraciones**.

> Para cambios de esquema en desarrollo: borra `musicbot.db` y reinicia. La app recreará todas las tablas.

### Tablas

| Tabla | Modelo | Descripción |
|-------|--------|-------------|
| `Users` | `AppUser` | Usuarios registrados (username, slug, password hash, overlay token) |
| `ApiKeys` | `UserApiKey` | API keys por usuario |
| `SpotifyTokens` | `SpotifyToken` | Access token + refresh token de Spotify |
| `CachedTracks` | `CachedTrack` | Canciones descargadas (metadata + ruta de archivo) |
| `PlayedSongs` | `PlayedSong` | Historial de reproducciones |
| `AutoQueueSongs` | `AutoQueueSong` | Pool de autocola (máx. 100) |
| `PersistedQueueItems` | `PersistedQueueItem` | Cola guardada entre reinicios |
| `PlatformConfigs` | `PlatformConfig` | Config de TikTok/Twitch/Kick por usuario |
| `TickerMessages` | `TickerMessage` | Mensajes del ticker de overlays |
| `BannedSongs` | — | Canciones baneadas |
| `QueueSettings` | — | Configuración persistida de la cola |

---

## Real-time (SignalR)

El hub `OverlayHub` en `/hub/overlay` es el canal de comunicación en tiempo real.

### Conectarse (cliente)

```typescript
const connection = new HubConnectionBuilder()
  .withUrl("/hub/overlay")
  .build();

await connection.start();
await connection.invoke("JoinUserGroup", overlayToken);
```

### Eventos que emite el servidor

| Evento | Payload | Cuándo |
|--------|---------|--------|
| `queue:updated` | `QueueState` | La cola o el now-playing cambian |
| `download:started` | `{ spotifyUri, title, artist }` | Inicia descarga de un track |
| `download:progress` | `{ spotifyUri, pct }` | Progreso de descarga (0–100) |
| `download:done` | `{ spotifyUri }` | Descarga completada |
| `queue:download-failed` | `{ spotifyUri, title, artist, error }` | Error al descargar |
| `integration:event` | `IntegrationEvent` | Acción en chat (play, gift, sub) |
| `platform:status` | `PlatformState` | Cambio de estado de una plataforma |
| `settings:updated` | `QueueSettings` | Configuración de cola actualizada |
| `ticker:updated` | `TickerMessage[]` | Lista de mensajes del ticker |

---

## Relay OAuth

El relay es un Cloudflare Worker que actúa como proxy OAuth para que `client_secret` nunca esté en la app de escritorio.

```
App Desktop  →  relay (X-Relay-Key)  →  Spotify/Twitch/Kick OAuth
               (client_secret guardado en Cloudflare, nunca en código)
```

Ver [`relay/README.md`](relay/README.md) para instrucciones completas de despliegue.

**Configuración mínima:**

```bash
cd relay
npm install
wrangler login
wrangler deploy
wrangler secret put RELAY_API_KEY       # genera un UUID aleatorio
wrangler secret put SPOTIFY_CLIENT_ID
wrangler secret put SPOTIFY_CLIENT_SECRET
# (igual para Twitch y Kick)
```

---

## Build y distribución

La distribución usa [Velopack](https://velopack.io/) para instalador y actualizaciones delta.

### Build de desarrollo

```bash
# Opción 1: NUKE (recomendado — igual que CI)
./build.cmd --target Compile

# Opción 2: directo
dotnet build MusicBot.sln
```

### Generar instalador

```bash
./build.cmd --target Pack --configuration Release --runtime win-x86 --version 1.2.3
# Salida: artifacts/MusicBot-1.2.3-Setup.exe
```

### CI/CD (GitHub Actions)

El workflow `.github/workflows/build.yml` se dispara al pushear un tag `v*`:

```bash
git tag v1.2.3
git push --tags
# → GitHub Actions genera el instalador y crea un Release automáticamente
```

**Pasos del pipeline:**
1. Setup .NET 9 + Node.js 20
2. `./build.cmd --target Pack ...`
3. Crear GitHub Release con el `.exe` como asset

---

## Flujo de contribución

### Ramas

- `master` — rama principal, siempre estable
- Los PRs se hacen directamente a `master` para un proyecto de este tamaño

### Agregar un nuevo endpoint

1. Crear o modificar el controlador en `src/MusicBot.Web/Controllers/`
2. Agregar el método correspondiente en `src/MusicBot.Web/clientapp/src/services/api.ts`
3. Añadir o actualizar interfaces en `src/MusicBot.Web/clientapp/src/types/models.ts`
4. Usar el nuevo método desde el componente React correspondiente

### Agregar un nuevo servicio

1. Si es lógica de dominio puro → `MusicBot.Core/`
2. Si depende de ASP.NET o infraestructura → `MusicBot.Web/Services/`
3. Registrar en `WebHost.cs` (singleton para servicios compartidos, scoped/transient si aplica)
4. Si es un `BackgroundService`, usar `builder.Services.AddHostedService<T>()`

### Convenciones

- **C#**: Nullable habilitado (`#nullable enable`). No usar `!` para suprimir warnings salvo justificación.
- **TypeScript**: `strict: true`. Las interfaces de la API van en `types/models.ts`.
- **CSS**: Variables CSS en `:root` para colores. Soporte light/dark via `[data-theme="light"]`.
- **Sin migraciones EF**: Cambios de esquema = borrar `musicbot.db` en desarrollo.
- **Secrets**: Siempre via `dotnet user-secrets`, nunca en `appsettings.json`.

### Logs

Los logs van a `logs/musicbot-YYYYMMDD.log` (Serilog, retención 30 días). Dentro del Desktop, el botón de logs abre una ventana en tiempo real.

---

## Licencia

Ver el repositorio para los términos de licencia.
