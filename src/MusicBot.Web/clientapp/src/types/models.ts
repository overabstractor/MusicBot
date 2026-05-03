export interface Song {
  spotifyUri: string;
  title: string;
  artist: string;
  coverUrl: string;
  durationMs: number;
  requestedBy?: string;
  platform?: string;
  isDownloaded?: boolean;
  // Playlist search result fields (only present when isPlaylist=true)
  isPlaylist?: boolean;
  playlistUrl?: string;
  playlistVideoCount?: number;
}

export interface QueueItem {
  id: string;
  song: Song;
  requestedBy: string;
  platform: string;
  addedAt: number;
  isPlaylistItem?: boolean;
  downloadError?: string | null;
}

export interface NowPlayingState {
  item: QueueItem | null;
  progressMs: number;
  isPlaying: boolean;
  spotifyTrack: Song | null;
}

export interface QueueState {
  nowPlaying: NowPlayingState;
  upcoming: QueueItem[];
  activePlaylistName?: string | null;
}

export interface CommandResult {
  success: boolean;
  message: string;
  data?: any;
}

export interface AuthUser {
  id: string;
  username: string;
  slug: string;
}

export interface AuthResponse {
  token: string;
  user: AuthUser;
}

export interface MeResponse {
  id: string;
  username: string;
  slug: string;
  overlayToken: string;
  spotifyConnected: boolean;
}

export interface ApiKeyInfo {
  id: string;
  keyPrefix: string;
  label: string;
  createdAt: string;
  lastUsedAt: string | null;
}

export interface ApiKeyCreated {
  key: string;
  id: string;
  label: string;
  keyPrefix: string;
}

export interface SpotifyQueueState {
  currentlyPlaying: Song | null;
  queue: Song[];
}

export interface TikTokConfig {
  username: string;
}

export interface TwitchConfig {
  channel: string;
  botUsername: string;
}

export interface KickConfig {
  channel: string;
}

export interface LibraryTrack {
  id: number;
  trackId: string;
  title: string;
  artist: string;
  coverUrl: string | null;
  durationMs: number;
  downloadedAt: string;
  fileSizeBytes: number;
  fileExists: boolean;
  playCount: number;
  totalPlayedMs: number;
}

export interface HistoryItem {
  id: string;
  trackId: string;
  title: string;
  artist: string;
  coverUrl: string | null;
  durationMs: number;
  requestedBy: string | null;
  platform: string | null;
  playedAt: string;
}

export interface PlatformState {
  platform: "tiktok" | "twitch" | "kick";
  status: "connected" | "connecting" | "disconnected" | "error";
  errorMessage?: string;
  autoConnect: boolean;
  config: TikTokConfig | TwitchConfig | KickConfig | null;
}

export interface PlaylistLibrary {
  id: number;
  name: string;
  isActive: boolean;
  isSystem?: boolean;
  isPinned?: boolean;
  pinOrder?: number;
  createdAt: string;
  songCount: number;
  coverUrls?: string[];
}

export interface PlaylistMembership {
  id: number;
  name: string;
  songCount: number;
  isInPlaylist: boolean;
  isSystem: boolean;
  updatedAt: string;
}

export interface PlaylistLibrarySong {
  id: number;
  playlistId: number;
  spotifyUri: string;
  title: string;
  artist: string;
  coverUrl: string;
  durationMs: number;
  position: number;
}
