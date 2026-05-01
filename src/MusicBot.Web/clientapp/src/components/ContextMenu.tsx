import React, { useState, useRef, useEffect } from "react";
import { createPortal } from "react-dom";
import { Play, Plus, X, Ban, ListMusic, ChevronLeft, Search, Heart, SkipForward } from "lucide-react";
import { api } from "../services/api";
import { PlaylistMembership } from "../types/models";

export type SongRef = {
  spotifyUri: string;
  title: string;
  artist: string;
  coverUrl?: string;
  durationMs: number;
};

interface Props {
  song: SongRef;
  /** True when the song is already in the queue (shows remove/ban instead of play/enqueue) */
  isQueue: boolean;
  /** True when this is a background playlist item — shows "Mover a la cola" */
  isBackground?: boolean;
  onClose: () => void;
  onRemove?: (uri: string) => void;
  onBan?: (uri: string, title: string, artist: string) => void;
  onPromoteToQueue?: (uri: string) => void;
  /** If set, opens directly in the playlist/memberships view */
  defaultView?: "main" | "playlist";
  /** True when this is the currently playing track — shows Skip instead of Play/Enqueue */
  isNowPlaying?: boolean;
  onSkip?: () => void;
  /** The trigger element — used to calculate fixed position so the menu
   *  escapes overflow:hidden and stacking-context constraints. */
  anchorEl?: HTMLElement | null;
}

type View = "main" | "playlist";

