import React, { useState, useEffect, useRef } from "react";
import { Music, Volume2, RotateCcw, Headphones, MoreHorizontal, Heart, GripVertical } from "lucide-react";
import { useConfirm } from "../hooks/useConfirm";
import { QueueItem, NowPlayingState, HistoryItem } from "../types/models";
import { DownloadState } from "../hooks/useSignalR";
import { formatDuration, getPlatform } from "../utils";
import { api } from "../services/api";
import { ContextMenu, SongRef } from "./ContextMenu";

interface Props {
  mode: "queue" | "nowplaying" | "devices";
  items: QueueItem[];
  nowPlaying: NowPlayingState | null;
  onRemove: (uri: string) => void;
  onReorder?: (uri: string, toIndex: number) => void;
  onPromoteToQueue?: (uri: string, toIndex?: number) => void;
  onShuffleBackground?: () => void;
  onBan: (uri: string, title: string, artist: string) => void;
  downloadStates: Record<string, DownloadState>;
  queueUpdateCount: number;
  activePlaylistName?: string | null;
  likedUris?: Set<string>;
  onToggleLike?: (song: SongRef) => void;
}

// ── Now Playing panel ─────────────────────────────────────────────────────────

const NowPlayingView: React.FC<{ nowPlaying: NowPlayingState | null }> = ({ nowPlaying }) => {
  const song       = nowPlaying?.spotifyTrack ?? nowPlaying?.item?.song ?? null;
  const reqBy      = nowPlaying?.item?.requestedBy ?? null;
  const platform   = getPlatform(nowPlaying?.item?.platform ?? undefined);
  const isPlaylist = nowPlaying?.item?.isPlaylistItem;

  if (!song) return (
    <div className="np-view-empty">
      <div className="np-view-empty-icon"><Music size={40} /></div>
      <div>No hay canción en reproducción</div>
    </div>
  );

  return (
    <div className="np-view">
      <img src={song.coverUrl} alt="" className="np-view-cover" />
      <div className="np-view-info">
        <div className="np-view-title">{song.title}</div>
        <div className="np-view-artist">{song.artist}</div>
        {reqBy && (
          <div className="np-view-meta">
            {platform && <span className={`platform-badge ${platform.className}`}>{platform.label}</span>}
            {" "}{reqBy}
          </div>
        )}
        {isPlaylist && (
          <div className="np-view-playlist-badge">De tu lista de reproducción</div>
        )}
      </div>
      <div className="np-view-duration">{formatDuration(song.durationMs)}</div>
    </div>
  );
};

// ── Devices panel ─────────────────────────────────────────────────────────────

type AudioDevice = { id: string; name: string; isDefault: boolean };

