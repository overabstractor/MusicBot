import React, { useState, useCallback, useRef, useEffect } from "react";
import { Sun, Moon, Power, FileText, Wrench, Music2, FolderOpen, Bot, Users, Coffee, LifeBuoy, Languages } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useTheme } from "../hooks/useTheme";
import { MainBrowser } from "../components/MainBrowser";
import { QueuePanel } from "../components/QueuePanel";
import { PlayerBar } from "../components/PlayerBar";
import { StatusBar } from "../components/StatusBar";
import { QueueToolsModal } from "../components/QueueToolsModal";
import { ComunidadPanel } from "../components/ComunidadPanel";
import { DonacionesPanel } from "../components/DonacionesPanel";
import { SoportePanel } from "../components/SoportePanel";
import { useSignalR } from "../hooks/useSignalR";
import { useLikedSongs } from "../hooks/useLikedSongs";
import { useConfirm } from "../hooks/useConfirm";
import { api } from "../services/api";
import i18n from "../i18n";

type AppSection = "bot" | "comunidad" | "donaciones" | "soporte";

const SECTIONS: { id: AppSection; labelKey: string; icon: React.ReactNode }[] = [
  { id: "bot",       labelKey: "sections.manager",   icon: <Bot size={13} />      },
  { id: "comunidad", labelKey: "sections.community", icon: <Users size={13} />    },
  { id: "soporte",   labelKey: "sections.support",   icon: <LifeBuoy size={13} /> },
  { id: "donaciones",labelKey: "sections.donations", icon: <Coffee size={13} />   },
];

const OVERLAY_TOKEN = "local";

function formatDownloadReason(raw: string): string {
  // Strip "yt-dlp failed for "X": " prefix
  const prefixed = raw.match(/^yt-dlp failed for ".+?": (.+)$/s);
  const msg = prefixed ? prefixed[1] : raw;

  // Map known yt-dlp error patterns to friendly messages
  const t = i18n.t;
  if (/private video/i.test(msg))           return t("dashboard.downloadReason.private");
  if (/video unavailable/i.test(msg))       return t("dashboard.downloadReason.unavailable");
  if (/has been removed/i.test(msg))        return t("dashboard.downloadReason.removed");
  if (/not available in your country/i.test(msg)) return t("dashboard.downloadReason.region");
  if (/age.?restricted/i.test(msg))         return t("dashboard.downloadReason.ageRestricted");
  if (/no youtube match/i.test(msg))        return t("dashboard.downloadReason.noMatch");
  if (/output file not found/i.test(msg))   return t("dashboard.downloadReason.processError");
  if (/unable to download/i.test(msg))      return t("dashboard.downloadReason.downloadError");
  if (/copyright/i.test(msg))               return t("dashboard.downloadReason.copyright");

  // Return first line of the message (yt-dlp errors can be multi-line)
  return msg.split("\n")[0].trim();
}

