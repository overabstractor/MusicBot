import React, { useEffect, useState, useCallback } from "react";
import { LibraryTrack } from "../types/models";
import { formatDuration } from "../utils";
import { api } from "../services/api";

function formatBytes(bytes: number): string {
  if (bytes === 0) return "—";
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

type SortKey = "downloadedAt" | "title" | "playCount" | "totalPlayedMs" | "fileSizeBytes";

interface Props {
  refreshKey?: number;
  saveDownloads?: boolean;
}

export const Library: React.FC<Props> = ({ refreshKey, saveDownloads = true }) => {
  const [tracks,  setTracks]  = useState<LibraryTrack[]>([]);
  const [loading, setLoading] = useState(true);
  const [sort,    setSort]    = useState<SortKey>("downloadedAt");
  const [asc,     setAsc]     = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [search,  setSearch]  = useState("");
  const [opening, setOpening] = useState(false);
  const [libMsg,  setLibMsg]  = useState<{ id: number; text: string } | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    api.getLibrary()
      .then(setTracks)
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load, refreshKey]);

  const handleSort = (key: SortKey) => {
    if (sort === key) setAsc(a => !a);
    else { setSort(key); setAsc(true); }
  };

  const handleDelete = async (id: number) => {
    await api.deleteTrack(id).catch(() => {});
    setTracks(ts => ts.filter(t => t.id !== id));
  };

  const handleClear = async () => {
    await api.clearLibrary().catch(() => {});
    setTracks([]);
    setConfirm(false);
  };

  const handleOpenFolder = async () => {
    setOpening(true);
    await api.openLibraryFolder().catch(() => {});
    setTimeout(() => setOpening(false), 1500);
  };

  const flashLib = (id: number, text: string) => {
    setLibMsg({ id, text });
    setTimeout(() => setLibMsg(null), 2500);
  };

  const handlePlay = async (t: LibraryTrack) => {
    try {
      const r = await api.playNow({ spotifyUri: t.trackId, title: t.title, artist: t.artist, coverUrl: t.coverUrl ?? undefined, durationMs: t.durationMs });
      flashLib(t.id, r.message);
    } catch (e: any) { flashLib(t.id, e.message ?? "Error"); }
  };

  const handleEnqueue = async (t: LibraryTrack) => {
    try {
      const r = await api.enqueueTrack({ spotifyUri: t.trackId, title: t.title, artist: t.artist, coverUrl: t.coverUrl ?? undefined, durationMs: t.durationMs });
      flashLib(t.id, r.message);
    } catch (e: any) { flashLib(t.id, e.message ?? "Error"); }
  };

  const filtered = search.trim()
    ? tracks.filter(t =>
        t.title.toLowerCase().includes(search.toLowerCase()) ||
        t.artist.toLowerCase().includes(search.toLowerCase())
      )
    : tracks;

  const sorted = [...filtered].sort((a, b) => {
    let v = 0;
    if (sort === "title")              v = a.title.localeCompare(b.title);
    else if (sort === "playCount")     v = a.playCount - b.playCount;
    else if (sort === "totalPlayedMs") v = a.totalPlayedMs - b.totalPlayedMs;
    else if (sort === "fileSizeBytes") v = a.fileSizeBytes - b.fileSizeBytes;
    else v = new Date(a.downloadedAt).getTime() - new Date(b.downloadedAt).getTime();
    return asc ? v : -v;
  });

  const totalSize  = tracks.reduce((s, t) => s + t.fileSizeBytes, 0);
  const totalPlays = tracks.reduce((s, t) => s + t.playCount, 0);

  const SortBtn: React.FC<{ col: SortKey; label: string }> = ({ col, label }) => (
    <th className={`lib-th sortable ${sort === col ? "active" : ""}`} onClick={() => handleSort(col)}>
      {label} {sort === col ? (asc ? "↑" : "↓") : ""}
    </th>
  );

  if (loading) return <div className="lib-empty">Cargando librería...</div>;

  return (
    <div className="library-panel">
      {!saveDownloads && (
        <div className="lib-temp-banner">
          ⚠️ Modo temporal activo — los archivos se eliminan al terminar cada canción. La librería solo muestra la sesión actual.
        </div>
      )}
      <div className="lib-toolbar">
        <div className="lib-toolbar-left">
          <div className="lib-search-wrap">
            <span className="lib-search-icon">🔍</span>
            <input
              className="lib-search-input"
              type="text"
              placeholder="Buscar por título o artista…"
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
            {search && (
              <button className="lib-search-clear" onClick={() => setSearch("")}>✕</button>
            )}
          </div>
          <div className="lib-stats">
            <span>{filtered.length}{search ? ` / ${tracks.length}` : ""} canciones</span>
            <span>·</span>
            <span>{totalPlays} reproducciones</span>
            <span>·</span>
            <span>{formatBytes(totalSize)}</span>
          </div>
        </div>
        <div className="lib-toolbar-right">
          <button className="btn btn-sm btn-outline lib-open-folder" onClick={handleOpenFolder} disabled={opening}>
            {opening ? "Abriendo…" : "📁 Abrir carpeta"}
          </button>
          {!confirm ? (
            <button className="btn btn-sm btn-danger-outline" onClick={() => setConfirm(true)} disabled={tracks.length === 0}>
              Limpiar librería
            </button>
          ) : (
            <div className="lib-confirm">
              <span>¿Eliminar {tracks.length} canciones del disco?</span>
              <button className="btn btn-sm btn-danger" onClick={handleClear}>Sí, eliminar</button>
              <button className="btn btn-sm btn-secondary" onClick={() => setConfirm(false)}>Cancelar</button>
            </div>
          )}
        </div>
      </div>

      {tracks.length === 0 ? (
        <div className="lib-empty">No hay canciones descargadas.</div>
      ) : filtered.length === 0 ? (
        <div className="lib-empty">Sin resultados para "{search}".</div>
      ) : (
        <div className="lib-table-wrap">
          <table className="lib-table">
            <thead>
              <tr>
                <th className="lib-th lib-th-cover" />
                <SortBtn col="title"         label="Canción" />
                <SortBtn col="downloadedAt"  label="Descargado" />
                <SortBtn col="playCount"     label="Veces solicitada" />
                <SortBtn col="totalPlayedMs" label="Tiempo total" />
                <SortBtn col="fileSizeBytes" label="Tamaño" />
                <th className="lib-th" />
              </tr>
            </thead>
            <tbody>
              {sorted.map(t => (
                <tr key={t.id} className={`lib-row ${!t.fileExists ? "lib-row-missing" : ""}`}>
                  <td className="lib-td lib-td-cover">
                    {t.coverUrl
                      ? <img src={t.coverUrl} alt="" className="lib-cover" />
                      : <div className="lib-cover lib-cover-placeholder">♫</div>}
                  </td>
                  <td className="lib-td lib-td-info">
                    <div className="lib-title">{t.title}</div>
                    <div className="lib-artist">{t.artist}</div>
                    <div className="lib-duration">{formatDuration(t.durationMs)}</div>
                    {!t.fileExists && <span className="lib-missing-badge">Archivo no encontrado</span>}
                  </td>
                  <td className="lib-td lib-td-center">{formatDate(t.downloadedAt)}</td>
                  <td className="lib-td lib-td-center lib-stat">{t.playCount}</td>
                  <td className="lib-td lib-td-center">{t.totalPlayedMs > 0 ? formatDuration(t.totalPlayedMs) : "—"}</td>
                  <td className="lib-td lib-td-center">{formatBytes(t.fileSizeBytes)}</td>
                  <td className="lib-td lib-td-action">
                    {libMsg?.id === t.id ? (
                      <span className="lib-feedback">{libMsg.text}</span>
                    ) : (
                      <div className="lib-actions">
                        <button className="btn btn-sm btn-primary" title="Reproducir ahora" disabled={!t.fileExists} onClick={() => handlePlay(t)}>▶</button>
                        <button className="btn btn-sm btn-outline" title="Agregar a la cola" disabled={!t.fileExists} onClick={() => handleEnqueue(t)}>+ Cola</button>
                        <button className="btn btn-icon-danger" title="Eliminar" onClick={() => handleDelete(t.id)}>🗑</button>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
};