export const ContextMenu: React.FC<Props> = ({
  song, isQueue, isBackground, onClose, onRemove, onBan, onPromoteToQueue, defaultView, isNowPlaying, onSkip, anchorEl,
}) => {
  const ref = useRef<HTMLDivElement>(null);
  const [view,         setView]         = useState<View>(defaultView ?? "main");
  const [memberships,  setMemberships]  = useState<PlaylistMembership[] | null>(null);
  const [search,       setSearch]       = useState("");
  const [newName,      setNewName]      = useState("");
  const [showCreate,   setShowCreate]   = useState(false);
  const [feedback,     setFeedback]     = useState<string | null>(null);
  const [busy,         setBusy]         = useState(false);
  const [pos,          setPos]          = useState<React.CSSProperties>({});

  // Close on outside click
  useEffect(() => {
    const h = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose();
    };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, [onClose]);

  // Calculate fixed position from the anchor element so the menu renders
  // at the viewport level — escapes overflow:hidden and stacking contexts.
  useEffect(() => {
    const anchor = anchorEl ?? ref.current?.previousElementSibling as HTMLElement | null;
    const menu   = ref.current;
    if (!anchor || !menu) return;

    const ar = anchor.getBoundingClientRect();
    const menuH = menu.offsetHeight || 300;
    const spaceBelow = window.innerHeight - ar.bottom;
    const spaceAbove = ar.top;

    const top = spaceBelow >= menuH || spaceBelow >= spaceAbove
      ? ar.bottom + 4
      : ar.top - menuH - 4;

    // Align right edge of menu with right edge of anchor button
    const right = window.innerWidth - ar.right;

    setPos({ position: "fixed", top, right, bottom: "auto", left: "auto" });
  }, [anchorEl]);

  // Fetch memberships when entering playlist view
  const openPlaylistView = async () => {
    setView("playlist");
    if (memberships == null) {
      try {
        const m = await api.getSongMemberships(song.spotifyUri);
        setMemberships(m);
      } catch { setMemberships([]); }
    }
  };

  // If defaultView="playlist", fetch on mount
  useEffect(() => {
    if (defaultView === "playlist") openPlaylistView();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const toggleMembership = async (m: PlaylistMembership) => {
    if (busy) return;
    setBusy(true);
    try {
      const result = await api.toggleMembership(m.id, song);
      setMemberships(prev => prev?.map(p =>
        p.id === m.id ? { ...p, isInPlaylist: result.isInPlaylist, songCount: result.isInPlaylist ? p.songCount + 1 : p.songCount - 1 } : p
      ) ?? null);
    } catch {
      // Revert optimistic state by re-fetching from server
      try { setMemberships(await api.getSongMemberships(song.spotifyUri)); } catch { }
    }
    finally { setBusy(false); }
  };

  const createAndAdd = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newName.trim()) return;
    setBusy(true);
    try {
      const pl = await api.createPlaylist(newName.trim());
      await api.addPlaylistSong(pl.id, song);
      // Re-fetch memberships to show the new playlist with checkmark
      const updated = await api.getSongMemberships(song.spotifyUri).catch(() => null);
      if (updated) setMemberships(updated);
      setNewName(""); setShowCreate(false);
      setFeedback(`✓ Guardada en "${pl.name}"`);
      setTimeout(() => setFeedback(null), 1800);
    } catch (err: unknown) {
      setFeedback(err instanceof Error ? err.message : "Error");
    }
    finally { setBusy(false); }
  };

  const handlePlayNow = async () => {
    try { await api.playNow(song, "Admin"); } catch { }
    onClose();
  };
  const handleEnqueue = async () => {
    try { await api.enqueueTrack(song, "Admin"); } catch { }
    onClose();
  };

  // Split memberships: saved + unsaved, system (Liked Songs) always first
  const saved   = memberships?.filter(m =>  m.isInPlaylist).sort((a, b) => b.isSystem ? 1 : -1) ?? [];
  const unsaved = memberships?.filter(m => !m.isInPlaylist).sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime()) ?? [];
  const filtered = (arr: PlaylistMembership[]) =>
    search ? arr.filter(m => m.name.toLowerCase().includes(search.toLowerCase())) : arr;

  const menu = (
    <div className="ctx-menu" ref={ref} style={pos}>
      {/* ── Main view ── */}
      {view === "main" && (
        <>
          {isNowPlaying ? (
            <button className="ctx-item" onClick={() => { onSkip?.(); onClose(); }}>
              <SkipForward size={13} /> Skipear
            </button>
          ) : (
            <>
              {!isQueue && (
                <button className="ctx-item" onClick={handlePlayNow}>
                  <Play size={13} fill="currentColor" /> Reproducir ahora
                </button>
              )}
              {!isQueue && (
                <button className="ctx-item" onClick={handleEnqueue}>
                  <Plus size={13} /> Agregar a cola
                </button>
              )}
            </>
          )}
          {isBackground && onPromoteToQueue && (
            <button className="ctx-item" onClick={() => { onPromoteToQueue(song.spotifyUri); onClose(); }}>
              <Plus size={13} /> Mover a la cola
            </button>
          )}
          <button className="ctx-item" onClick={openPlaylistView}>
            <ListMusic size={13} /> Guardar en playlist
          </button>
          {onRemove && !isNowPlaying && (
            <>
              <div className="ctx-separator" />
              <button className="ctx-item" onClick={() => { onRemove(song.spotifyUri); onClose(); }}>
                <X size={13} /> {isQueue ? "Eliminar de cola" : "Quitar de lista"}
              </button>
            </>
          )}
          {isQueue && !isNowPlaying && onBan && (
            <button className="ctx-item ctx-item-danger" onClick={() => { onBan(song.spotifyUri, song.title, song.artist); onClose(); }}>
              <Ban size={13} /> Banear canción
            </button>
          )}
        </>
      )}

      {/* ── Playlist memberships view ── */}
      {view === "playlist" && (
        <>
          {/* Header */}
          <div className="ctx-pl-header">
            {defaultView !== "playlist" && (
              <button className="ctx-pl-back" onClick={() => setView("main")}>
                <ChevronLeft size={14} />
              </button>
            )}
            <span className="ctx-pl-title">Guardar en playlist</span>
          </div>

          {feedback ? (
            <div className="ctx-pl-feedback">{feedback}</div>
          ) : (
            <>
              {/* Search */}
              <div className="ctx-pl-search-wrap">
                <Search size={12} className="ctx-pl-search-icon" />
                <input
                  className="ctx-pl-search"
                  placeholder="Buscar lista…"
                  value={search}
                  onChange={e => setSearch(e.target.value)}
                  autoFocus={defaultView !== "playlist"}
                />
              </div>

              {/* Create new */}
              {!showCreate ? (
                <button className="ctx-item ctx-item-create" onClick={() => setShowCreate(true)}>
                  <Plus size={13} /> Nueva lista
                </button>
              ) : (
                <form className="ctx-pl-create-form" onSubmit={createAndAdd}>
                  <input
                    className="ctx-pl-search"
                    placeholder="Nombre de la lista…"
                    value={newName}
                    onChange={e => setNewName(e.target.value)}
                    autoFocus
                    disabled={busy}
                  />
                  <button type="submit" className="ctx-pl-create-btn" disabled={busy || !newName.trim()}>
                    Crear
                  </button>
                </form>
              )}

              {memberships == null ? (
                <div className="ctx-pl-empty">Cargando…</div>
              ) : (
                <div className="ctx-pl-list">
                  {/* Saved in section */}
                  {filtered(saved).length > 0 && (
                    <>
                      <div className="ctx-pl-section-label">Guardado en</div>
                      {filtered(saved).map(m => (
                        <button key={m.id} className="ctx-item ctx-item-membership" onClick={() => toggleMembership(m)} disabled={busy}>
                          <span className={`ctx-pl-icon${m.isSystem ? " liked" : ""}`}>
                            {m.isSystem ? <Heart size={13} fill="currentColor" /> : <ListMusic size={13} />}
                          </span>
                          <span className="ctx-pl-name">{m.name}</span>
                          <span className="ctx-pl-count">{m.songCount}</span>
                          <span className="ctx-membership-check saved">
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5"><polyline points="20 6 9 17 4 12"/></svg>
                          </span>
                        </button>
                      ))}
                    </>
                  )}

                  {/* Recently updated (not saved) */}
                  {filtered(unsaved).length > 0 && (
                    <>
                      <div className="ctx-pl-section-label">
                        {filtered(saved).length > 0 ? "Actualizado recientemente" : "Tus listas"}
                      </div>
                      {filtered(unsaved).map(m => (
                        <button key={m.id} className="ctx-item ctx-item-membership" onClick={() => toggleMembership(m)} disabled={busy}>
                          <span className={`ctx-pl-icon${m.isSystem ? " liked" : ""}`}>
                            {m.isSystem ? <Heart size={13} /> : <ListMusic size={13} />}
                          </span>
                          <span className="ctx-pl-name">{m.name}</span>
                          <span className="ctx-pl-count">{m.songCount}</span>
                          <span className="ctx-membership-check">
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" opacity="0.3"><circle cx="12" cy="12" r="9"/></svg>
                          </span>
                        </button>
                      ))}
                    </>
                  )}

                  {filtered(saved).length === 0 && filtered(unsaved).length === 0 && (
                    <div className="ctx-pl-empty">Sin resultados</div>
                  )}
                </div>
              )}
            </>
          )}
        </>
      )}
    </div>
  );

  return createPortal(menu, document.body);
};
