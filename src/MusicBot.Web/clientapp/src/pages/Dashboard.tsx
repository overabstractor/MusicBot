import React, { useState, useCallback, useRef, useEffect } from "react";
import { Sun, Moon, Power, FileText, Wrench, Music2, FolderOpen } from "lucide-react";
import { useTheme } from "../hooks/useTheme";
import { MainBrowser } from "../components/MainBrowser";
import { QueuePanel } from "../components/QueuePanel";
import { PlayerBar } from "../components/PlayerBar";
import { StatusBar } from "../components/StatusBar";
import { QueueToolsModal } from "../components/QueueToolsModal";
import { useSignalR } from "../hooks/useSignalR";
import { useLikedSongs } from "../hooks/useLikedSongs";
import { useConfirm } from "../hooks/useConfirm";
import { api } from "../services/api";

const OVERLAY_TOKEN = "local";

function formatDownloadReason(raw: string): string {
  // Strip "yt-dlp failed for "X": " prefix
  const prefixed = raw.match(/^yt-dlp failed for ".+?": (.+)$/s);
  const msg = prefixed ? prefixed[1] : raw;

  // Map known yt-dlp error patterns to friendly messages
  if (/private video/i.test(msg))           return "El video es privado";
  if (/video unavailable/i.test(msg))       return "El video no está disponible";
  if (/has been removed/i.test(msg))        return "El video fue eliminado";
  if (/not available in your country/i.test(msg)) return "No disponible en tu región";
  if (/age.?restricted/i.test(msg))         return "El video tiene restricción de edad";
  if (/no youtube match/i.test(msg))        return "No se encontró en YouTube";
  if (/output file not found/i.test(msg))   return "Error al procesar el archivo de audio";
  if (/unable to download/i.test(msg))      return "No se pudo descargar el video";
  if (/copyright/i.test(msg))               return "Bloqueado por derechos de autor";

  // Return first line of the message (yt-dlp errors can be multi-line)
  return msg.split("\n")[0].trim();
}

