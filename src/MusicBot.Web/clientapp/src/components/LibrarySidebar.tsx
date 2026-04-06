import React, { useState, useEffect, useCallback, useRef } from "react";
import { Plus, Heart, Pin, GripVertical, Download } from "lucide-react";
import { api } from "../services/api";
import { PlaylistLibrary } from "../types/models";
import { PlaylistCover } from "./PlaylistCover";

interface Props {
  selectedId: number | null;
  onSelect: (id: number) => void;
  refreshKey?: number;
}

export const LibrarySidebar: React.FC<Props> = ({ selectedId, onSelect, refreshKey }) => {
  const [playlists,    setPlaylists]    = useState<PlaylistLibrary[]>([]);
  const [newName,      setNewName]      = useState("");
  const [creating,     setCreating]     = useState(false);
  const [showForm,     setShowForm]     = useState(false);
  const [showImport,   setShowImport]   = useState(false);
  const [importUrl,    setImportUrl]    = useState("");
  const [importName,   setImportName]   = useState("");
  const [importing,    setImporting]    = useState(false);
  const [importMsg,    setImportMsg]    = useState<{ text: string; err: boolean } | null>(null);
  const [dragOverId,   setDragOverId]   = useState<number | null>(null);
  const dragId = useRef<number | null>(null);

  const load = useCallback(async () => {
    try { setPlaylists(await api.getPlaylists()); } catch { }
  }, []);

  useEffect(() => { load(); }, [load, refreshKey]);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newName.trim()) return;
    setCreating(true);
    try {
      const p = await api.createPlaylist(newName.trim());
      setNewName(""); setShowForm(false);
      await load();
      onSelect(p.id);
    } catch { }
    finally { setCreating(false); }
  };

  const handleImport = async (e: React.FormEvent) => {
    e.preventDefault();
    const url = importUrl.trim();
    if (!url) return;
    setImporting(true);
    setImportMsg(null);
    try {
      const name = importName.trim() || "Lista importada";
      const p    = await api.createPlaylist(name);
      const r    = await api.importPlaylistSongs(p.id, url);
      setImportMsg({ text: `✓ ${r.added} canciones importadas`, err: false });
      setImportUrl(""); setImportName("");
      await load();
      onSelect(p.id);
      setTimeout(() => { setShowImport(false); setImportMsg(null); }, 1800);
    } catch (err: unknown) {
      setImportMsg({ text: err instanceof Error ? err.message : "Error al importar", err: true });
    } finally {
      setImporting(false);
    }
  };

  const handleTogglePin = async (e: React.MouseEvent, p: PlaylistLibrary) => {
    e.stopPropagation();
    // Optimistic update
    const willPin = !p.isPinned;
    setPlaylists(prev => prev.map(pl =>
      pl.id === p.id ? { ...pl, isPinned: willPin } : pl
    ));
    try {
      await api.togglePlaylistPin(p.id);
      await load();
    } catch {
      await load();
    }
  };

  // ── Drag-to-reorder (pinned only) ─────────────────────────────────────────

  const handleDragStart = (id: number) => { dragId.current = id; };

  const handleDragOver = (e: React.DragEvent, id: number) => {
    e.preventDefault();
    if (dragId.current !== id) setDragOverId(id);
  };

  const handleDrop = async (e: React.DragEvent, targetId: number) => {
    e.preventDefault();
    setDragOverId(null);
    const fromId = dragId.current;
    dragId.current = null;
    if (fromId === null || fromId === targetId) return;

    const pinned = playlists
      .filter(p => p.isPinned)
      .sort((a, b) => (a.pinOrder ?? 0) - (b.pinOrder ?? 0));

    const fromIdx = pinned.findIndex(p => p.id === fromId);
    const toIdx   = pinned.findIndex(p => p.id === targetId);
    if (fromIdx < 0 || toIdx < 0) return;

    const reordered = [...pinned];
    const [moved]   = reordered.splice(fromIdx, 1);
    reordered.splice(toIdx, 0, moved);

    // Optimistic
    setPlaylists(prev => {
      const unpinned = prev.filter(p => !p.isPinned);
      const updated  = reordered.map((p, i) => ({ ...p, pinOrder: i }));
      return [...updated, ...unpinned];
    });

    try {
      await api.reorderPins(reordered.map(p => p.id));
    } catch {
      await load();
    }
  };

  const handleDragEnd = () => { setDragOverId(null); dragId.current = null; };

  // ── Split lists ───────────────────────────────────────────────────────────

  const pinned   = playlists.filter(p => p.isPinned).sort((a, b) => (a.pinOrder ?? 0) - (b.pinOrder ?? 0));
  const unpinned = playlists.filter(p => !p.isPinned);

  const renderItem = (p: PlaylistLibrary, draggable: boolean) => (
    <div
      key={p.id}
      className={`lib-sidebar-item-wrap${dragOverId === p.id ? " drag-over" : ""}`}
      draggable={draggable}
      onDragStart={draggable ? () => handleDragStart(p.id) : undefined}
      onDragOver={draggable ? (e) => handleDragOver(e, p.id) : undefined}
      onDrop={draggable ? (e) => handleDrop(e, p.id) : undefined}
      onDragEnd={draggable ? handleDragEnd : undefined}
    >
      {draggable && (
        <span className="lib-sidebar-grip" aria-hidden>
          <GripVertical size={13} />
        </span>
      )}
      <button
        className={`lib-sidebar-item${selectedId === p.id ? " active" : ""}`}
        onClick={() => onSelect(p.id)}
      >
        <div className={`lib-sidebar-item-icon lib-sidebar-item-cover${p.isSystem ? " lib-sidebar-liked-icon" : ""}`}>
          {p.isSystem
            ? <Heart size={16} fill="currentColor" />
            : <PlaylistCover coverUrls={p.coverUrls} iconSize={16} />}
          {p.isActive && <span className="lib-sidebar-active-dot" />}
        </div>
        <div className="lib-sidebar-item-info">
          <span className="lib-sidebar-item-name">{p.name}</span>
          <span className="lib-sidebar-item-meta">
            {p.isActive && <span className="lib-active-badge">Activa · </span>}
            {p.songCount} canciones
          </span>
        </div>
      </button>
      {!p.isSystem && (
        <button
          className={`lib-sidebar-pin-btn${p.isPinned ? " pinned" : ""}`}
          title={p.isPinned ? "Desfijar" : "Fijar"}
          onClick={(e) => handleTogglePin(e, p)}
        >
          <Pin size={12} />
        </button>
      )}
    </div>
  );

  return (
    <div className="lib-sidebar">
      <div className="lib-sidebar-header">
        <span className="lib-sidebar-title">Tu Librería</span>
        <div style={{ display: "flex", gap: 4 }}>
          <button
            className={`lib-sidebar-create-btn${showImport ? " active" : ""}`}
            title="Importar lista de YouTube"
            onClick={() => { setShowImport(v => !v); setShowForm(false); setImportMsg(null); }}
          >
            <Download size={15} />
          </button>
          <button
            className={`lib-sidebar-create-btn${showForm ? " active" : ""}`}
            title="Crear lista"
            onClick={() => { setShowForm(v => !v); setShowImport(false); }}
          >
            <Plus size={16} />
          </button>
        </div>
      </div>

      {showForm && (
        <form className="lib-sidebar-create-form" onSubmit={handleCreate}>
          <input
            className="input input-sm"
            placeholder="Nombre de la lista…"
            value={newName}
            onChange={e => setNewName(e.target.value)}
            autoFocus
            disabled={creating}
          />
          <button type="submit" className="btn btn-primary btn-sm" disabled={creating || !newName.trim()}>
            Crear
          </button>
        </form>
      )}

      {showImport && (
        <form className="lib-sidebar-create-form lib-sidebar-import-form" onSubmit={handleImport}>
          <input
            className="input input-sm"
            placeholder="URL de YouTube o YouTube Music…"
            value={importUrl}
            onChange={e => setImportUrl(e.target.value)}
            autoFocus
            disabled={importing}
          />
          <input
            className="input input-sm"
            placeholder="Nombre (opcional)"
            value={importName}
            onChange={e => setImportName(e.target.value)}
            disabled={importing}
          />
          {importMsg && (
            <span className={`lib-import-msg${importMsg.err ? " err" : ""}`}>{importMsg.text}</span>
          )}
          <button type="submit" className="btn btn-primary btn-sm" disabled={importing || !importUrl.trim()}>
            {importing ? "Importando…" : "Importar"}
          </button>
        </form>
      )}

      <div className="lib-sidebar-list">
        {playlists.length === 0 ? (
          <div className="lib-sidebar-empty">
            Crea tu primera lista con el botón +
          </div>
        ) : (
          <>
            {pinned.length > 0 && (
              <>
                <div className="lib-sidebar-section-label">Fijadas</div>
                {pinned.map(p => renderItem(p, true))}
                {unpinned.length > 0 && <div className="lib-sidebar-section-divider" />}
              </>
            )}
            {unpinned.map(p => renderItem(p, false))}
          </>
        )}
      </div>
    </div>
  );
};
