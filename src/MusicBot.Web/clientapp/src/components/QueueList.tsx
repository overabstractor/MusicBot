import React, { useState, useRef } from "react";
import { QueueItem } from "../types/models";
import { formatDuration, getPlatform } from "../utils";
import { DownloadState } from "../hooks/useSignalR";

interface Props {
  items:              QueueItem[];
  onRemove?:          (uri: string) => void;
  onReorder?:         (uri: string, toIndex: number) => void;
  onBan?:             (uri: string, title: string, artist: string) => void;
  onAddToAutoQueue?:  (song: import("../types/models").Song) => void;
  downloadStates?:    Record<string, DownloadState>;
}

export const QueueList: React.FC<Props> = ({ items, onRemove, onReorder, onBan, onAddToAutoQueue, downloadStates }) => {
  const activeDownloads = Object.values(downloadStates ?? {});
  const [dragUri,      setDragUri]      = useState<string | null>(null);
  const [dropIndex,    setDropIndex]    = useState<number | null>(null);
  const dragIndexRef = useRef<number>(-1);

  if (items.length === 0 && activeDownloads.length === 0) {
    return <div className="queue-empty">La cola está vacía</div>;
  }

  const handleDragStart = (uri: string, index: number) => {
    setDragUri(uri);
    dragIndexRef.current = index;
  };

  const handleDragOver = (e: React.DragEvent, index: number) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    setDropIndex(index);
  };

  const handleDrop = (e: React.DragEvent, toIndex: number) => {
    e.preventDefault();
    if (dragUri && dragIndexRef.current !== toIndex) {
      onReorder?.(dragUri, toIndex);
    }
    setDragUri(null);
    setDropIndex(null);
  };

  const handleDragEnd = () => {
    setDragUri(null);
    setDropIndex(null);
  };

  return (
    <div className="queue-list-panel">
      {activeDownloads.map(dl => (
        <div key={dl.spotifyUri} className="queue-download-banner">
          <div className="queue-download-header">
            <span className="queue-download-icon">⬇</span>
            <div className="queue-download-song">
              <span className="queue-download-song-label">Descargando</span>
              <span className="queue-download-song-title">{dl.title}</span>
            </div>
            <span className="queue-download-pct">{dl.pct}%</span>
          </div>
          <div className="queue-download-bar-track">
            <div
              className={`queue-download-bar-fill${dl.pct === 0 ? " indeterminate" : ""}`}
              style={dl.pct > 0 ? { width: `${dl.pct}%` } : undefined}
            />
          </div>
        </div>
      ))}
      {items.map((item, index) => {
        const { song } = item;
        const platform  = getPlatform(item.platform);
        const isDragging = dragUri === song.spotifyUri;
        const isDropTarget = dropIndex === index && dragUri !== song.spotifyUri;
        const dlState = downloadStates?.[song.spotifyUri];

        return (
          <div
            key={song.spotifyUri}
            className={`queue-row${item.isPlaylistItem ? " queue-row-playlist" : " queue-row-requested"}${isDragging ? " queue-row-dragging" : ""}${isDropTarget ? " queue-row-drop-target" : ""}`}
            draggable={!!onReorder}
            onDragStart={() => handleDragStart(song.spotifyUri, index)}
            onDragOver={(e) => handleDragOver(e, index)}
            onDrop={(e) => handleDrop(e, index)}
            onDragEnd={handleDragEnd}
            onDragLeave={() => {}}
          >
            {onReorder && (
              <span className="queue-drag-handle" title="Arrastrar para reordenar">⠿</span>
            )}
            <span className="queue-pos">{index + 1}</span>
            {song.coverUrl && (
              <img src={song.coverUrl} alt="" className="queue-cover" />
            )}
            <div className="queue-info">
              <div className="queue-song-title">{song.title}</div>
              <div className="queue-song-artist">{song.artist}</div>
              <div className="queue-song-meta">
                {formatDuration(song.durationMs)}
                {!song.isDownloaded && !dlState && (
                  <span className="queue-dl-badge" title="Pendiente de descarga">⬇</span>
                )}
                {item.requestedBy && (
                  <span className="queue-requested-by">
                    {" · "}
                    {platform && (
                      <span className={`platform-badge ${platform.className}`}>{platform.label}</span>
                    )}
                    {" "}<strong>{item.requestedBy}</strong>
                  </span>
                )}
              </div>
              {dlState && (
                <div className="queue-item-dl-progress">
                  <div className="queue-item-dl-bar-track">
                    <div className="queue-item-dl-bar-fill" style={{ width: `${dlState.pct}%` }} />
                  </div>
                  <span className="queue-item-dl-pct">⬇ {dlState.pct}%</span>
                </div>
              )}
            </div>
            <div className="queue-row-actions">
              {onAddToAutoQueue && (
                <button
                  className="btn btn-icon btn-autoqueue-queue"
                  onClick={() => onAddToAutoQueue(song)}
                  title="Agregar a autocola"
                >🎲</button>
              )}
              {onBan && (
                <button
                  className="btn btn-icon btn-ban-queue"
                  onClick={() => onBan(song.spotifyUri, song.title, song.artist)}
                  title="Banear canción"
                >🚫</button>
              )}
              {onRemove && (
                <button
                  className="btn btn-icon btn-remove-queue"
                  onClick={() => onRemove(song.spotifyUri)}
                  title="Eliminar de la cola"
                >✕</button>
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
};
