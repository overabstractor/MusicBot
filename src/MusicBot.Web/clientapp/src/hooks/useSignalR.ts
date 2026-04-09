import { useEffect, useRef, useState, useCallback } from "react";
import * as signalR from "@microsoft/signalr";
import { NowPlayingState, SpotifyQueueState, QueueItem, QueueState } from "../types/models";

const HUB_URL = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/hub/overlay`
  : "/hub/overlay";

export type IntegrationStatus = "disconnected" | "connecting" | "connected" | "error";

export interface IntegrationStatusPayload {
  source: "tiktok" | "twitch" | "kick";
  status: IntegrationStatus;
}

export interface IntegrationEvent {
  id: number;
  source: "tiktok" | "twitch" | "kick";
  type: "play" | "gift";
  platform?: string;
  user: string;
  query: string;
  success: boolean;
  message: string;
  timestamp: Date;
}

export interface QueueSettings {
  maxQueueSize: number;
  maxSongsPerUser: number;
  votingEnabled: boolean;
  presenceCheckEnabled: boolean;
  presenceCheckWarningSeconds: number;
  presenceCheckConfirmSeconds: number;
  saveDownloads: boolean;
  autoQueueEnabled: boolean;
  openLogOnStart: boolean;
}

export interface TickerMessage {
  id: string;
  text: string;
  imageUrl?: string;
  durationSec: number;
  enabled: boolean;
  order: number;
}

export interface DownloadState {
  spotifyUri: string;
  title: string;
  artist: string;
  pct: number;
}

let _evId = 0;

export function useSignalR(overlayToken: string | null) {
  const connRef   = useRef<signalR.HubConnection | null>(null);
  const activeRef = useRef(false); // tracks whether the hook is still mounted

  const [nowPlaying,         setNowPlaying]         = useState<NowPlayingState | null>(null);
  const [spotifyQueue,       setSpotifyQueue]        = useState<SpotifyQueueState | null>(null);
  const [appQueue,           setAppQueue]            = useState<QueueItem[]>([]);
  const [connected,          setConnected]           = useState(false);
  const [tiktokStatus,       setTiktokStatus]        = useState<IntegrationStatus>("disconnected");
  const [twitchStatus,       setTwitchStatus]        = useState<IntegrationStatus>("disconnected");
  const [kickStatus,         setKickStatus]          = useState<IntegrationStatus>("disconnected");
  const [integrationEvents,  setIntegrationEvents]  = useState<IntegrationEvent[]>([]);
  const [queueSettings,      setQueueSettings]      = useState<QueueSettings>({ maxQueueSize: 50, maxSongsPerUser: 10, votingEnabled: false, presenceCheckEnabled: false, presenceCheckWarningSeconds: 30, presenceCheckConfirmSeconds: 30, saveDownloads: false, autoQueueEnabled: false, openLogOnStart: false });
  const [tickerMessages,     setTickerMessages]     = useState<TickerMessage[]>([]);
  const [activePlaylistName,  setActivePlaylistName]  = useState<string | null>(null);
  const [queueUpdateCount,    setQueueUpdateCount]    = useState(0);
  const [playlistUpdateCount, setPlaylistUpdateCount] = useState(0);
  const [downloadStates,     setDownloadStates]     = useState<Record<string, DownloadState>>({});
  const [downloadErrors,     setDownloadErrors]     = useState<Array<{ id: number; title: string; artist: string; reason?: string }>>([]);
  const [authUpdatedAt,      setAuthUpdatedAt]      = useState(0);

  const connect = useCallback(async () => {
    // Abort if unmounted or a connection already exists
    if (!activeRef.current || connRef.current) return;

    const conn = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build();

    // Claim the slot BEFORE any await to prevent concurrent starts
    connRef.current = conn;

    conn.on("nowplaying:updated",    (s: NowPlayingState)   => setNowPlaying(s));
    conn.on("queue:updated",         (s: QueueState)        => {
      if (s?.nowPlaying) setNowPlaying(s.nowPlaying);
      if (s?.upcoming)   setAppQueue(s.upcoming);
      setActivePlaylistName(s?.activePlaylistName ?? null);
      setQueueUpdateCount(c => c + 1);
    });
    conn.on("spotify:queue-updated", (s: SpotifyQueueState) => setSpotifyQueue(s));

    conn.on("integration:status", (p: IntegrationStatusPayload) => {
      if (p.source === "tiktok")  setTiktokStatus(p.status);
      if (p.source === "twitch")  setTwitchStatus(p.status);
      if (p.source === "kick")    setKickStatus(p.status);
    });

    conn.on("settings:updated",  (s: QueueSettings) => setQueueSettings(s));
    conn.on("playlist:status",   ()                  => setPlaylistUpdateCount(c => c + 1));

    conn.on("ticker:updated", (msgs: TickerMessage[]) => setTickerMessages(msgs || []));

    // ── Download progress animation ─────────────────────────────────────────
    const progressQueues = new Map<string, number[]>();
    const progressTimers = new Map<string, ReturnType<typeof setTimeout>>();

    function drainQueue(uri: string) {
      const q = progressQueues.get(uri);
      if (!q || q.length === 0) { progressTimers.delete(uri); return; }
      const pct = q.shift()!;
      setDownloadStates(prev => {
        const existing = prev[uri];
        if (existing) return { ...prev, [uri]: { ...existing, pct } };
        return { ...prev, [uri]: { spotifyUri: uri, title: "Descargando...", artist: "", pct } };
      });
      progressTimers.set(uri, setTimeout(() => drainQueue(uri), 180));
    }

    conn.on("download:started", (d: { spotifyUri: string; title: string; artist: string }) => {
      progressQueues.delete(d.spotifyUri);
      setDownloadStates(prev => ({ ...prev, [d.spotifyUri]: { ...d, pct: 0 } }));
    });

    conn.on("download:progress", (d: { spotifyUri: string; pct: number }) => {
      const q = progressQueues.get(d.spotifyUri) ?? [];
      q.push(d.pct);
      progressQueues.set(d.spotifyUri, q);
      if (!progressTimers.has(d.spotifyUri)) drainQueue(d.spotifyUri);
    });

    conn.on("download:error", (d: { spotifyUri: string; error: string }) => {
      progressQueues.delete(d.spotifyUri);
      const t = progressTimers.get(d.spotifyUri);
      if (t !== undefined) { clearTimeout(t); progressTimers.delete(d.spotifyUri); }
      setDownloadStates(prev => {
        const next = { ...prev };
        delete next[d.spotifyUri];
        return next;
      });
    });

    conn.on("download:done", (d: { spotifyUri: string }) => {
      progressQueues.delete(d.spotifyUri);
      const t = progressTimers.get(d.spotifyUri);
      if (t !== undefined) { clearTimeout(t); progressTimers.delete(d.spotifyUri); }

      setDownloadStates(prev => {
        const existing = prev[d.spotifyUri];
        if (!existing) return prev;
        return { ...prev, [d.spotifyUri]: { ...existing, pct: 100 } };
      });
      setTimeout(() => {
        setDownloadStates(prev => {
          const next = { ...prev };
          delete next[d.spotifyUri];
          return next;
        });
      }, 1200);
    });

    conn.on("queue:download-failed", (d: { title: string; artist: string; reason?: string }) => {
      const id = ++_evId;
      setDownloadErrors(prev => [...prev, { id, title: d.title, artist: d.artist, reason: d.reason }]);
      setTimeout(() => {
        setDownloadErrors(prev => prev.filter(e => e.id !== id));
      }, 12000);
    });

    conn.on("auth:updated", () => setAuthUpdatedAt(n => n + 1));

    conn.on("integration:event", (p: Omit<IntegrationEvent, "id" | "timestamp">) => {
      setIntegrationEvents(prev =>
        [{ ...p, id: ++_evId, timestamp: new Date() }, ...prev].slice(0, 30)
      );
    });

    conn.onreconnecting(() => setConnected(false));

    conn.onreconnected(async () => {
      setConnected(true);
      try { await conn.invoke("JoinUserGroup", overlayToken!); } catch {}
    });

    conn.onclose(() => {
      setConnected(false);
      for (const t of progressTimers.values()) clearTimeout(t);
      progressTimers.clear();
      progressQueues.clear();
      if (connRef.current === conn) {
        connRef.current = null;
        if (activeRef.current) setTimeout(connect, 5000);
      }
    });

    try {
      await conn.start();

      if (!activeRef.current) {
        conn.stop();
        return;
      }

      await conn.invoke("JoinUserGroup", overlayToken!);
      try { const msgs = await fetch("/api/ticker").then(r => r.json()); setTickerMessages(msgs || []); } catch {}
      setConnected(true);
    } catch {
      if (connRef.current === conn) connRef.current = null;
      if (activeRef.current) setTimeout(connect, 5000);
    }
  }, [overlayToken]);

  useEffect(() => {
    if (!overlayToken) return;

    activeRef.current = true;
    connect();

    return () => {
      activeRef.current = false;
      const c = connRef.current;
      connRef.current = null;
      c?.stop();
    };
  }, [connect, overlayToken]);

  return { nowPlaying, spotifyQueue, appQueue, activePlaylistName, connected, tiktokStatus, twitchStatus, kickStatus, integrationEvents, queueSettings, tickerMessages, queueUpdateCount, playlistUpdateCount, downloadStates, downloadErrors, authUpdatedAt, dismissDownloadError: (id: number) => setDownloadErrors(prev => prev.filter(e => e.id !== id)) };
}
