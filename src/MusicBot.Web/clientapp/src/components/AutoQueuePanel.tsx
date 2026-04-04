import React, { useState, useEffect, useCallback } from "react";
import { ListPlus, X } from "lucide-react";
import { api } from "../services/api";
import { Song, NowPlayingState } from "../types/models";
import { formatDuration } from "../utils";

interface AutoQueueSong {
  id: number;
  spotifyUri: string;
  title: string;
  artist: string;
  coverUrl: string;
  durationMs: number;
}

interface Props {
  nowPlaying?: NowPlayingState | null;
}

export const AutoQueuePanel: React.FC<Props> = ({ nowPlaying }) => {
  const [songs,       setSongs]       = useState<AutoQueueSong[]>([]);
  const [loading,     setLoading]     = useState(true);
  const [query,       setQuery]       = useState("");
  const [results,     setResults]     = useState<Song[]>([]);
  const [searching,   setSearching]   = useState(false);
  const [searchMsg,   setSearchMsg]   = useState("");
  const [addMsg,      setAddMsg]      = useState("");
  const [playlistUrl, setPlaylistUrl] = useState("");
  const [importing,   setImporting]   = useState(false);
  const [importMsg,   setImportMsg]   = useState("");
  const [confirm,     setConfirm]     = useState(false);

  const load = useCallback(() => {
    setLoading(true);
    api.getAutoQueue().then(setSongs).catch(() => {}).finally(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load]);

  const currentSong = nowPlaying?.spotifyTrack ?? nowPlaying?.item?.song ?? null;

  const handleSearch = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!query.trim()) return;
    setSearching(true);
    setSearchMsg("");
    setResults([]);
    try {
      const hits = await api.search(query.trim(), 10);
      setResults(hits);
      if (hits.length === 0) setSearchMsg("Sin resultados");
    } catch {
      setSearchMsg("Error al buscar");
    } finally {
      setSearching(false);
    }
  };

  const handleAddCurrentSong = async () => {
    if (!currentSong) return;
    try {
      await api.addAutoQueueSong(currentSong);
      setAddMsg(`✓ "${currentSong.title}" agregada`);
      load();
    } catch (e: unknown) {
      setAddMsg(e instanceof Error ? e.message : "Error al agregar");
    }
    setTimeout(() => setAddMsg(""), 3000);
  };

  const handleAdd = async (song: Song) => {
    try {
      await api.addAutoQueueSong(song);
      setAddMsg(`✓ "${song.title}" agregada`);
      load();
    } catch (e: unknown) {
      setAddMsg(e instanceof Error ? e.message : "Error al agregar");
    }
    setTimeout(() => setAddMsg(""), 3000);
  };

  const handleRemove = async (uri: string) => {
    await api.removeAutoQueueSong(uri).catch(() => {});
    setSongs(s => s.filter(x => x.spotifyUri !== uri));
  };

  const handleClear = async () => {
    await api.clearAutoQueue().catch(() => {});
    setSongs([]);
    setConfirm(false);
  };

  const handleImport = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!playlistUrl.trim()) return;
    setImporting(true);
    setImportMsg("");
    try {
      const res = await api.importAutoQueue(playlistUrl.trim());
      setImportMsg(`✓ ${res.added} canciones agregadas de ${res.total}`);
      setPlaylistUrl("");
      load();
    } catch (ex: unknown) {
      setImportMsg(ex instanceof Error ? ex.message : "Error al importar");
    } finally {
      setImporting(false);
    }
  };

  return (
    <div className="autoqueue-panel">
      <div className="autoqueue-header">
        <span className="autoqueue-count">{songs.length} / 100 canciones en el pool</span>
        {songs.length > 0 && (
          confirm ? (
            <span className="autoqueue-confirm">
              ¿Limpiar todo?{" "}
              <button className="btn btn-sm btn-danger" onClick={handleClear}>Sí</button>{" "}
              <button className="btn btn-sm btn-secondary" onClick={() => setConfirm(false)}>No</button>
            </span>
          ) : (
            <button className="btn btn-sm btn-danger-outline" onClick={() => setConfirm(true)}>Limpiar</button>
          )
        )}
      </div>

      {/* Current song shortcut */}
      {currentSong && (
        <div className="autoqueue-add-section">
          <div className="queue-section-label">Canción en curso</div>
          <div className="autoqueue-current-row">
            {currentSong.coverUrl && (
              <img src={currentSong.coverUrl} alt="" className="autoqueue-cover" />
            )}
            <div className="autoqueue-info">
              <span className="autoqueue-title">{currentSong.title}</span>
              <span className="autoqueue-artist">{currentSong.artist} · {formatDuration(currentSong.durationMs)}</span>
            </div>
            <button className="btn btn-sm btn-autoqueue" onClick={handleAddCurrentSong}>+ AutoCola</button>
          </div>
          {addMsg && <div className="form-message" style={{ marginTop: 6 }}>{addMsg}</div>}
        </div>
      )}

      {/* Search to add */}
      <div className="autoqueue-add-section">
        <div className="queue-section-label">Buscar canción</div>
        <form className="form-row" onSubmit={handleSearch}>
          <input
            type="text"
            className="input"
            placeholder="Nombre, artista…"
            value={query}
            onChange={e => { setQuery(e.target.value); setSearchMsg(""); }}
            autoComplete="off"
          />
          <button type="submit" className="btn btn-primary" style={{ whiteSpace: "nowrap" }} disabled={searching || !query.trim()}>
            {searching ? "Buscando…" : "Buscar"}
          </button>
        </form>

        {results.length > 0 && (
          <div className="search-results-list">
            {results.map(song => (
              <div key={song.spotifyUri} className="search-result-row">
                {song.coverUrl && <img src={song.coverUrl} alt="" className="search-result-cover" />}
                <div className="search-result-info">
                  <span className="search-result-title">{song.title}</span>
                  <span className="search-result-artist">{song.artist} · {formatDuration(song.durationMs)}</span>
                </div>
                <button className="btn btn-sm btn-primary" onClick={() => handleAdd(song)}>+</button>
              </div>
            ))}
          </div>
        )}
        {searchMsg && <div className="form-message" style={{ marginTop: 6 }}>{searchMsg}</div>}
        {addMsg    && <div className="form-message" style={{ marginTop: 6 }}>{addMsg}</div>}
      </div>

      {/* Import playlist */}
      <div className="autoqueue-add-section">
        <div className="queue-section-label">Importar playlist</div>
        <form className="form-row" onSubmit={handleImport}>
          <input
            type="text"
            className="input"
            placeholder="URL de playlist YouTube o Spotify…"
            value={playlistUrl}
            onChange={e => setPlaylistUrl(e.target.value)}
            disabled={importing}
          />
          <button type="submit" className="btn btn-primary" style={{ whiteSpace: "nowrap" }} disabled={importing || !playlistUrl.trim()}>
            {importing ? "Importando…" : "Importar"}
          </button>
        </form>
        {importMsg && <div className="form-message" style={{ marginTop: 6 }}>{importMsg}</div>}
      </div>

      {/* Song list */}
      <div className="queue-section-label">Pool de canciones</div>
      {loading ? (
        <div className="lib-empty">Cargando…</div>
      ) : songs.length === 0 ? (
        <div className="lib-empty">No hay canciones en el pool. Busca y agrega canciones arriba.</div>
      ) : (
        <div className="autoqueue-list">
          {songs.map(song => (
            <div key={song.spotifyUri} className="autoqueue-row">
              {song.coverUrl
                ? <img src={song.coverUrl} alt="" className="autoqueue-cover" />
                : <div className="autoqueue-cover autoqueue-cover-placeholder"><X size={14} /></div>}
              <div className="autoqueue-info">
                <span className="autoqueue-title">{song.title}</span>
                <span className="autoqueue-artist">{song.artist} · {formatDuration(song.durationMs)}</span>
              </div>
              <div style={{ display: "flex", gap: 4, flexShrink: 0 }}>
                <button
                  className="btn btn-icon"
                  title="Agregar a cola"
                  onClick={() => api.enqueueTrack({ spotifyUri: song.spotifyUri, title: song.title, artist: song.artist, coverUrl: song.coverUrl, durationMs: song.durationMs }, "Admin").catch(() => {})}
                >
                  <ListPlus size={14} />
                </button>
                <button className="btn-icon-danger" onClick={() => handleRemove(song.spotifyUri)} title="Eliminar">
                  <X size={14} />
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
