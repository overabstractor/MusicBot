import React, { useState, useCallback, useRef, useEffect } from "react";
import { useTheme } from "../hooks/useTheme";
import { NowPlaying } from "../components/NowPlaying";
import { QueueList } from "../components/QueueList";
import { AddSong } from "../components/AddSong";
import { OverlayLinks } from "../components/OverlayLinks";
import { StatusBar } from "../components/StatusBar";
import { PlatformConnections } from "../components/PlatformConnections";
import { SongHistory } from "../components/SongHistory";
import { Library } from "../components/Library";
import { SettingsPanel } from "../components/SettingsPanel";
import { AutoQueuePanel } from "../components/AutoQueuePanel";
import { TickerMessages } from "../components/TickerMessages";
import { QueueToolsModal } from "../components/QueueToolsModal";
import { useSignalR } from "../hooks/useSignalR";
import { api } from "../services/api";

const OVERLAY_TOKEN = "local";

type Tab = "queue" | "history" | "library" | "integrations" | "overlays" | "settings" | "ticker" | "autoqueue";

const NAV_ITEMS: { id: Tab; icon: string; label: string }[] = [
  { id: "queue",        icon: "≡",  label: "Cola"          },
  { id: "history",      icon: "⏱",  label: "Historial"     },
  { id: "library",      icon: "♪",  label: "Librería"      },
  { id: "autoqueue",    icon: "🎲", label: "Auto Cola"     },
  { id: "integrations", icon: "⚡",  label: "Plataformas"   },
  { id: "overlays",     icon: "📺", label: "Overlays"      },
  { id: "settings",     icon: "⚙",  label: "Ajustes"       },
  { id: "ticker",       icon: "📢", label: "Mensajes"      },
];

