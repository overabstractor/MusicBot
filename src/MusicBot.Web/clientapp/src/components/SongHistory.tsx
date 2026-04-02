import React, { useEffect, useState } from "react";
import { HistoryItem } from "../types/models";
import { formatDuration, getPlatform } from "../utils";
import { api } from "../services/api";

interface Props {
  onPlayNow?:        (item: HistoryItem) => void;
  onEnqueue?:        (item: HistoryItem) => void;
  onAddToAutoQueue?: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) => void;
  refreshKey?:       number;
}

export const SongHistory: React.FC<Props> = ({ onPlayNow, onEnqueue, onAddToAutoQueue, refreshKey }) => {
  const [history,  setHistory]  = useState<HistoryItem[]>([]);
  const [loading,  setLoading]  = useState(true);
  const [msg,      setMsg]      = useState<{ id: string; text: string } | null>(null);
  const [search,   setSearch]   = useState("");
  const [confirm,  setConfirm]  = useState(false);

  const load = () => {
    setLoading(true);
    api.getHistory(200)
      .then(setHistory)
      .catch(() => {})
      .finally(() => setLoading(false));
  };

  useEffect(() => { load(); }, [refreshKey]);

  const flash = (id: string, text: string) => {
    setMsg({ id, text });
    setTimeout(() => setMsg(null), 2500);
  };

  const handlePlay = async (item: HistoryItem) => {
    if (onPlayNow) { onPlayNow(item); return; }
    try {
      const r = await api.playNow({ spotifyUri: item.trackId, title: item.title, artist: item.artist, coverUrl: item.coverUrl ?? undefined, durationMs: item.durationMs });
      flash(item.id, r.message);
    } catch (e: any) { flash(item.id, e.message ?? "Error"); }
  };

  const handleEnqueue = async (item: HistoryItem) => {
    if (onEnqueue) { onEnqueue(item); return; }
    try {
      const r = await api.enqueueTrack({ spotifyUri: item.trackId, title: item.title, artist: item.artist, coverUrl: item.coverUrl ?? undefined, durationMs: item.durationMs });
      flash(item.id, r.message);
    } catch (e: any) { flash(item.id, e.message ?? "Error"); }
  };

  const handleClear = async () => {
    await api.clearHistory().catch(() => {});
    setHistory([]);
    setConfirm(false);
  };

  const filtered = search.trim()
    ? history.filter(h =>
        h.title.toLowerCase().includes(search.toLowerCase()) ||
        h.artist.toLowerCase().includes(search.toLowerCase()) ||
        (h.requestedBy ?? "").toLowerCase().includes(search.toLowerCase())
      )
    : history;

  return (
    <div className="history-panel">
      <div className="history-toolbar">
        <div className="history-search-wrap">
          <span className="history-search-icon">🔍</span>
          <input
            className="history-search-input"
            type="text"
            placeholder="Buscar por título, artista o usuario…"
            value={search}
            onChange={e => setSearch(e.target.value)}
          />
          {search && (
            <button className="history-search-clear" onClick={() => setSearch("")}>✕</button>
          )}
        </div>
        <div className="history-toolbar-right">
          <span className="history-count">
            {filtered.length}{search ? ` / ${history.length}` : ""} canciones
          </span>
          {!confirm ? (
            <button className="btn btn-sm btn-danger-outline" onClick={() => setConfirm(true)} disabled={history.length === 0}>
              Limpiar historial
            </button>
          ) : (
            <div className="history-confirm">
              <span>¿Limpiar {history.length} entradas?</span>
              <button className="btn btn-sm btn-danger" onClick={handleClear}>Sí</button>
              <button className="btn btn-sm btn-secondary" onClick={() => setConfirm(false)}>No</button>
            </div>
          )}
        </div>
      </div>

      {loading ? (
        <div className="history-empty">Cargando historial...</div>
      ) : filtered.length === 0 ? (
        <div className="history-empty">{history.length === 0 ? "No hay canciones en el historial." : "Sin resultados."}</div>
      ) : (
      <div className="history-list">
      {filtered.map((item) => {
        const platform = getPlatform(item.platform ?? undefined);
        const playedAt = new Date(item.playedAt).toLocaleString();
        const feedback = msg?.id === item.id ? msg.text : null;
        return (
          <div key={item.id} className="history-item">
            {item.coverUrl && <img src={item.coverUrl} alt="" className="history-cover" />}
            <div className="history-info">
              <div className="history-title">{item.title}</div>
              <div className="history-artist">{item.artist}</div>
              {item.requestedBy && (
                <div className="history-requested">
                  {platform && (
                    <span className={`platform-badge ${platform.className}`}>{platform.label}</span>
                  )}{" "}
                  {item.requestedBy}
                </div>
              )}
            </div>
            <div className="history-meta">
              <div className="history-duration">{formatDuration(item.durationMs)}</div>
              <div className="history-time">{playedAt}</div>
              {feedback ? (
                <div className="history-feedback">{feedback}</div>
              ) : (
                <div className="history-actions">
                  <button className="btn btn-sm btn-primary" onClick={() => handlePlay(item)} title="Reproducir ahora">▶ Ahora</button>
                  <button className="btn btn-sm btn-outline" onClick={() => handleEnqueue(item)} title="Agregar a la cola">+ Cola</button>
                  {onAddToAutoQueue && (
                    <button
                      className="btn btn-sm btn-autoqueue"
                      onClick={() => onAddToAutoQueue({ spotifyUri: item.trackId, title: item.title, artist: item.artist, coverUrl: item.coverUrl ?? undefined, durationMs: item.durationMs })}
                      title="Agregar a autocola"
                    >+ AutoCola</button>
                  )}
                </div>
              )}
            </div>
          </div>
        );
      })}
      </div>
      )}
    </div>
  );
};
