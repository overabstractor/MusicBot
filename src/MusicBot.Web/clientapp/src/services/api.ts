import {
  CommandResult,
  NowPlayingState,
  MeResponse,
  SpotifyQueueState,
  PlatformState,
  HistoryItem,
  LibraryTrack,
  PlaylistLibrary,
  PlaylistLibrarySong,
  PlaylistMembership,
  FeatureRequest,
  SupportTicket,
} from "../types/models";
import type { TickerMessage } from "../hooks/useSignalR";

const BASE = import.meta.env.VITE_API_URL || "";

async function request<T>(url: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${url}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...(options?.headers ?? {}) },
  });

  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error(body.error || `Request failed (${res.status})`);
  }

  if (res.status === 204) return undefined as T;
  return res.json();
}

export const api = {
  // Info
  me: () => request<MeResponse>("/api/auth/me"),

  getSpotifyAuthUrl:   () => request<{ url: string }>("/api/auth/spotify"),
  getSpotifyStatus:    () => request<{ authenticated: boolean }>("/api/auth/spotify/status"),
  disconnectSpotify:   () => request<void>("/api/auth/spotify", { method: "DELETE" }),

  // Queue
  getNowPlaying: () => request<NowPlayingState>("/api/queue/now-playing"),

  // Commands
  search: (q: string, limit = 5) =>
    request<import("../types/models").Song[]>(`/api/search?q=${encodeURIComponent(q)}&limit=${limit}`),

  getPlaylistTracks: (url: string, limit = 200) =>
    request<import("../types/models").Song[]>(`/api/search/playlist-tracks?url=${encodeURIComponent(url)}&limit=${limit}`),

  play: (query: string, requestedBy: string, platform?: string) =>
    request<CommandResult>("/api/play", {
      method: "POST",
      body: JSON.stringify({ query, requestedBy, platform }),
    }),

  skip: (requestedBy: string) =>
    request<CommandResult>("/api/skip", {
      method: "POST",
      body: JSON.stringify({ requestedBy }),
    }),

  pause:  () => request<void>("/api/pause",  { method: "POST" }),
  resume: () => request<void>("/api/resume", { method: "POST" }),

  getVolume: () =>
    request<{ volume: number }>("/api/player/volume"),

  setVolume: (volume: number) =>
    request<void>("/api/player/volume", { method: "POST", body: JSON.stringify({ volume }) }),

  seek: (positionMs: number) =>
    request<void>("/api/player/seek", { method: "POST", body: JSON.stringify({ positionMs }) }),

  getAudioDevices: () =>
    request<{ activeDeviceId: string | null; devices: { id: string; name: string; isDefault: boolean }[] }>("/api/player/devices"),

  setAudioDevice: (deviceId: string | null) =>
    request<void>("/api/player/device", { method: "POST", body: JSON.stringify({ deviceId }) }),

  getHistory:   (limit = 50) => request<HistoryItem[]>(`/api/history?limit=${limit}`),
  clearHistory: ()           => request<void>("/api/history", { method: "DELETE" }),

  // Library
  getLibrary:        ()           => request<LibraryTrack[]>("/api/library"),
  deleteTrack:       (id: number) => request<void>(`/api/library/${id}`, { method: "DELETE" }),
  clearLibrary:      ()           => request<void>("/api/library", { method: "DELETE" }),
  openLibraryFolder: ()           => request<{ path: string }>("/api/library/open-folder", { method: "POST" }),

  startAutoQueue: () =>
    request<CommandResult>("/api/queue/start-auto", { method: "POST" }),

  shuffleQueue: () =>
    request<{ message: string }>("/api/queue/shuffle", { method: "POST" }),

  shuffleBackgroundPlaylist: () =>
    request<void>("/api/queue/shuffle-background", { method: "POST" }),

  promoteFromBackground: (uri: string, toIndex?: number) =>
    request<void>("/api/queue/promote-from-background", {
      method: "POST",
      body: JSON.stringify({ uri, toIndex }),
    }),

  prewarmNext: (count = 2) =>
    request<void>(`/api/queue/prewarm-next?count=${count}`, { method: "POST" }),

  // Queue management
  clearUserQueue: () =>
    request<void>("/api/queue/user", { method: "DELETE" }),

  removeQueueItem: (uri: string) =>
    request<void>("/api/queue/item", {
      method: "DELETE",
      body: JSON.stringify({ uri }),
    }),

  moveQueueItem: (uri: string, direction: "up" | "down") =>
    request<void>("/api/queue/move", {
      method: "POST",
      body: JSON.stringify({ uri, direction }),
    }),

  playNow: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }, requestedBy?: string) =>
    request<CommandResult>("/api/queue/play-now", {
      method: "POST",
      body: JSON.stringify({ ...song, requestedBy: requestedBy ?? "Admin", platform: "web" }),
    }),

  enqueueTrack: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }, requestedBy?: string) =>
    request<CommandResult>("/api/queue/enqueue", {
      method: "POST",
      body: JSON.stringify({ ...song, requestedBy: requestedBy ?? "Admin", platform: "web" }),
    }),

  reorderQueue: (uri: string, toIndex: number) =>
    request<void>("/api/queue/reorder", {
      method: "POST",
      body: JSON.stringify({ uri, toIndex }),
    }),

  importPlaylist: (url: string, requestedBy: string) =>
    request<{ added: number; skipped: number; total: number }>("/api/queue/import-playlist", {
      method: "POST",
      body: JSON.stringify({ url, requestedBy }),
    }),

  // Liked Songs
  getLikedUris: () => request<string[]>("/api/liked/uris"),

  toggleLiked: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) =>
    request<{ isLiked: boolean }>("/api/liked/toggle", {
      method: "POST",
      body: JSON.stringify({ spotifyUri: song.spotifyUri, title: song.title, artist: song.artist, coverUrl: song.coverUrl, durationMs: song.durationMs }),
    }),

  getSongMemberships: (uri: string) =>
    request<PlaylistMembership[]>(`/api/liked/memberships?uri=${encodeURIComponent(uri)}`),

  toggleMembership: (playlistId: number, song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) =>
    request<{ isInPlaylist: boolean }>(`/api/liked/memberships/${playlistId}`, {
      method: "POST",
      body: JSON.stringify({ spotifyUri: song.spotifyUri, title: song.title, artist: song.artist, coverUrl: song.coverUrl, durationMs: song.durationMs }),
    }),

  // Banned songs
  getBanned: () =>
    request<{ uri: string; title: string; artist: string; bannedAt: string }[]>("/api/banned"),

  banSong: (uri: string, title: string, artist: string) =>
    request<void>("/api/banned", {
      method: "POST",
      body: JSON.stringify({ uri, title, artist }),
    }),

  unbanSong: (uri: string) =>
    request<void>(`/api/banned/${encodeURIComponent(uri)}`, { method: "DELETE" }),

  // Vote
  vote: (username: string, skip: boolean, platform?: string) =>
    request<CommandResult>("/api/vote", {
      method: "POST",
      body: JSON.stringify({ username, skip, platform }),
    }),

  // TikTok gift bump
  giftBump: (username: string, coins: number) =>
    request<CommandResult>("/api/queue/gift-bump", {
      method: "POST",
      body: JSON.stringify({ username, coins }),
    }),

  // Settings
  getSettings: () => request<{ maxQueueSize: number; maxSongsPerUser: number; votingEnabled: boolean; presenceCheckEnabled: boolean; presenceCheckWarningSeconds: number; presenceCheckConfirmSeconds: number; saveDownloads: boolean; autoQueueEnabled: boolean; openLogOnStart: boolean }>("/api/settings"),
  updateSettings: (s: { maxQueueSize: number; maxSongsPerUser: number; votingEnabled: boolean; presenceCheckEnabled: boolean; presenceCheckWarningSeconds: number; presenceCheckConfirmSeconds: number; saveDownloads: boolean; autoQueueEnabled: boolean; openLogOnStart: boolean }) =>
    request<void>("/api/settings", { method: "PUT", body: JSON.stringify(s) }),

  // Ticker messages
  getTicker: () => request<TickerMessage[]>("/api/ticker"),
  addTicker: (msg: Omit<TickerMessage, "id" | "order">) =>
    request<TickerMessage>("/api/ticker", { method: "POST", body: JSON.stringify(msg) }),
  updateTicker: (id: string, msg: Omit<TickerMessage, "id" | "order">) =>
    request<void>(`/api/ticker/${id}`, { method: "PUT", body: JSON.stringify(msg) }),
  deleteTicker: (id: string) =>
    request<void>(`/api/ticker/${id}`, { method: "DELETE" }),

  // AutoQueue
  getAutoQueue: () => request<{ id: number; spotifyUri: string; title: string; artist: string; coverUrl: string; durationMs: number }[]>("/api/autoqueue"),
  addAutoQueueSong: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) =>
    request<void>("/api/autoqueue", { method: "POST", body: JSON.stringify(song) }),
  removeAutoQueueSong: (uri: string) =>
    request<void>(`/api/autoqueue/${encodeURIComponent(uri)}`, { method: "DELETE" }),
  clearAutoQueue: () => request<void>("/api/autoqueue", { method: "DELETE" }),
  importAutoQueue: (url: string) =>
    request<{ added: number; total: number }>("/api/autoqueue/import", { method: "POST", body: JSON.stringify({ url }) }),

  // Platforms
  getPlatforms: () => request<PlatformState[]>("/api/platforms"),

  saveTikTok: (username: string, autoConnect: boolean, giftInterruptThreshold: number = 100) =>
    request<void>("/api/platforms/tiktok", {
      method: "PUT",
      body: JSON.stringify({ username, autoConnect, giftInterruptThreshold }),
    }),

  saveTwitch: (channel: string, botUsername: string, autoConnect: boolean) =>
    request<void>("/api/platforms/twitch", {
      method: "PUT",
      body: JSON.stringify({ channel, botUsername, autoConnect }),
    }),

  // TikTok in-app login
  startTikTokLogin:   () => request<{ message: string }>("/api/auth/tiktok/start", { method: "POST" }),
  getTikTokAuthStatus: () => request<{ authenticated: boolean; username: string | null; cancelled: boolean }>("/api/auth/tiktok/status"),
  disconnectTikTokAuth: () => request<void>("/api/auth/tiktok", { method: "DELETE" }),

  // YouTube in-app login (cookies for yt-dlp bot detection bypass)
  startYouTubeLogin:    () => request<{ message: string }>("/api/auth/youtube/start", { method: "POST" }),
  getYouTubeAuthStatus: () => request<{ enabled: boolean; authenticated: boolean; account: string | null; savedAt: string | null; cancelled: boolean }>("/api/auth/youtube/status"),
  enableYouTubeAuth:    () => request<void>("/api/auth/youtube/enable",  { method: "POST" }),
  disableYouTubeAuth:   () => request<void>("/api/auth/youtube/disable", { method: "POST" }),
  disconnectYouTubeAuth: () => request<void>("/api/auth/youtube", { method: "DELETE" }),

  // Open a URL in the user's default system browser (for OAuth flows)
  openInBrowser: (url: string) =>
    request<void>(`/api/auth/open-in-browser?url=${encodeURIComponent(url)}`, { method: "POST" }),

  // Twitch OAuth
  getTwitchAuthUrl:    () => request<{ url: string }>("/api/auth/twitch"),
  getTwitchStatus:     () => request<{ authenticated: boolean; username: string | null }>("/api/auth/twitch/status"),
  disconnectTwitch:    () => request<void>("/api/auth/twitch", { method: "DELETE" }),

  // Kick OAuth
  getKickAuthUrl:   () => request<{ url: string }>("/api/auth/kick"),
  getKickStatus:    () => request<{ authenticated: boolean; channel: string | null }>("/api/auth/kick/status"),
  disconnectKick:   () => request<void>("/api/auth/kick", { method: "DELETE" }),

  saveKick: (channel: string, autoConnect: boolean) =>
    request<void>("/api/platforms/kick", {
      method: "PUT",
      body: JSON.stringify({ channel, autoConnect }),
    }),

  connectPlatform:    (platform: string) =>
    request<void>(`/api/platforms/${platform}/connect`,    { method: "POST" }),

  disconnectPlatform: (platform: string) =>
    request<void>(`/api/platforms/${platform}/disconnect`, { method: "POST" }),

  forgetPlatform: (platform: string) =>
    request<void>(`/api/platforms/${platform}/forget`, { method: "POST" }),

  // Playlist Library
  getPlaylists: () =>
    request<PlaylistLibrary[]>("/api/playlists"),

  createPlaylist: (name: string) =>
    request<PlaylistLibrary>("/api/playlists", { method: "POST", body: JSON.stringify({ name }) }),

  deletePlaylist: (id: number) =>
    request<void>(`/api/playlists/${id}`, { method: "DELETE" }),

  renamePlaylist: (id: number, name: string) =>
    request<void>(`/api/playlists/${id}/rename`, { method: "PUT", body: JSON.stringify({ name }) }),

  getPlaylistSongs: (id: number) =>
    request<PlaylistLibrarySong[]>(`/api/playlists/${id}/songs`),

  addPlaylistSong: (id: number, song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) =>
    request<void>(`/api/playlists/${id}/songs`, { method: "POST", body: JSON.stringify(song) }),

  removePlaylistSong: (id: number, uri: string) =>
    request<void>(`/api/playlists/${id}/songs/${encodeURIComponent(uri)}`, { method: "DELETE" }),

  importPlaylistSongs: (id: number, url: string, userProvidedName?: string) =>
    request<{ added: number; skipped: number; total: number; name?: string }>(`/api/playlists/${id}/import`, { method: "POST", body: JSON.stringify({ url, userProvidedName }) }),

  activatePlaylist: (id: number, shuffle = false) =>
    request<{ message: string }>(`/api/playlists/${id}/play`, {
      method: "POST",
      body: JSON.stringify({ shuffle }),
    }),

  playSongFromPlaylist: (playlistId: number, uri: string, shuffle = false) =>
    request<{ message: string }>(`/api/playlists/${playlistId}/songs/${encodeURIComponent(uri)}/play`, {
      method: "POST",
      body: JSON.stringify({ shuffle }),
    }),

  deactivatePlaylist: () =>
    request<void>("/api/playlists/active", { method: "DELETE" }),

  togglePlaylistPin: (id: number) =>
    request<{ isPinned: boolean }>(`/api/playlists/${id}/pin`, { method: "POST" }),

  reorderPins: (ids: number[]) =>
    request<void>("/api/playlists/pins/reorder", { method: "PUT", body: JSON.stringify({ ids }) }),

  reorderPlaylists: (ids: number[]) =>
    request<void>("/api/playlists/reorder", { method: "PUT", body: JSON.stringify({ ids }) }),

  reorderPlaylistSong: (playlistId: number, spotifyUri: string, toIndex: number) =>
    request<void>(`/api/playlists/${playlistId}/songs/reorder`, {
      method: "PUT",
      body: JSON.stringify({ spotifyUri, toIndex }),
    }),

  // Relay
  getRelayStatus: () => request<{ configured: boolean; reachable: boolean; error: string | null }>("/api/relay/status"),

  // App
  shutdown:      () => request<void>("/api/app/shutdown", { method: "POST" }),
  openLog:       () => request<void>("/api/app/open-log",     { method: "POST" }),
  openLogDir:    () => request<void>("/api/app/open-log-dir", { method: "POST" }),
  getVersion:    () => request<{ version: string }>("/api/app/version"),
  updateYtDlp:   () => request<{ version: string; message: string }>("/api/app/yt-dlp/update", { method: "POST" }),

  // Community – Feature Requests
  // Note: API uses integer IDs; LocalCommunityService converts to/from string.
  getFeatureRequests: () => request<FeatureRequest[]>("/api/community/features"),
  createFeatureRequest: (title: string, description: string) =>
    request<FeatureRequest>("/api/community/features", {
      method: "POST",
      body: JSON.stringify({ title, description }),
    }),
  voteFeature: (id: number) =>
    request<{ votes: number; hasVoted: boolean }>(`/api/community/features/${id}/vote`, { method: "POST" }),
  deleteFeatureRequest: (id: number) =>
    request<void>(`/api/community/features/${id}`, { method: "DELETE" }),

  // Support Tickets
  getSupportTickets: () => request<SupportTicket[]>("/api/support/tickets"),
  createSupportTicket: (title: string, description: string, category: string) =>
    request<SupportTicket>("/api/support/tickets", {
      method: "POST",
      body: JSON.stringify({ title, description, category }),
    }),
  deleteSupportTicket: (id: number) =>
    request<void>(`/api/support/tickets/${id}`, { method: "DELETE" }),

};
