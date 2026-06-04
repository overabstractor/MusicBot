import React, { useEffect, useState, useRef, useCallback } from "react";
import { useTranslation } from "react-i18next";
import { NowPlayingState } from "../types/models";
import { formatDuration, getPlatform } from "../utils";
import { api } from "../services/api";

type AudioDevice = { id: string; name: string; isDefault: boolean };

interface Props {
  state: NowPlayingState | null;
  onSkip: () => void;
  onPause: () => void;
  onResume: () => void;
  downloadStates?: Record<string, import("../hooks/useSignalR").DownloadState>;
}

export const NowPlaying: React.FC<Props> = ({ state, onSkip, onPause, onResume, downloadStates }) => {
  const { t } = useTranslation();
  const song        = state?.spotifyTrack ?? state?.item?.song ?? null;
  const requestedBy = state?.item?.requestedBy ?? song?.requestedBy ?? null;
  const platform    = getPlatform(state?.item?.platform ?? song?.platform ?? undefined);

  // Resolve download state: match current song, or show first active download when idle
  const downloadState = song
    ? (downloadStates?.[song.spotifyUri] ?? null)
    : (downloadStates ? (Object.values(downloadStates)[0] ?? null) : null);

  // Local progress interpolation
  const [localProgress, setLocalProgress] = useState(state?.progressMs ?? 0);
  const [isSeeking,     setIsSeeking]     = useState(false);
  const [seekValue,     setSeekValue]     = useState(0);
  const [volume,        setVolume]        = useState(1.0);
  const [devices,       setDevices]       = useState<AudioDevice[]>([]);
  const [activeDevice,  setActiveDevice]  = useState<string | null>(null);
  const [showDevices,   setShowDevices]   = useState(false);
  const [devicesLoaded, setDevicesLoaded] = useState(false);
  const volumeDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (!isSeeking) setLocalProgress(state?.progressMs ?? 0);
  }, [state?.progressMs, isSeeking]);

  useEffect(() => {
    if (!state?.isPlaying || isSeeking) return;
    const id = setInterval(() => setLocalProgress(p => p + 500), 500);
    return () => clearInterval(id);
  }, [state?.isPlaying, isSeeking]);

  const duration = song?.durationMs || state?.spotifyTrack?.durationMs || 0;
  const pct      = duration > 0 ? Math.min((localProgress / duration) * 100, 100) : 0;

  // Seek handlers
  const handleSeekChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    setIsSeeking(true);
    setSeekValue(Number(e.target.value));
  }, []);

  const handleSeekCommit = useCallback((e: React.MouseEvent<HTMLInputElement> | React.TouchEvent<HTMLInputElement>) => {
    const ms = Number((e.target as HTMLInputElement).value);
    setLocalProgress(ms);
    setIsSeeking(false);
    api.seek(ms).catch(() => {});
  }, []);

  // Load persisted volume once on mount
  useEffect(() => {
    api.getVolume().then(({ volume: v }) => setVolume(v)).catch(() => {});
  }, []);

  // Load devices once on mount
  useEffect(() => {
    api.getAudioDevices()
      .then(({ activeDeviceId, devices: list }) => {
        setDevices(list);
        setDevicesLoaded(true);
        setActiveDevice(activeDeviceId);
      })
      .catch(err => console.warn("Could not load audio devices:", err));
  }, []);

  const toggleDevices = useCallback(() => setShowDevices(v => !v), []);

  const handleDeviceChange = useCallback((id: string | null) => {
    setActiveDevice(id);
    setShowDevices(false);
    api.setAudioDevice(id).catch(err => console.error("setAudioDevice failed:", err));
  }, []);

  // Volume handler (debounced API call)
  const handleVolume = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const v = Number(e.target.value);
    setVolume(v);
    if (volumeDebounce.current) clearTimeout(volumeDebounce.current);
    volumeDebounce.current = setTimeout(() => api.setVolume(v).catch(() => {}), 150);
  }, []);

  const deviceDropdown = showDevices && devicesLoaded ? (
    <div className="np-device-menu">
      <div className="np-device-menu-title">{t('nowPlaying.outputDevice')}</div>
      {devices.map(d => (
        <button
          key={d.id}
          className={`np-device-item${activeDevice === d.id ? " active" : ""}`}
          onClick={() => handleDeviceChange(d.id)}
        >
          {d.name}{d.isDefault ? ` (${t('nowPlaying.default')})` : ""}
        </button>
      ))}
      {activeDevice !== null && (
        <button className="np-device-item np-device-default" onClick={() => handleDeviceChange(null)}>
          ↩ {t('nowPlaying.useSystemDefault')}
        </button>
      )}
    </div>
  ) : null;

  const deviceBtn = (
    <div className="np-volume np-volume-device-row">
      <button className="np-device-btn" onClick={toggleDevices}>
        🔈 {activeDevice && devicesLoaded
          ? (devices.find(d => d.id === activeDevice)?.name ?? t('nowPlaying.audioDevice'))
          : t('nowPlaying.audioDevice')}
      </button>
    </div>
  );

  if (!song || !state) {
    return (
      <div className="now-playing-card empty">
        {downloadState ? (
          <div className="np-downloading-empty">
            <div className="np-downloading-empty-badge">⬇ {t('nowPlaying.downloading')}</div>
            <div className="np-downloading-empty-title">{downloadState.title}</div>
            <div className="np-downloading-empty-artist">{downloadState.artist}</div>
            <div className="np-downloading-empty-bar-track">
              <div
                className={`np-downloading-empty-bar-fill${downloadState.pct === 0 ? " indeterminate" : ""}`}
                style={downloadState.pct > 0 ? { width: `${downloadState.pct}%` } : undefined}
              />
            </div>
            <div className="np-downloading-empty-pct">{downloadState.pct}%</div>
            <div className="np-downloading-empty-sub">
              {downloadState.pct === 0 ? t('nowPlaying.searchingYouTube') : t('nowPlaying.downloadingAudio')}
            </div>
          </div>
        ) : (
          <div className="np-empty-text">{t('nowPlaying.empty')}</div>
        )}
        {deviceBtn}
        {deviceDropdown}
      </div>
    );
  }

  const displayProgress = isSeeking ? seekValue : localProgress;
  const displayPct      = duration > 0 ? Math.min((displayProgress / duration) * 100, 100) : 0;

  return (
    <div className="now-playing-card">
      <div className="np-bg" style={{ backgroundImage: `url(${song.coverUrl})` }} />
      <div className="np-bg-dim" />
      <div className="np-content">
        <img src={song.coverUrl} alt="" className="np-cover" />
        <div className="np-details">
          <div className="np-artist">{song.artist}</div>
          <div className="np-title">{song.title}</div>
          {requestedBy && (
            <div className="np-requested-by">
              {platform && (
                <span className={`platform-badge ${platform.className}`}>{platform.label}</span>
              )}
              {" "}{t('nowPlaying.requestedBy')} <strong>{requestedBy}</strong>
            </div>
          )}
          <div className="np-progress">
            {downloadState ? (
              <>
                <div className="np-download-bar-track">
                  <div className="np-download-bar-fill" style={{ width: `${downloadState.pct}%` }} />
                </div>
                <div className="np-time">
                  <span className="np-download-label">
                    ⬇ {downloadState.pct === 0 ? t('nowPlaying.searchingYouTube') : t('nowPlaying.downloadingShort')}
                  </span>
                  <span className="np-download-pct">{downloadState.pct}%</span>
                </div>
              </>
            ) : (
              <>
                <input
                  type="range"
                  className="np-seek-bar"
                  min={0}
                  max={duration}
                  value={isSeeking ? seekValue : localProgress}
                  onChange={handleSeekChange}
                  onMouseUp={handleSeekCommit}
                  onTouchEnd={handleSeekCommit}
                />
                <div className="np-time">
                  <span>{formatDuration(displayProgress)}</span>
                  <span>{formatDuration(duration)}</span>
                </div>
              </>
            )}
          </div>
          <div className="np-volume">
            <span className="np-volume-icon">{volume === 0 ? "🔇" : volume < 0.5 ? "🔉" : "🔊"}</span>
            <input
              type="range"
              className="np-volume-bar"
              min={0}
              max={1}
              step={0.02}
              value={volume}
              onChange={handleVolume}
            />
          </div>
          {deviceBtn}
          {deviceDropdown}
        </div>
        <div className="np-controls">
          {state.isPlaying ? (
            <button className="btn btn-control" onClick={onPause} title={t('nowPlaying.pause')}>⏸</button>
          ) : (
            <button className="btn btn-control" onClick={onResume} title={t('nowPlaying.resume')}>▶</button>
          )}
          <button className="btn btn-control" onClick={onSkip} title={t('nowPlaying.skip')}>⏭</button>
        </div>
      </div>
    </div>
  );
};