export const Dashboard: React.FC = () => {
  const {
    nowPlaying, appQueue, activePlaylistName, connected,
    tiktokStatus, twitchStatus, kickStatus,
    integrationEvents, queueSettings, tickerMessages,
    queueUpdateCount, playlistUpdateCount, downloadStates, downloadErrors, authAlerts, authUpdatedAt, dismissDownloadError, dismissAuthAlert,
  } = useSignalR(OVERLAY_TOKEN);

  const { t, i18n } = useTranslation();
  const { theme, toggle: toggleTheme } = useTheme();
  const toggleLanguage = useCallback(() => {
    i18n.changeLanguage(i18n.resolvedLanguage === "es" ? "en" : "es");
  }, [i18n]);
  const { likedUris, toggleLike } = useLikedSongs(playlistUpdateCount);
  const [confirmModal, confirm] = useConfirm();

  // Keep libRefreshKey in sync with server-pushed playlist changes
  useEffect(() => {
    setLibRefreshKey(k => k + 1);
  }, [playlistUpdateCount]);

  const [appVersion,       setAppVersion]       = useState<string | null>(null);
  const [section,          setSection]          = useState<AppSection>("bot");
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

  const prewarmTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const triggerPrewarmDebounced = useCallback(() => {
    if (prewarmTimerRef.current) clearTimeout(prewarmTimerRef.current);
    prewarmTimerRef.current = setTimeout(() => api.prewarmNext(2).catch(() => {}), 3000);
  }, []);
  useEffect(() => () => { if (prewarmTimerRef.current) clearTimeout(prewarmTimerRef.current); }, []);

  const handleRemove  = useCallback((uri: string)                          => { api.removeQueueItem(uri).catch(console.error); }, []);
  const handleReorder = useCallback((uri: string, toIndex: number) => {
    api.reorderQueue(uri, toIndex).catch(console.error);
    triggerPrewarmDebounced();
  }, [triggerPrewarmDebounced]);

  const handlePromoteToQueue = useCallback((uri: string, toIndex?: number) => {
    api.promoteFromBackground(uri, toIndex).catch(console.error);
    triggerPrewarmDebounced();
  }, [triggerPrewarmDebounced]);

  const handleShuffleBackground = useCallback(() => {
    api.shuffleBackgroundPlaylist().catch(console.error);
    triggerPrewarmDebounced();
  }, [triggerPrewarmDebounced]);
  const handleToggleQueue   = useCallback(() => setRightPanelMode(m => m === "queue" ? "nowplaying" : "queue"), []);
  const handleToggleDevices = useCallback(() => setRightPanelMode(m => m === "devices" ? "queue" : "devices"), []);
  const handleToggleShuffle = useCallback(async () => {
    await api.shuffleQueue().catch(() => {});
    setShuffleActive(true);
    setTimeout(() => setShuffleActive(false), 800);
  }, []);

  const handleBan     = useCallback(async (uri: string, title: string, artist: string) => {
    const ok = await confirm({ title: t("dashboard.banTitle", { title }), message: t("dashboard.banMessage"), confirmText: t("dashboard.ban"), danger: true });
    if (!ok) return;
    api.banSong(uri, title, artist).catch(console.error);
    api.removeQueueItem(uri).catch(console.error);
  }, [confirm, t]);

  const handleVote = useCallback(async (skip: boolean) => {
    const r = await api.vote(voteUser || "Admin", skip, "web").catch(() => null);
    showSimMsg(r ? r.message : t("dashboard.voteError"));
  }, [voteUser, t]);

  const handleGiftBump = useCallback(async () => {
    if (!giftUser.trim()) { showSimMsg(t("dashboard.enterUser")); return; }
    const r = await api.giftBump(giftUser.trim(), giftCoins).catch(() => null);
    showSimMsg(r ? r.message : t("common.error"));
  }, [giftUser, giftCoins, t]);

  const nowPlayingUri = (nowPlaying?.spotifyTrack ?? nowPlaying?.item?.song)?.spotifyUri ?? null;

  return (
    <div className="app-shell spotify-shell">

      {/* ── Header ──────────────────────────────────────────── */}
      <header className="app-header">
        <div className="header-left">
          <div className="header-brand" onClick={() => setSection("bot")} title={t("header.manager")}>
            <span className="header-logo"><Music2 size={20} /></span>
            <span className="header-title">MusicBot</span>
            {appVersion && <span className="header-version">v{appVersion}</span>}
          </div>

          <nav className="header-section-nav">
            {SECTIONS.map(s => (
              <button
                key={s.id}
                className={`header-section-btn${section === s.id ? " active" : ""}`}
                onClick={() => setSection(s.id)}
              >
                {s.icon} {t(s.labelKey)}
              </button>
            ))}
          </nav>
        </div>

        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <StatusBar
            signalRConnected={connected}
            tiktokStatus={tiktokStatus}
            twitchStatus={twitchStatus}
            kickStatus={kickStatus}
          />

          <button className="header-icon-btn" onClick={() => setQueueToolsOpen(true)} title={t("header.queueTools")}><Wrench size={15} /></button>
          <button className="theme-toggle-btn" onClick={toggleTheme} title={t("header.toggleTheme")}>
            {theme === "dark" ? <Sun size={15} /> : <Moon size={15} />}
          </button>
          <button className="header-icon-btn header-lang-btn" onClick={toggleLanguage} title={t("language.switch")}>
            <Languages size={15} />
            <span className="header-lang-code">{(i18n.resolvedLanguage ?? "en").toUpperCase()}</span>
          </button>
          <button className="header-icon-btn" onClick={() => api.openLog()} title={t("header.logs")}><FileText size={15} /></button>
          <button className="header-icon-btn" onClick={() => api.openLogDir()} title={t("header.logsFolder")}><FolderOpen size={15} /></button>
          <button className="header-icon-btn header-icon-btn-danger"
            onClick={async () => {
              const ok = await confirm({ title: t("dashboard.closeTitle"), message: t("dashboard.closeMessage"), confirmText: t("header.close"), danger: true });
              if (ok) api.shutdown();
            }}
            title={t("header.close")}
          ><Power size={15} /></button>
        </div>
      </header>

      {/* ── Download error toasts ───────────────────────────── */}
      {downloadErrors.length > 0 && (
        <div className="download-error-stack">
          {downloadErrors.length > 1 && (
            <div className="download-error-toolbar">
              <span className="download-error-count">{t("dashboard.errors", { count: downloadErrors.length })}</span>
              <button
                className="download-error-dismiss-all"
                onClick={() => downloadErrors.forEach(e => dismissDownloadError(e.id))}
              >
                {t("dashboard.clearAll")}
              </button>
            </div>
          )}
          {downloadErrors.slice(0, 3).map(e => (
            <div key={e.id} className="download-error-toast">
              <span className="download-error-icon">⚠</span>
              <div className="download-error-body">
                <span className="download-error-text">
                  {t("dashboard.downloadFailed")} <strong>{e.title}</strong>
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
              {t("dashboard.more", { count: downloadErrors.length - 3 })}
            </div>
          )}
        </div>
      )}

      {/* ── Auth expiration alerts ──────────────────────────── */}
      {authAlerts.length > 0 && (
        <div className="download-error-stack">
          {authAlerts.map(a => (
            <div key={a.id} className="download-error-toast auth-alert-toast">
              <span className="download-error-icon">🔑</span>
              <div className="download-error-body">
                <span className="download-error-text"><strong>{a.platform.charAt(0).toUpperCase() + a.platform.slice(1)}</strong></span>
                <span className="download-error-reason">{a.message}</span>
              </div>
              <button className="download-error-close" onClick={() => dismissAuthAlert(a.id)}>✕</button>
            </div>
          ))}
        </div>
      )}

      {/* ── Body: 2 columns ─────────────────────────────────── */}
      <div className="spotify-body">

        {/* Center: section-dependent content */}
        {section === "bot" && (
          <MainBrowser
            selectedPlaylistId={selectedPlaylist}
            onSelectPlaylist={id => setSelectedPlaylist(id)}
            onClearSelection={() => setSelectedPlaylist(null)}
            onPlaylistsChanged={() => setLibRefreshKey(k => k + 1)}
            nowPlayingUri={nowPlayingUri}
            nowPlaying={nowPlaying}
            queueUpdateCount={queueUpdateCount}
            playlistsRefreshKey={libRefreshKey}
            likedUris={likedUris}
            onToggleLike={toggleLike}
            settings={queueSettings}
            tiktokEvents={integrationEvents.filter(e => e.source === "tiktok")}
            twitchEvents={integrationEvents.filter(e => e.source === "twitch")}
            kickEvents={integrationEvents.filter(e => e.source === "kick")}
            authUpdatedAt={authUpdatedAt}
            tickerMessages={tickerMessages}
            overlayToken={OVERLAY_TOKEN}
          />
        )}
        {section === "comunidad"  && <div className="section-panel"><ComunidadPanel /></div>}
        {section === "donaciones" && <div className="section-panel"><DonacionesPanel /></div>}
        {section === "soporte"    && <div className="section-panel"><SoportePanel /></div>}

        {/* Right: Queue / Now Playing / Devices */}
        <QueuePanel
          mode={rightPanelMode}
          items={appQueue}
          nowPlaying={nowPlaying}
          onRemove={handleRemove}
          onReorder={handleReorder}
          onPromoteToQueue={handlePromoteToQueue}
          onShuffleBackground={handleShuffleBackground}
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
