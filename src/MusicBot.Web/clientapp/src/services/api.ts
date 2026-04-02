import {
  CommandResult,
  NowPlayingState,
  MeResponse,
  SpotifyQueueState,
  PlatformState,
  HistoryItem,
  LibraryTrack,
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

  setVolume: (volume: number) =>
    request<void>("/api/player/volume", { method: "POST", body: JSON.stringify({ volume }) }),

  seek: (positionMs: number) =>
    request<void>("/api/player/seek", { method: "POST", body: JSON.stringify({ positionMs }) }),

  getAudioDevices: () =>
    request<{ id: string; name: string; isDefault: boolean }[]>("/api/player/devices"),

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

  // Queue management
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

  saveTikTok: (username: string, autoConnect: boolean) =>
    request<void>("/api/platforms/tiktok", {
      method: "PUT",
      body: JSON.stringify({ username, autoConnect }),
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

  // Relay
  getRelayStatus: () => request<{ configured: boolean; reachable: boolean; error: string | null }>("/api/relay/status"),

  // App
  shutdown: () => request<void>("/api/app/shutdown", { method: "POST" }),
  openLog:  () => request<void>("/api/app/open-log", { method: "POST" }),
};