export const Dashboard: React.FC = () => {
  const { nowPlaying, appQueue, connected, tiktokStatus, twitchStatus, kickStatus, integrationEvents, queueSettings, tickerMessages, queueUpdateCount, downloadStates, downloadErrors, dismissDownloadError } = useSignalR(OVERLAY_TOKEN);
  const { theme, toggle: toggleTheme } = useTheme();

  const [tab,          setTab]          = useState<Tab>("queue");
  const [queueToolsOpen, setQueueToolsOpen] = useState(false);
  const [appVersion,   setAppVersion]   = useState<string | null>(null);

  useEffect(() => {
    api.getVersion().then(r => setAppVersion(r.version)).catch(() => {});
  }, []);

  const [voteUser,    setVoteUser]    = useState("Admin");
  const [giftUser,    setGiftUser]    = useState("");
  const [giftCoins,   setGiftCoins]   = useState(10);
  const [simMsg,      setSimMsg]      = useState<string | null>(null);
  const simTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const [autoQueueMsg,     setAutoQueueMsg]     = useState<string | null>(null);
  const autoQueueTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const handleAddToAutoQueue = useCallback(async (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) => {
    try {
      await api.addAutoQueueSong(song);
      setAutoQueueMsg(`✓ "${song.title}" agregada a AutoCola`);
    } catch (e: unknown) {
      setAutoQueueMsg(e instanceof Error ? e.message : "Error al agregar");
    }
    if (autoQueueTimerRef.current) clearTimeout(autoQueueTimerRef.current);
    autoQueueTimerRef.current = setTimeout(() => setAutoQueueMsg(null), 3000);
  }, []);

  const showSimMsg = (msg: string) => {
    setSimMsg(msg);
    if (simTimerRef.current) clearTimeout(simTimerRef.current);
    simTimerRef.current = setTimeout(() => setSimMsg(null), 3000);
  };

  const handleSkip   = useCallback(() => api.skip("Admin"), []);
  const handlePause  = useCallback(() => api.pause(),       []);
  const handleResume = useCallback(() => api.resume(),      []);

  const handleRemove = useCallback((uri: string) => {
    api.removeQueueItem(uri).catch(console.error);
  }, []);

  const handleReorder = useCallback((uri: string, toIndex: number) => {
    api.reorderQueue(uri, toIndex).catch(console.error);
  }, []);

  const handleBan = useCallback((uri: string, title: string, artist: string) => {
    if (!confirm(`¿Banear "${title}" de ${artist}? Los usuarios no podrán solicitarla.`)) return;
    api.banSong(uri, title, artist).catch(console.error);
    api.removeQueueItem(uri).catch(console.error);
  }, []);

  const handleVote = useCallback(async (skip: boolean) => {
    const r = await api.vote(voteUser || "Admin", skip, "web").catch(() => null);
    showSimMsg(r ? r.message : "Error al votar");
  }, [voteUser]);

  const handleGiftBump = useCallback(async () => {
    if (!giftUser.trim()) { showSimMsg("Ingresa un usuario"); return; }
    const r = await api.giftBump(giftUser.trim(), giftCoins).catch(() => null);
    showSimMsg(r ? r.message : "Error al simular regalo");
  }, [giftUser, giftCoins]);

  return (
    <div className="app-shell">

      {/* ── Header ─────────────────────────────────── */}
      <header className="app-header">
        <div className="header-brand">
          <span className="header-logo">♫</span>
          <span className="header-title">MusicBot</span>
          {appVersion && (
            <span className="header-version">v{appVersion}</span>
          )}
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
          <StatusBar
            signalRConnected={connected}
            tiktokStatus={tiktokStatus}
            twitchStatus={twitchStatus}
            kickStatus={kickStatus}
          />
          <button
            className="theme-toggle-btn"
            onClick={toggleTheme}
            title={theme === "dark" ? "Cambiar a tema claro" : "Cambiar a tema oscuro"}
          >
            {theme === "dark" ? "☀️" : "🌙"}
          </button>
          <button
            className="header-icon-btn"
            onClick={() => api.openLog()}
            title="Abrir ventana de logs"
          >
            📋
          </button>
          <button
            className="header-icon-btn header-icon-btn-danger"
            onClick={() => {
              if (confirm("¿Cerrar MusicBot? Se detendrá la reproducción."))
                api.shutdown();
            }}
            title="Cerrar MusicBot"
          >
            ⏻
          </button>
        </div>
      </header>

      {/* ── Download error toasts ──────────────────── */}
      {downloadErrors.length > 0 && (
        <div className="download-error-stack">
          {downloadErrors.map(e => (
            <div key={e.id} className="download-error-toast">
              <span className="download-error-icon">⚠</span>
              <span className="download-error-text">
                No se pudo descargar <strong>{e.title}</strong>
                {e.artist ? ` · ${e.artist}` : ""}
              </span>
              <button className="download-error-close" onClick={() => dismissDownloadError(e.id)}>✕</button>
            </div>
          ))}
        </div>
      )}

      {/* ── AutoQueue toast ───────────────────────── */}
      {autoQueueMsg && (
        <div className="autoqueue-toast">{autoQueueMsg}</div>
      )}

      {/* ── Body ───────────────────────────────────── */}
      <div className="app-body">

        {/* Left: always-visible player column */}
        <aside className="player-column">
          <div className="player-column-inner">
            <div className="player-section">
              <p className="section-label">Reproduciendo ahora</p>
              <NowPlaying
                state={nowPlaying}
                onSkip={handleSkip}
                onPause={handlePause}
                onResume={handleResume}
                onAddToAutoQueue={handleAddToAutoQueue}
                downloadStates={downloadStates}
              />
            </div>
            <div className="player-section">
              <p className="section-label">Agregar canción</p>
              <AddSong onAdded={() => {}} />
            </div>
          </div>
        </aside>

        {/* Right: tabbed content */}
        <div className="content-column">
          <nav className="tab-nav">
            {NAV_ITEMS.map(n => (
              <button
                key={n.id}
                className={`tab-nav-btn ${tab === n.id ? "active" : ""}`}
                onClick={() => setTab(n.id)}
              >
                <span className="tab-nav-icon">{n.icon}</span>
                <span className="tab-nav-label">{n.label}</span>
                {n.id === "queue" && appQueue.length > 0 && (
                  <span className="tab-nav-badge">{appQueue.length}</span>
                )}
              </button>
            ))}
          </nav>

          <div className="tab-content">

            <div className="tab-pane" style={{ display: tab === "queue" ? "flex" : "none" }}>
              <div className="tab-pane-header">
                <h2 className="tab-pane-title">Cola de reproducción</h2>
                <span className="count-chip">{appQueue.length} canciones</span>
                <button
                  className="btn btn-sm btn-queue-tools"
                  onClick={() => setQueueToolsOpen(true)}
                  title="Importar y simulación"
                >
                  ⚙ Herramientas
                </button>
              </div>

              <QueueList items={appQueue} onRemove={handleRemove} onReorder={handleReorder} onBan={handleBan} onAddToAutoQueue={handleAddToAutoQueue} downloadStates={downloadStates} />

              <QueueToolsModal
                open={queueToolsOpen}
                onClose={() => setQueueToolsOpen(false)}
                voteUser={voteUser}
                onVoteUserChange={setVoteUser}
                giftUser={giftUser}
                onGiftUserChange={setGiftUser}
                giftCoins={giftCoins}
                onGiftCoinsChange={setGiftCoins}
                onVote={handleVote}
                onGiftBump={handleGiftBump}
                simMsg={simMsg}
              />
            </div>

            <div className="tab-pane" style={{ display: tab === "history" ? "flex" : "none" }}>
              <div className="tab-pane-header">
                <h2 className="tab-pane-title">Historial</h2>
              </div>
              <SongHistory refreshKey={queueUpdateCount} onAddToAutoQueue={handleAddToAutoQueue} />
            </div>

            <div className="tab-pane" style={{ display: tab === "library" ? "flex" : "none" }}>
              <div className="tab-pane-header">
                <h2 className="tab-pane-title">Librería local</h2>
              </div>
              <Library refreshKey={queueUpdateCount} saveDownloads={queueSettings.saveDownloads} />
            </div>

            <div className="tab-pane" style={{ display: tab === "autoqueue" ? "flex" : "none" }}>
              <div className="tab-pane-header">
                <h2 className="tab-pane-title">Auto Cola</h2>
                {queueSettings.autoQueueEnabled && !nowPlaying?.isPlaying && (
                  <button
                    className="btn btn-primary btn-sm"
                    onClick={async () => {
                      try { await api.startAutoQueue(); }
                      catch (e: unknown) { alert(e instanceof Error ? e.message : "Error"); }
                    }}
                  >
                    ▶ Iniciar ahora
                  </button>
                )}
              </div>
              <div className="tab-pane-body">
                <AutoQueuePanel nowPlaying={nowPlaying} />
              </div>
            </div>

            <div className="tab-pane" style={{ display: tab === "integrations" ? "flex" : "none" }}>
              <div className="tab-pane-header">
                <h2 className="tab-pane-title">Plataformas</h2>
              </div>
              <div className="tab-pane-body">
                <PlatformConnections
                  tiktokEvents={integrationEvents.filter(e => e.source === "tiktok")}
                  twitchEvents={integrationEvents.filter(e => e.source === "twitch")}
                  kickEvents={integrationEvents.filter(e => e.source === "kick")}
                />
              </div>
            </div>

            <div className="tab-pane" style={{ display: tab === "overlays" ? "flex" : "none" }}>
              <div className="tab-pane-header">
                <h2 className="tab-pane-title">Overlays</h2>
              </div>
              <div className="tab-pane-body">
                <OverlayLinks overlayToken={OVERLAY_TOKEN} />
              </div>
            </div>

            <div className="tab-pane" style={{ display: tab === "settings" ? "flex" : "none" }}>
              <div className="tab-pane-header">
                <h2 className="tab-pane-title">Ajustes</h2>
              </div>
              <div className="tab-pane-body">
                <SettingsPanel settings={queueSettings} />
              </div>
            </div>

            <div className="tab-pane" style={{ display: tab === "ticker" ? "flex" : "none" }}>
              <div className="tab-pane-header">
                <h2 className="tab-pane-title">Mensajes del Overlay</h2>
              </div>
              <div className="tab-pane-body">
                <TickerMessages messages={tickerMessages} />
              </div>
            </div>

          </div>
        </div>
      </div>
    </div>
  );
};