const DevicesView: React.FC = () => {
  const [devices,  setDevices]  = useState<AudioDevice[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [loading,  setLoading]  = useState(true);

  useEffect(() => {
    api.getAudioDevices().then(({ activeDeviceId, devices: list }) => {
      setDevices(list);
      setActiveId(activeDeviceId);
      setLoading(false);
    }).catch(() => setLoading(false));
  }, []);

  const select = (id: string | null) => {
    setActiveId(id);
    api.setAudioDevice(id).catch(() => {});
  };

  return (
    <div className="devices-view">
      <div className="devices-view-title">Dispositivo de audio</div>
      {loading ? (
        <div className="devices-view-empty">Cargando…</div>
      ) : devices.length === 0 ? (
        <div className="devices-view-empty">No se detectaron dispositivos</div>
      ) : (
        <div className="devices-list">
          {devices.map(d => (
            <button
              key={d.id}
              className={`devices-item${activeId === d.id ? " active" : ""}`}
              onClick={() => select(d.id)}
            >
              <span className="devices-item-icon">
                {activeId === d.id ? <Volume2 size={16} /> : <Headphones size={16} />}
              </span>
              <div className="devices-item-info">
                <span className="devices-item-name">{d.name}</span>
                {d.isDefault && <span className="devices-item-default">Predeterminado</span>}
              </div>
              {activeId === d.id && (
                <span className="devices-item-check">
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5"><polyline points="20 6 9 17 4 12"/></svg>
                </span>
              )}
            </button>
          ))}
          {activeId !== null && (
            <button className="devices-item devices-item-reset" onClick={() => select(null)}>
              <span className="devices-item-icon"><RotateCcw size={15} /></span>
              <span>Usar predeterminado del sistema</span>
            </button>
          )}
        </div>
      )}
    </div>
  );
};

// ── History panel ─────────────────────────────────────────────────────────────

const HistoryView: React.FC<{ refreshKey: number; likedUris?: Set<string>; onToggleLike?: (song: SongRef) => void }> = ({ refreshKey, likedUris, onToggleLike }) => {
  const [history,  setHistory]  = useState<HistoryItem[]>([]);
  const [loading,  setLoading]  = useState(true);
  const [menuAnchor, setMenuAnchor] = useState<{ id: string; el: HTMLElement } | null>(null);

  useEffect(() => {
    setLoading(true);
    api.getHistory(100).then(setHistory).catch(() => {}).finally(() => setLoading(false));
  }, [refreshKey]);

  if (loading) return <div className="np-view-empty">Cargando…</div>;
  if (history.length === 0) return <div className="np-view-empty">Sin historial</div>;

  return (
    <div className="queue-items-list" style={{ position: "relative" }}>
      {history.map(item => {
        const song: SongRef = {
          spotifyUri: item.trackId,
          title:      item.title,
          artist:     item.artist,
          coverUrl:   item.coverUrl ?? undefined,
          durationMs: item.durationMs,
        };
        return (
          <div key={item.id} className="queue-item-row history-row">
            {item.coverUrl
              ? <img src={item.coverUrl} alt="" className="qi-cover" />
              : <div className="qi-cover qi-cover-ph"><Music size={14} /></div>}
            <div className="qi-info">
              <div className="qi-title">{item.title}</div>
              <div className="qi-artist">{item.artist}</div>
            </div>
            <div className="qi-actions">
              {onToggleLike && (
                <button
                  className={`qi-like-btn${likedUris?.has(song.spotifyUri) ? " liked" : ""}`}
                  onClick={e => { e.stopPropagation(); onToggleLike(song); }}
                  title={likedUris?.has(song.spotifyUri) ? "Guardado en Liked Songs" : "Guardar en Liked Songs"}
                >
                  <Heart size={13} fill={likedUris?.has(song.spotifyUri) ? "currentColor" : "none"} />
                </button>
              )}
              <button
                className="qi-menu-btn"
                onClick={e => { e.stopPropagation(); setMenuAnchor(v => v?.id === item.id ? null : { id: item.id, el: e.currentTarget }); }}
                title="Más opciones"
              ><MoreHorizontal size={15} /></button>
            </div>
            {menuAnchor?.id === item.id && (
              <ContextMenu
                song={song}
                isQueue={false}
                anchorEl={menuAnchor.el}
                onClose={() => setMenuAnchor(null)}
              />
            )}
          </div>
        );
      })}
    </div>
  );
};

// ── Main QueuePanel ───────────────────────────────────────────────────────────

export const QueuePanel: React.FC<Props> = ({
  mode, items, nowPlaying, onRemove, onReorder, onPromoteToQueue, onShuffleBackground,
  onBan, downloadStates, queueUpdateCount, activePlaylistName, likedUris, onToggleLike,
}) => {
  const [historyTab, setHistoryTab] = useState(false);
  const [menuAnchor, setMenuAnchor] = useState<{ uri: string; el: HTMLElement } | null>(null);
  const [dragUri,    setDragUri]    = useState<string | null>(null);
  const [dropIndex,  setDropIndex]  = useState<number | null>(null);
  const dragIndexRef = useRef<number>(-1);
  const [confirmModal, confirm] = useConfirm();

  const nowSong   = nowPlaying?.spotifyTrack ?? nowPlaying?.item?.song ?? null;
  const userItems = items.filter(i => !i.isPlaylistItem);
  const bgItems   = items.filter(i =>  i.isPlaylistItem);
  const realIndex = (uri: string) => items.findIndex(it => it.song.spotifyUri === uri);
  const isBgUri   = (uri: string) => bgItems.some(i => i.song.spotifyUri === uri);

  const handleClearUserQueue = async () => {
    const ok = await confirm({ title: "¿Limpiar la cola?", message: "Se eliminarán todas las canciones en espera. Esta acción no se puede deshacer.", confirmText: "Limpiar", danger: true });
    if (!ok) return;
    api.clearUserQueue().catch(console.error);
  };

  if (mode === "nowplaying") return <NowPlayingView nowPlaying={nowPlaying} />;
  if (mode === "devices")    return <DevicesView />;

  return (
    <div className="queue-panel">
      {confirmModal}
      {/* Tabs */}
      <div className="queue-panel-tabs">
        <button
          className={`queue-panel-tab${!historyTab ? " active" : ""}`}
          onClick={() => setHistoryTab(false)}
        >
          Cola {items.length > 0 && <span className="queue-panel-badge">{items.length}</span>}
        </button>
        <button
          className={`queue-panel-tab${historyTab ? " active" : ""}`}
          onClick={() => setHistoryTab(true)}
        >
          Recientes
        </button>
      </div>

      {historyTab ? (
        <HistoryView refreshKey={queueUpdateCount} likedUris={likedUris} onToggleLike={onToggleLike} />
      ) : (
        <div className="queue-items-list">

          {/* Now Playing */}
          {nowSong && (
            <div className="queue-section">
              <div className="queue-section-label-sp">Reproduciendo ahora</div>
              <div className="queue-item-row now-playing-row">
                {nowSong.coverUrl
                  ? <img src={nowSong.coverUrl} alt="" className="qi-cover" />
                  : <div className="qi-cover qi-cover-ph"><Music size={14} /></div>}
                <div className="qi-info">
                  <div className="qi-title qi-title-active">{nowSong.title}</div>
                  <div className="qi-artist">{nowSong.artist}</div>
                </div>
                {downloadStates[nowSong.spotifyUri] && (
                  <span className="qi-dl-badge">{downloadStates[nowSong.spotifyUri].pct}%</span>
                )}
                {onToggleLike && !likedUris?.has(nowSong.spotifyUri) && (
                  <div className="qi-actions">
                    <button
                      className="qi-like-btn"
                      onClick={e => { e.stopPropagation(); onToggleLike({ spotifyUri: nowSong.spotifyUri, title: nowSong.title, artist: nowSong.artist, coverUrl: nowSong.coverUrl, durationMs: nowSong.durationMs }); }}
                      title="Guardar en Liked Songs"
                    >
                      <Heart size={13} fill="none" />
                    </button>
                  </div>
                )}
              </div>
            </div>
          )}

          {/* Download banners */}
          {Object.values(downloadStates)
            .filter(dl => dl.spotifyUri !== nowSong?.spotifyUri)
            .map(dl => (
              <div key={dl.spotifyUri} className="queue-dl-banner">
                <span className="queue-dl-icon">
                  <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor"><path d="M19 9h-4V3H9v6H5l7 7 7-7zm-8 2V5h2v6h1.17L12 13.17 9.83 11H11zm-6 7h14v2H5z"/></svg>
                </span>
                <div className="qi-info">
                  <div className="qi-title" style={{ fontSize: 12 }}>{dl.title || "Descargando…"}</div>
                  <div className="queue-dl-bar">
                    <div
                      className={`queue-dl-fill${dl.pct === 0 ? " indeterminate" : ""}`}
                      style={dl.pct > 0 ? { width: `${dl.pct}%` } : undefined}
                    />
                  </div>
                </div>
                <span className="qi-dl-badge">{dl.pct}%</span>
              </div>
            ))}

          {/* User-requested items */}
          {userItems.length > 0 && (
            <div className="queue-section">
              <div className="queue-section-header">
                <span className="queue-section-label-sp">A continuación en la cola</span>
                <button className="queue-clear-btn" onClick={handleClearUserQueue} title="Limpiar cola">
                  Limpiar cola
                </button>
              </div>
              {userItems.map((item, i) => {
                const platform = getPlatform(item.platform);
                const song: SongRef = {
                  spotifyUri: item.song.spotifyUri,
                  title:      item.song.title,
                  artist:     item.song.artist,
                  coverUrl:   item.song.coverUrl,
                  durationMs: item.song.durationMs,
                };
                const isDragging   = dragUri === item.song.spotifyUri;
                const ri = realIndex(item.song.spotifyUri);
                const isDropTarget = dropIndex === ri && dragUri !== item.song.spotifyUri;
                return (
                  <div
                    key={item.song.spotifyUri}
                    className={`queue-item-row${isDragging ? " queue-row-dragging" : ""}${isDropTarget ? " queue-row-drop-target" : ""}`}
                    style={{ position: "relative" }}
                    draggable={!!onReorder || !!onPromoteToQueue}
                    onDragStart={() => { setDragUri(item.song.spotifyUri); dragIndexRef.current = ri; }}
                    onDragOver={e => { e.preventDefault(); e.dataTransfer.dropEffect = "move"; setDropIndex(ri); }}
                    onDrop={e => {
                      e.preventDefault();
                      if (dragUri && dragIndexRef.current !== ri) {
                        if (isBgUri(dragUri)) onPromoteToQueue?.(dragUri, i);
                        else onReorder?.(dragUri, ri);
                      }
                      setDragUri(null); setDropIndex(null);
                    }}
                    onDragEnd={() => { setDragUri(null); setDropIndex(null); }}
                  >
                    {onReorder && (
                      <span className="queue-drag-handle" title="Arrastrar para reordenar">
                        <GripVertical size={14} />
                      </span>
                    )}
                    <span className="qi-pos">{i + 1}</span>
                    {item.song.coverUrl
                      ? <img src={item.song.coverUrl} alt="" className="qi-cover" />
                      : <div className="qi-cover qi-cover-ph"><Music size={14} /></div>}
                    <div className="qi-info">
                      <div className="qi-title">{item.song.title}</div>
                      <div className="qi-artist">
                        {item.song.artist}
                        {item.requestedBy && item.platform !== "web" && (
                          <span className="qi-meta">
                            {" · "}
                            {platform && <span className={`platform-badge ${platform.className}`}>{platform.label}</span>}
                            {" "}{item.requestedBy}
                          </span>
                        )}
                      </div>
                    </div>
                    <div className="qi-actions">
                      {onToggleLike && (
                        <button
                          className={`qi-like-btn${likedUris?.has(song.spotifyUri) ? " liked" : ""}`}
                          onClick={e => { e.stopPropagation(); onToggleLike(song); }}
                          title={likedUris?.has(song.spotifyUri) ? "Guardado en Liked Songs" : "Guardar en Liked Songs"}
                        >
                          <Heart size={13} fill={likedUris?.has(song.spotifyUri) ? "currentColor" : "none"} />
                        </button>
                      )}
                      <button
                        className="qi-menu-btn"
                        onClick={e => { e.stopPropagation(); setMenuAnchor(v => v?.uri === item.song.spotifyUri ? null : { uri: item.song.spotifyUri, el: e.currentTarget }); }}
                        title="Más opciones"
                      ><MoreHorizontal size={15} /></button>
                    </div>
                    {item.downloadError ? (
                      <span className="qi-error-badge" title={item.downloadError}>
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor"><path d="M12 2L1 21h22L12 2zm0 3.5L20.5 19h-17L12 5.5zM11 10v4h2v-4h-2zm0 6v2h2v-2h-2z"/></svg>
                      </span>
                    ) : item.song.isDownloaded ? (
                      <span className="qi-dl-ready" title="Descargada">
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor"><path d="M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z"/></svg>
                      </span>
                    ) : !downloadStates[item.song.spotifyUri] ? (
                      <span className="qi-dl-pending" title="Pendiente de descarga">
                        <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M19 9h-4V3H9v6H5l7 7 7-7z"/></svg>
                      </span>
                    ) : null}
                    {menuAnchor?.uri === item.song.spotifyUri && (
                      <ContextMenu
                        song={song}
                        isQueue
                        anchorEl={menuAnchor.el}
                        onClose={() => setMenuAnchor(null)}
                        onRemove={onRemove}
                        onBan={onBan}
                      />
                    )}
                  </div>
                );
              })}
            </div>
          )}

          {/* Background playlist items */}
          {bgItems.length > 0 && (
            <div className="queue-section">
              <div className="queue-section-header">
                <span className="queue-section-label-sp">
                  A continuación de: <span className="queue-section-playlist-name">{activePlaylistName ?? "Tu lista"}</span>
                </span>
                {onShuffleBackground && (
                  <button className="queue-clear-btn" onClick={onShuffleBackground} title="Aleatorizar lista de fondo">
                    Aleatorizar
                  </button>
                )}
              </div>
               {bgItems.map((item, i) => {
                const song: SongRef = {
                  spotifyUri: item.song.spotifyUri,
                  title:      item.song.title,
                  artist:     item.song.artist,
                  coverUrl:   item.song.coverUrl,
                  durationMs: item.song.durationMs,
                };
                const isDragging   = dragUri === item.song.spotifyUri;
                const ri = realIndex(item.song.spotifyUri);
                const isDropTarget = dropIndex === ri && dragUri !== item.song.spotifyUri;
                return (
                  <div
                    key={item.song.spotifyUri}
                    className={`queue-item-row queue-item-bg${isDragging ? " queue-row-dragging" : ""}${isDropTarget ? " queue-row-drop-target" : ""}`}
                    style={{ position: "relative" }}
                    draggable={!!onReorder || !!onPromoteToQueue}
                    onDragStart={() => { setDragUri(item.song.spotifyUri); dragIndexRef.current = ri; }}
                    onDragOver={e => { e.preventDefault(); e.dataTransfer.dropEffect = "move"; setDropIndex(ri); }}
                    onDrop={e => {
                      e.preventDefault();
                      if (dragUri && dragIndexRef.current !== ri) {
                        // Only allow bg-to-bg reorder; ignore queue→bg drops
                        if (isBgUri(dragUri)) onReorder?.(dragUri, ri);
                      }
                      setDragUri(null); setDropIndex(null);
                    }}
                    onDragEnd={() => { setDragUri(null); setDropIndex(null); }}
                  >
                    {onReorder && (
                      <span className="queue-drag-handle" title="Arrastrar para reordenar">
                        <GripVertical size={14} />
                      </span>
                    )}
                    <span className="qi-pos">{i + 1}</span>
                    {item.song.coverUrl
                      ? <img src={item.song.coverUrl} alt="" className="qi-cover" />
                      : <div className="qi-cover qi-cover-ph"><Music size={14} /></div>}
                    <div className="qi-info">
                      <div className="qi-title">{item.song.title}</div>
                      <div className="qi-artist">{item.song.artist}</div>
                    </div>
                    <div className="qi-actions">
                      {onToggleLike && (
                        <button
                          className={`qi-like-btn${likedUris?.has(song.spotifyUri) ? " liked" : ""}`}
                          onClick={e => { e.stopPropagation(); onToggleLike(song); }}
                          title={likedUris?.has(song.spotifyUri) ? "Guardado" : "Guardar en Liked Songs"}
                        >
                          <Heart size={13} fill={likedUris?.has(song.spotifyUri) ? "currentColor" : "none"} />
                        </button>
                      )}
                      <button
                        className="qi-menu-btn"
                        onClick={e => { e.stopPropagation(); setMenuAnchor(v => v?.uri === item.song.spotifyUri ? null : { uri: item.song.spotifyUri, el: e.currentTarget }); }}
                      ><MoreHorizontal size={15} /></button>
                    </div>
                    {item.downloadError ? (
                      <span className="qi-error-badge" title={item.downloadError}>
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor"><path d="M12 2L1 21h22L12 2zm0 3.5L20.5 19h-17L12 5.5zM11 10v4h2v-4h-2zm0 6v2h2v-2h-2z"/></svg>
                      </span>
                    ) : item.song.isDownloaded ? (
                      <span className="qi-dl-ready" title="Descargada">
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor"><path d="M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z"/></svg>
                      </span>
                    ) : !downloadStates[item.song.spotifyUri] ? (
                      <span className="qi-dl-pending" title="Pendiente de descarga">
                        <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M19 9h-4V3H9v6H5l7 7 7-7z"/></svg>
                      </span>
                    ) : null}
                    {menuAnchor?.uri === item.song.spotifyUri && (
                      <ContextMenu
                        song={song}
                        isQueue={false}
                        isBackground
                        anchorEl={menuAnchor.el}
                        onClose={() => setMenuAnchor(null)}
                        onPromoteToQueue={onPromoteToQueue ? uri => onPromoteToQueue(uri) : undefined}
                      />
                    )}
                  </div>
                );
              })}
            </div>
          )}

          {!nowSong && items.length === 0 && Object.keys(downloadStates).length === 0 && (
            <div className="queue-panel-empty">
              <div style={{ opacity: 0.2, marginBottom: 10 }}><Music size={40} /></div>
              <div>La cola está vacía</div>
              <div style={{ fontSize: 12, marginTop: 6, color: "var(--text-muted)" }}>
                Busca canciones en el panel central para agregarlas
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
};
