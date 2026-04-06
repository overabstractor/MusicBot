import React, { useState, useEffect, useCallback, useRef } from "react";
import { api } from "../services/api";
import { PlaylistLibrary, PlaylistLibrarySong, Song } from "../types/models";
import { formatDuration } from "../utils";

interface Props {
  onPlaylistActivated?: () => void;
}

export const PlaylistLibraryPanel: React.FC<Props> = ({ onPlaylistActivated }) => {
  const [playlists,       setPlaylists]       = useState<PlaylistLibrary[]>([]);
  const [selectedId,      setSelectedId]      = useState<number | null>(null);
  const [songs,           setSongs]           = useState<PlaylistLibrarySong[]>([]);
  const [loadingList,     setLoadingList]     = useState(true);
  const [loadingSongs,    setLoadingSongs]    = useState(false);
  const [msg,             setMsg]             = useState("");
  const [msgErr,          setMsgErr]          = useState(false);

  // New playlist
  const [newName,         setNewName]         = useState("");
  const [creating,        setCreating]        = useState(false);

  // Rename
  const [renamingId,      setRenamingId]      = useState<number | null>(null);
  const [renameValue,     setRenameValue]     = useState("");

  // Search to add songs
  const [query,           setQuery]           = useState("");
  const [results,         setResults]         = useState<Song[]>([]);
  const [searching,       setSearching]       = useState(false);
  const [searchMsg,       setSearchMsg]       = useState("");

  // Import
  const [importUrl,       setImportUrl]       = useState("");
  const [importing,       setImporting]       = useState(false);

  // Playback
  const [activating,      setActivating]      = useState(false);

  // Drag-and-drop reorder
  const [dragSongUri,  setDragSongUri]  = useState<string | null>(null);
  const [dropSongIdx,  setDropSongIdx]  = useState<number | null>(null);
  const dragSongIdxRef = useRef<number>(-1);

  const showMsg = (text: string, err = false) => {
    setMsg(text);
    setMsgErr(err);
    setTimeout(() => setMsg(""), 3500);
  };

  const loadPlaylists = useCallback(async () => {
    setLoadingList(true);
    try {
      const list = await api.getPlaylists();
      setPlaylists(list);
    } catch {
      // ignore
    } finally {
      setLoadingList(false);
    }
  }, []);

  useEffect(() => { loadPlaylists(); }, [loadPlaylists]);

  const loadSongs = useCallback(async (id: number) => {
    setLoadingSongs(true);
    setSongs([]);
    try {
      const list = await api.getPlaylistSongs(id);
      setSongs(list);
    } catch {
      showMsg("Error al cargar canciones", true);
    } finally {
      setLoadingSongs(false);
    }
  }, []);

  const handleSelectPlaylist = (id: number) => {
    setSelectedId(id);
    setResults([]);
    setQuery("");
    setSearchMsg("");
    loadSongs(id);
  };

  // ── Create playlist ────────────────────────────────────────────────────────

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newName.trim()) return;
    setCreating(true);
    try {
      const p = await api.createPlaylist(newName.trim());
      setNewName("");
      await loadPlaylists();
      setSelectedId(p.id);
      setSongs([]);
    } catch (ex: unknown) {
      showMsg(ex instanceof Error ? ex.message : "Error al crear lista", true);
    } finally {
      setCreating(false);
    }
  };

  // ── Delete playlist ────────────────────────────────────────────────────────

  const handleDelete = async (id: number, name: string) => {
    if (!confirm(`¿Eliminar la lista "${name}"? Esta acción no se puede deshacer.`)) return;
    try {
      await api.deletePlaylist(id);
      if (selectedId === id) { setSelectedId(null); setSongs([]); }
      await loadPlaylists();
    } catch {
      showMsg("Error al eliminar lista", true);
    }
  };

  // ── Rename playlist ────────────────────────────────────────────────────────

  const handleRenameSubmit = async (id: number) => {
    if (!renameValue.trim()) return;
    try {
      await api.renamePlaylist(id, renameValue.trim());
      setRenamingId(null);
      await loadPlaylists();
    } catch (ex: unknown) {
      showMsg(ex instanceof Error ? ex.message : "Error al renombrar", true);
    }
  };

  // ── Search ─────────────────────────────────────────────────────────────────

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

  const handleAddSong = async (song: Song) => {
    if (selectedId == null) return;
    try {
      await api.addPlaylistSong(selectedId, song);
      showMsg(`✓ "${song.title}" agregada`);
      await loadSongs(selectedId);
      await loadPlaylists();
    } catch (ex: unknown) {
      showMsg(ex instanceof Error ? ex.message : "Error al agregar", true);
    }
  };

  // ── Reorder song ───────────────────────────────────────────────────────────

  const handleReorderSong = async (uri: string, toIndex: number) => {
    if (selectedId == null) return;
    // Optimistic update
    setSongs(prev => {
      const next = [...prev];
      const fromIndex = next.findIndex(s => s.spotifyUri === uri);
      if (fromIndex < 0) return prev;
      const [moved] = next.splice(fromIndex, 1);
      next.splice(toIndex, 0, moved);
      return next;
    });
    try {
      await api.reorderPlaylistSong(selectedId, uri, toIndex);
    } catch {
      showMsg("Error al reordenar", true);
      await loadSongs(selectedId);
    }
  };

  // ── Remove song ────────────────────────────────────────────────────────────

  const handleRemoveSong = async (uri: string) => {
    if (selectedId == null) return;
    try {
      await api.removePlaylistSong(selectedId, uri);
      setSongs(s => s.filter(x => x.spotifyUri !== uri));
      await loadPlaylists();
    } catch {
      showMsg("Error al eliminar canción", true);
    }
  };

  // ── Import ─────────────────────────────────────────────────────────────────

  const handleImport = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!importUrl.trim() || selectedId == null) return;
    setImporting(true);
    try {
      const res = await api.importPlaylistSongs(selectedId, importUrl.trim());
      showMsg(`✓ ${res.added} canciones importadas de ${res.total}`);
      setImportUrl("");
      await loadSongs(selectedId);
      await loadPlaylists();
    } catch (ex: unknown) {
      showMsg(ex instanceof Error ? ex.message : "Error al importar", true);
    } finally {
      setImporting(false);
    }
  };

  // ── Play ───────────────────────────────────────────────────────────────────

  const handlePlay = async (id: number) => {
    setActivating(true);
    try {
      const res = await api.activatePlaylist(id);
      showMsg(res.message);
      await loadPlaylists();
      onPlaylistActivated?.();
    } catch (ex: unknown) {
      showMsg(ex instanceof Error ? ex.message : "Error al reproducir", true);
    } finally {
      setActivating(false);
    }
  };

  const handleDeactivate = async () => {
    try {
      await api.deactivatePlaylist();
      showMsg("Lista de reproducción detenida");
      await loadPlaylists();
    } catch {
      showMsg("Error al detener la lista", true);
    }
  };

  const selectedPlaylist = playlists.find(p => p.id === selectedId) ?? null;

  return (
    <div className="playlist-panel">

      {/* ── Left: playlist list ───────────────────────────────────────── */}
      <div className="playlist-sidebar">
        <div className="playlist-sidebar-header">
          <span className="queue-section-label" style={{ marginBottom: 0 }}>Mis listas</span>
        </div>

        {/* Create new */}
        <form className="playlist-create-form" onSubmit={handleCreate}>
          <input
            className="input input-sm"
            placeholder="Nueva lista…"
            value={newName}
            onChange={e => setNewName(e.target.value)}
            disabled={creating}
            autoComplete="off"
          />
          <button type="submit" className="btn btn-primary btn-sm" disabled={creating || !newName.trim()}>
            +
          </button>
        </form>

        {loadingList ? (
          <div className="lib-empty" style={{ padding: "24px 0" }}>Cargando…</div>
        ) : playlists.length === 0 ? (
          <div className="lib-empty" style={{ padding: "24px 12px", fontSize: 13 }}>
            No hay listas. Crea una arriba.
          </div>
        ) : (
          <div className="playlist-list">
            {playlists.map(p => (
              <div
                key={p.id}
                className={`playlist-list-item ${selectedId === p.id ? "active" : ""}`}
                onClick={() => handleSelectPlaylist(p.id)}
              >
                {renamingId === p.id ? (
                  <form
                    className="playlist-rename-form"
                    onSubmit={e => { e.preventDefault(); handleRenameSubmit(p.id); }}
                    onClick={e => e.stopPropagation()}
                  >
                    <input
                      className="input input-sm"
                      value={renameValue}
                      onChange={e => setRenameValue(e.target.value)}
                      autoFocus
                      onKeyDown={e => { if (e.key === "Escape") setRenamingId(null); }}
                    />
                    <button type="submit" className="btn btn-sm btn-primary">✓</button>
                    <button type="button" className="btn btn-sm btn-secondary" onClick={() => setRenamingId(null)}>✕</button>
                  </form>
                ) : (
                  <>
                    <div className="playlist-list-item-info">
                      {p.isActive && <span className="playlist-active-dot" title="Reproduciendo" />}
                      <span className="playlist-list-item-name">{p.name}</span>
                      <span className="playlist-list-item-count">{p.songCount}</span>
                    </div>
                    <div className="playlist-list-item-actions" onClick={e => e.stopPropagation()}>
                      <button
                        className="btn-icon-muted"
                        title="Renombrar"
                        onClick={() => { setRenamingId(p.id); setRenameValue(p.name); }}
                      >
                        ✏
                      </button>
                      <button
                        className="btn-icon-danger"
                        title="Eliminar"
                        onClick={() => handleDelete(p.id, p.name)}
                      >
                        ✕
                      </button>
                    </div>
                  </>
                )}
              </div>
            ))}
          </div>
        )}
      </div>

      {/* ── Right: playlist detail ────────────────────────────────────── */}
      <div className="playlist-detail">
        {selectedPlaylist == null ? (
          <div className="lib-empty">Selecciona una lista para ver su contenido</div>
        ) : (
          <>
            {/* Header */}
            <div className="playlist-detail-header">
              <div className="playlist-detail-title">
                <span className="tab-pane-title" style={{ fontSize: 16 }}>{selectedPlaylist.name}</span>
                <span className="count-chip">{selectedPlaylist.songCount} canciones</span>
              </div>
              <div className="playlist-detail-actions">
                {selectedPlaylist.isActive ? (
                  <button className="btn btn-sm btn-danger-outline" onClick={handleDeactivate}>
                    ⏹ Detener
                  </button>
                ) : (
                  <button
                    className="btn btn-sm btn-primary"
                    onClick={() => handlePlay(selectedPlaylist.id)}
                    disabled={activating || selectedPlaylist.songCount === 0}
                  >
                    {activating ? "Iniciando…" : "▶ Reproducir lista"}
                  </button>
                )}
              </div>
            </div>

            {msg && (
              <div className={`form-message${msgErr ? " form-message-error" : ""}`} style={{ margin: "0 0 8px" }}>
                {msg}
              </div>
            )}

            {/* Import from URL */}
            <div className="autoqueue-add-section">
              <div className="queue-section-label">Importar desde YouTube</div>
              <form className="form-row" onSubmit={handleImport}>
                <input
                  className="input"
                  placeholder="URL de lista de YouTube…"
                  value={importUrl}
                  onChange={e => setImportUrl(e.target.value)}
                  disabled={importing}
                  autoComplete="off"
                />
                <button
                  type="submit"
                  className="btn btn-primary"
                  style={{ whiteSpace: "nowrap" }}
                  disabled={importing || !importUrl.trim()}
                >
                  {importing ? "Importando…" : "Importar"}
                </button>
              </form>
            </div>

            {/* Search to add */}
            <div className="autoqueue-add-section">
              <div className="queue-section-label">Buscar y agregar canción</div>
              <form className="form-row" onSubmit={handleSearch}>
                <input
                  className="input"
                  placeholder="Nombre, artista…"
                  value={query}
                  onChange={e => { setQuery(e.target.value); setSearchMsg(""); }}
                  autoComplete="off"
                />
                <button
                  type="submit"
                  className="btn btn-primary"
                  style={{ whiteSpace: "nowrap" }}
                  disabled={searching || !query.trim()}
                >
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
                      <button className="btn btn-sm btn-primary" onClick={() => handleAddSong(song)}>+</button>
                    </div>
                  ))}
                </div>
              )}
              {searchMsg && <div className="form-message" style={{ marginTop: 6 }}>{searchMsg}</div>}
            </div>

            {/* Song list */}
            <div className="queue-section-label">Canciones</div>
            {loadingSongs ? (
              <div className="lib-empty">Cargando…</div>
            ) : songs.length === 0 ? (
              <div className="lib-empty">Lista vacía. Importa o busca canciones arriba.</div>
            ) : (
              <div className="autoqueue-list">
                {songs.map((song, i) => {
                  const isDragging   = dragSongUri === song.spotifyUri;
                  const isDropTarget = dropSongIdx === i && dragSongUri !== song.spotifyUri;
                  return (
                    <div
                      key={song.id}
                      className={`autoqueue-row${isDragging ? " queue-row-dragging" : ""}${isDropTarget ? " queue-row-drop-target" : ""}`}
                      draggable
                      onDragStart={() => { setDragSongUri(song.spotifyUri); dragSongIdxRef.current = i; }}
                      onDragOver={e => { e.preventDefault(); e.dataTransfer.dropEffect = "move"; setDropSongIdx(i); }}
                      onDrop={e => { e.preventDefault(); if (dragSongUri && dragSongIdxRef.current !== i) handleReorderSong(dragSongUri, i); setDragSongUri(null); setDropSongIdx(null); }}
                      onDragEnd={() => { setDragSongUri(null); setDropSongIdx(null); }}
                    >
                      <span className="queue-drag-handle" title="Arrastrar para reordenar">⠿</span>
                      <span style={{ fontSize: 11, color: "var(--text-muted)", minWidth: 22, textAlign: "right" }}>
                        {i + 1}
                      </span>
                      {song.coverUrl
                        ? <img src={song.coverUrl} alt="" className="autoqueue-cover" />
                        : <div className="autoqueue-cover autoqueue-cover-placeholder">♫</div>}
                      <div className="autoqueue-info">
                        <span className="autoqueue-title">{song.title}</span>
                        <span className="autoqueue-artist">{song.artist} · {formatDuration(song.durationMs)}</span>
                      </div>
                      <button
                        className="btn-icon-danger"
                        title="Quitar de lista"
                        onClick={() => handleRemoveSong(song.spotifyUri)}
                      >
                        ✕
                      </button>
                    </div>
                  );
                })}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
};
