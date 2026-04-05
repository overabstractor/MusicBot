import React, { useEffect, useState, useRef, useCallback } from "react";
import {
  Shuffle, SkipBack, Play, Pause, SkipForward,
  List, Headphones, Volume2, Volume1, VolumeX,
} from "lucide-react";
import { NowPlayingState } from "../types/models";
import { formatDuration, getPlatform } from "../utils";
import { DownloadState } from "../hooks/useSignalR";
import { api } from "../services/api";

interface Props {
  state: NowPlayingState | null;
  onSkip: () => void;
  onPause: () => void;
  onResume: () => void;
  downloadStates?: Record<string, DownloadState>;
  rightPanelMode: "queue" | "nowplaying" | "devices";
  onToggleQueue: () => void;
  onToggleDevices: () => void;
  shuffleActive: boolean;
  onToggleShuffle: () => void;
}

export const PlayerBar: React.FC<Props> = ({
  state, onSkip, onPause, onResume, downloadStates,
  rightPanelMode, onToggleQueue, onToggleDevices, shuffleActive, onToggleShuffle,
}) => {
  const song     = state?.spotifyTrack ?? state?.item?.song ?? null;
  const reqBy    = state?.item?.requestedBy ?? null;
  const platform = getPlatform(state?.item?.platform ?? undefined);

  const downloadState = song
    ? (downloadStates?.[song.spotifyUri] ?? null)
    : (downloadStates ? (Object.values(downloadStates)[0] ?? null) : null);

  const [localProgress, setLocalProgress] = useState(state?.progressMs ?? 0);
  const [isSeeking,     setIsSeeking]     = useState(false);
  const [seekValue,     setSeekValue]     = useState(0);
  const [volume,        setVolume]        = useState(1.0);
  const volumeDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => { if (!isSeeking) setLocalProgress(state?.progressMs ?? 0); }, [state?.progressMs, isSeeking]);
  useEffect(() => {
    if (!state?.isPlaying || isSeeking) return;
    const id = setInterval(() => setLocalProgress(p => p + 500), 500);
    return () => clearInterval(id);
  }, [state?.isPlaying, isSeeking]);

  const duration    = song?.durationMs ?? 0;
  const displayProg = isSeeking ? seekValue : localProgress;
  const progressPct = duration > 0 ? Math.min(100, (displayProg / duration) * 100) : 0;

  const handleSeekChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    setIsSeeking(true); setSeekValue(Number(e.target.value));
  }, []);
  const handleSeekCommit = useCallback((e: React.MouseEvent<HTMLInputElement> | React.TouchEvent<HTMLInputElement>) => {
    const ms = Number((e.target as HTMLInputElement).value);
    setLocalProgress(ms); setIsSeeking(false); api.seek(ms).catch(() => {});
  }, []);
  const handleVolume = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const v = Number(e.target.value); setVolume(v);
    if (volumeDebounce.current) clearTimeout(volumeDebounce.current);
    volumeDebounce.current = setTimeout(() => api.setVolume(v).catch(() => {}), 150);
  }, []);
  const handlePrev    = useCallback(() => { api.seek(0).catch(() => {}); }, []);
  const toggleMute    = useCallback(() => {
    setVolume(v => { const nv = v === 0 ? 1 : 0; api.setVolume(nv).catch(() => {}); return nv; });
  }, []);

  const queueActive   = rightPanelMode === "queue";
  const devicesActive = rightPanelMode === "devices";
  const VolumeIcon    = volume === 0 ? VolumeX : volume < 0.5 ? Volume1 : Volume2;

  return (
    <div className="player-bar">

      {/* ── Seek bar (top border) ───────────────────────────── */}
      <div className="pb-seekbar-wrap">
        {downloadState ? (
          <div className="pb-seekbar-track">
            <div
              className={`pb-seekbar-fill${downloadState.pct === 0 ? " indeterminate" : ""}`}
              style={downloadState.pct > 0 ? { width: `${downloadState.pct}%` } : undefined}
            />
          </div>
        ) : (
          <>
            <div className="pb-seekbar-track">
              <div className="pb-seekbar-fill" style={{ width: `${progressPct}%` }} />
            </div>
            <input
              className="pb-seekbar-input"
              type="range" min={0} max={duration || 1} value={displayProg}
              onChange={handleSeekChange}
              onMouseUp={handleSeekCommit} onTouchEnd={handleSeekCommit}
            />
          </>
        )}
      </div>

      {/* ── Main row ───────────────────────────────────────── */}
      <div className="pb-row">

        {/* Left: controls + time */}
        <div className="pb-left">
          <div className="pb-controls">
            <button className="pb-ctrl-btn" onClick={handlePrev} title="Reiniciar">
              <SkipBack size={18} />
            </button>
            {state?.isPlaying ? (
              <button className="pb-ctrl-btn pb-ctrl-play" onClick={onPause} title="Pausar">
                <Pause size={20} fill="currentColor" />
              </button>
            ) : (
              <button className="pb-ctrl-btn pb-ctrl-play" onClick={onResume} title="Reanudar">
                <Play size={20} fill="currentColor" />
              </button>
            )}
            <button className="pb-ctrl-btn" onClick={onSkip} title="Siguiente">
              <SkipForward size={18} />
            </button>
          </div>
          <span className="pb-time-display">
            {formatDuration(displayProg)} / {formatDuration(duration)}
          </span>
        </div>

        {/* Center: cover + info */}
        <div className="pb-center">
          {song ? (
            <>
              {song.coverUrl
                ? <img src={song.coverUrl} alt="" className="pb-cover" />
                : <div className="pb-cover pb-cover-ph"><List size={18} /></div>}
              <div className="pb-info">
                <span className="pb-title">{song.title}</span>
                <span className="pb-artist">
                  {song.artist}
                  {reqBy && (
                    <>
                      {platform && <span className={`platform-badge ${platform.className}`} style={{ marginLeft: 6 }}>{platform.label}</span>}
                      <span className="pb-requested"> · {reqBy}</span>
                    </>
                  )}
                </span>
              </div>
            </>
          ) : downloadState ? (
            <>
              <div className="pb-cover pb-cover-dl">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                  <path d="M19 9h-4V3H9v6H5l7 7 7-7zm-8 2V5h2v6h1.17L12 13.17 9.83 11H11zm-6 7h14v2H5z"/>
                </svg>
              </div>
              <div className="pb-info">
                <span className="pb-title">{downloadState.title || "Descargando…"}</span>
                <span className="pb-artist">{downloadState.pct === 0 ? "Buscando en YouTube…" : `${downloadState.pct}%`}</span>
              </div>
            </>
          ) : (
            <span className="pb-idle">No hay canción en reproducción</span>
          )}
        </div>

        {/* Right: extras + volume */}
        <div className="pb-right">
          <button
            className={`pb-side-btn${shuffleActive ? " pb-side-btn-active" : ""}`}
            onClick={onToggleShuffle}
            title="Mezclar cola"
          >
            <Shuffle size={16} />
          </button>
          <button
            className={`pb-side-btn${queueActive ? " pb-side-btn-active" : ""}`}
            title={queueActive ? "Ocultar cola" : "Mostrar cola"}
            onClick={onToggleQueue}
          >
            <List size={16} />
          </button>
          <button
            className={`pb-side-btn${devicesActive ? " pb-side-btn-active" : ""}`}
            title={devicesActive ? "Ocultar dispositivos" : "Dispositivos de audio"}
            onClick={onToggleDevices}
          >
            <Headphones size={16} />
          </button>
          <button className="pb-side-btn" onClick={toggleMute} title="Silenciar">
            <VolumeIcon size={16} />
          </button>
          <input
            type="range" className="pb-vol"
            min={0} max={1} step={0.02} value={volume}
            onChange={handleVolume}
          />
        </div>
      </div>
    </div>
  );
};