export const Dashboard: React.FC = () => {
  const {
    nowPlaying, appQueue, activePlaylistName, connected,
    tiktokStatus, twitchStatus, kickStatus,
    integrationEvents, queueSettings, tickerMessages,
    queueUpdateCount, playlistUpdateCount, downloadStates, downloadErrors, dismissDownloadError,
  } = useSignalR(OVERLAY_TOKEN);

  const { theme, toggle: toggleTheme } = useTheme();
  const { likedUris, toggleLike } = useLikedSongs(playlistUpdateCount);
  const [confirmModal, confirm] = useConfirm();

  // Keep libRefreshKey in sync with server-pushed playlist changes
  useEffect(() => {
    setLibRefreshKey(k => k + 1);
  }, [playlistUpdateCount]);

  const [appVersion,       setAppVersion]       = useState<string | null>(null);
  const [selectedPlaylist, setSelectedPlaylist] = useState<number | null>(null);
  const [libRefreshKey,    setLibRefreshKey]    = useState(0);
  const [queueToolsOpen,   setQueueToolsOpen]   = useState(false);
  const [rightPanelMode,   setRightPanelMode]   = useState<"queue" | "nowplaying" | "devices">("queue");
  const [shuffleActive,    setShuffleActive]    = useState(false);

  // simulation state for QueueToolsModal
  const [voteUser,  setVoteUser]  = useState("Admin");
  const [giftUser,  setGiftUser]  = useState("");
  const [giftCoins, setGiftCoins] = useState(10);
  const [simMsg,    setSimMsg]    = useState<string | null>(null);
  const simTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    api.getVersion().then(r => setAppVersion(r.version)).catch(() => {});
  }, []);

  const showSimMsg = (msg: string) => {
    setSimMsg(msg);
    if (simTimerRef.current) clearTimeout(simTimerRef.current);
    simTimerRef.current = setTimeout(() => setSimMsg(null), 3000);
  };

  const handleSkip   = useCallback(() => api.skip("Admin"),  []);
  const handlePause  = useCallback(() => api.pause(),        []);
  const handleResume = useCallback(() => api.resume(),       []);

  const handleRemove  = useCallback((uri: string)                          => { api.removeQueueItem(uri).catch(console.error); }, []);
  const handleReorder = useCallback((uri: string, toIndex: number)         => { api.reorderQueue(uri, toIndex).catch(console.error); }, []);
  const handleToggleQueue   = useCallback(() => setRightPanelMode(m => m === "queue" ? "nowplaying" : "queue"), []);
  const handleToggleDevices = useCallback(() => setRightPanelMode(m => m === "devices" ? "queue" : "devices"), []);
  const handleToggleShuffle = useCallback(async () => {
    await api.shuffleQueue().catch(() => {});
    setShuffleActive(true);
    setTimeout(() => setShuffleActive(false), 800);
  }, []);

  const handleBan     = useCallback(async (uri: string, title: string, artist: string) => {
    const ok = await confirm({ title: `Banear "${title}"`, message: "La canción no podrá volver a ser solicitada.", confirmText: "Banear", danger: true });
    if (!ok) return;
    api.banSong(uri, title, artist).catch(console.error);
    api.removeQueueItem(uri).catch(console.error);
  }, [confirm]);

  const handleVote = useCallback(async (skip: boolean) => {
    const r = await api.vote(voteUser || "Admin", skip, "web").catch(() => null);
    showSimMsg(r ? r.message : "Error al votar");
  }, [voteUser]);

  const handleGiftBump = useCallback(async () => {
    if (!giftUser.trim()) { showSimMsg("Ingresa un usuario"); return; }
    const r = await api.giftBump(giftUser.trim(), giftCoins).catch(() => null);
    showSimMsg(r ? r.message : "Error");
  }, [giftUser, giftCoins]);

  const nowPlayingUri = (nowPlaying?.spotifyTrack ?? nowPlaying?.item?.song)?.spotifyUri ?? null;

  return (
    <div className="app-shell spotify-shell">

      {/* ── Header ──────────────────────────────────────────── */}
      <header className="app-header">
        <div className="header-brand">
          <span className="header-logo"><Music2 size={20} /></span>
          <span className="header-title">MusicBot</span>
          {appVersion && <span className="header-version">v{appVersion}</span>}
        </div>

        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <StatusBar
            signalRConnected={connected}
            tiktokStatus={tiktokStatus}
            twitchStatus={twitchStatus}
            kickStatus={kickStatus}
          />

          <button className="header-icon-btn" onClick={() => setQueueToolsOpen(true)} title="Herramientas de cola"><Wrench size={15} /></button>
          <button className="theme-toggle-btn" onClick={toggleTheme} title="Cambiar tema">
            {theme === "dark" ? <Sun size={15} /> : <Moon size={15} />}
          </button>
          <button className="header-icon-btn" onClick={() => api.openLog()} title="Logs"><FileText size={15} /></button>
          <button className="header-icon-btn" onClick={() => api.openLogDir()} title="Carpeta de logs"><FolderOpen size={15} /></button>
          <button className="header-icon-btn header-icon-btn-danger"
            onClick={async () => {
              const ok = await confirm({ title: "¿Cerrar MusicBot?", message: "La reproducción se detendrá y la aplicación se cerrará.", confirmText: "Cerrar", danger: true });
              if (ok) api.shutdown();
            }}
            title="Cerrar"
          ><Power size={15} /></button>
        </div>
      </header>

      {/* ── Download error toasts ───────────────────────────── */}
      {downloadErrors.length > 0 && (
        <div className="download-error-stack">
          {downloadErrors.length > 1 && (
            <div className="download-error-toolbar">
              <span className="download-error-count">{downloadErrors.length} errores</span>
              <button
                className="download-error-dismiss-all"
                onClick={() => downloadErrors.forEach(e => dismissDownloadError(e.id))}
              >
                Limpiar todo
              </button>
            </div>
          )}
          {downloadErrors.slice(0, 3).map(e => (
            <div key={e.id} className="download-error-toast">
              <span className="download-error-icon">⚠</span>
              <div className="download-error-body">
                <span className="download-error-text">
                  No se pudo descargar <strong>{e.title}</strong>
                  {e.artist ? ` · ${e.artist}` : ""}
                </span>
                {e.reason && (
                  <span className="download-error-reason">{formatDownloadReason(e.reason)}</span>
                )}
              </div>
              <button className="download-error-close" onClick={() => dismissDownloadError(e.id)}>✕</button>
            </div>
          ))}
          {downloadErrors.length > 3 && (
            <div className="download-error-overflow">
              +{downloadErrors.length - 3} más
            </div>
          )}
        </div>
      )}

      {/* ── Body: 2 columns ─────────────────────────────────── */}
      <div className="spotify-body">

        {/* Center: Main browser */}
        <MainBrowser
          selectedPlaylistId={selectedPlaylist}
          onSelectPlaylist={id => setSelectedPlaylist(id)}
          onClearSelection={() => setSelectedPlaylist(null)}
          onPlaylistsChanged={() => setLibRefreshKey(k => k + 1)}
          nowPlayingUri={nowPlayingUri}
          queueUpdateCount={queueUpdateCount}
          playlistsRefreshKey={libRefreshKey}
          likedUris={likedUris}
          onToggleLike={toggleLike}
          settings={queueSettings}
          tiktokEvents={integrationEvents.filter(e => e.source === "tiktok")}
          twitchEvents={integrationEvents.filter(e => e.source === "twitch")}
          kickEvents={integrationEvents.filter(e => e.source === "kick")}
          tickerMessages={tickerMessages}
          overlayToken={OVERLAY_TOKEN}
        />

        {/* Right: Queue / Now Playing / Devices */}
        <QueuePanel
          mode={rightPanelMode}
          items={appQueue}
          nowPlaying={nowPlaying}
          onRemove={handleRemove}
          onReorder={handleReorder}
          onBan={handleBan}
          downloadStates={downloadStates}
          queueUpdateCount={queueUpdateCount}
          activePlaylistName={activePlaylistName}
          likedUris={likedUris}
          onToggleLike={toggleLike}
        />
      </div>

      {/* ── Player bar (bottom) ─────────────────────────────── */}
      <PlayerBar
        state={nowPlaying}
        onSkip={handleSkip}
        onPause={handlePause}
        onResume={handleResume}
        downloadStates={downloadStates}
        rightPanelMode={rightPanelMode}
        onToggleQueue={handleToggleQueue}
        onToggleDevices={handleToggleDevices}
        shuffleActive={shuffleActive}
        onToggleShuffle={handleToggleShuffle}
        likedUris={likedUris}
        onToggleLike={toggleLike}
      />

      {/* ── Modals ──────────────────────────────────────────── */}
      {confirmModal}

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
  );
};
