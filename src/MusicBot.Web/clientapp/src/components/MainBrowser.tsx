import React, { useState, useEffect, useCallback, useRef } from "react";
import {
  Search, X, Play, Plus, Music, ArrowLeft, Trash2, MoreHorizontal,
  Shuffle, Home, ListMusic, Download, Heart, Library,
  Settings, Zap, Monitor, MessageSquare,
} from "lucide-react";
import { api } from "../services/api";
import { Song, PlaylistLibrary, PlaylistLibrarySong } from "../types/models";
import { useConfirm } from "../hooks/useConfirm";
import { formatDuration } from "../utils";
import { PlaylistCover } from "./PlaylistCover";
import { ContextMenu, SongRef } from "./ContextMenu";
import { SettingsPanel } from "./SettingsPanel";
import { PlatformConnections } from "./PlatformConnections";
import { OverlayLinks } from "./OverlayLinks";
import { TickerMessages } from "./TickerMessages";
import { QueueSettings, IntegrationEvent, TickerMessage } from "../hooks/useSignalR";

type BrowserTab = "home" | "settings" | "platforms" | "overlays" | "ticker";

const BROWSER_TABS: { id: BrowserTab; label: string; icon: React.ReactNode }[] = [
  { id: "home",      label: "Mi librería", icon: <Library size={13} />        },
  { id: "platforms", label: "Plataformas", icon: <Zap size={13} />            },
  { id: "overlays",  label: "Overlays",    icon: <Monitor size={13} />        },
  { id: "ticker",    label: "Mensajes",    icon: <MessageSquare size={13} />  },
  { id: "settings",  label: "Ajustes",     icon: <Settings size={13} />       },
];

interface Props {
  selectedPlaylistId: number | null;
  onSelectPlaylist: (id: number) => void;
  onClearSelection: () => void;
  onPlaylistsChanged: () => void;
  nowPlayingUri?: string | null;
  queueUpdateCount: number;
  playlistsRefreshKey?: number;
  likedUris?: Set<string>;
  onToggleLike?: (song: SongRef) => void;
  // Settings tabs props
  settings: QueueSettings;
  tiktokEvents: IntegrationEvent[];
  twitchEvents: IntegrationEvent[];
  kickEvents: IntegrationEvent[];
  tickerMessages: TickerMessage[];
  overlayToken: string;
}

export const MainBrowser: React.FC<Props> = ({
  selectedPlaylistId, onSelectPlaylist, onClearSelection, onPlaylistsChanged, nowPlayingUri, queueUpdateCount,
  playlistsRefreshKey, likedUris, onToggleLike,
  settings, tiktokEvents, twitchEvents, kickEvents, tickerMessages, overlayToken,
}) => {
  const [confirmModal, confirm] = useConfirm();
  const [browserTab, setBrowserTab] = useState<BrowserTab>("home");
  // ── Global search ──────────────────────────────────────────────────────────
  const [query,       setQuery]       = useState("");
  const [searching,   setSearching]   = useState(false);
  const [results,     setResults]     = useState<Song[] | null>(null);
  const [searchMsg,   setSearchMsg]   = useState("");

  // ── Home data ──────────────────────────────────────────────────────────────
  const [playlists,     setPlaylists]     = useState<PlaylistLibrary[]>([]);
  const [loadingHome,   setLoadingHome]   = useState(false);

  // ── Library create / import ────────────────────────────────────────────────
  const [showLibCreate,  setShowLibCreate]  = useState(false);
  const [libCreateName,  setLibCreateName]  = useState("");
  const [libCreating,    setLibCreating]    = useState(false);
  const [showLibImport,  setShowLibImport]  = useState(false);
  const [libImportUrl,   setLibImportUrl]   = useState("");
  const [libImportName,  setLibImportName]  = useState("");
  const [libImporting,   setLibImporting]   = useState(false);
  const [libImportMsg,   setLibImportMsg]   = useState<{ text: string; err: boolean } | null>(null);

  // ── Saved playlist detail ──────────────────────────────────────────────────
  const [playlist,        setPlaylist]        = useState<PlaylistLibrary | null>(null);
  const [songs,           setSongs]           = useState<PlaylistLibrarySong[]>([]);
  const [loadingDetail,   setLoadingDetail]   = useState(false);
  const [activating,      setActivating]      = useState(false);
  const [shufflePlaylist, setShufflePlaylist] = useState(false);

  // ── Unsaved playlist preview (from search result) ─────────────────────────
  const [previewMeta,    setPreviewMeta]    = useState<Song | null>(null);   // the playlist Song entry
  const [previewTracks,  setPreviewTracks]  = useState<Song[] | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [savingLibrary,  setSavingLibrary]  = useState(false);
  const [saveNameInput,  setSaveNameInput]  = useState("");
  const [showSaveForm,   setShowSaveForm]   = useState(false);

  // ── Add to playlist detail search ──────────────────────────────────────────
  const [addQuery,     setAddQuery]     = useState("");
  const [addResults,   setAddResults]   = useState<Song[]>([]);
  const [addSearching, setAddSearching] = useState(false);

  // ── Import ─────────────────────────────────────────────────────────────────
  const [importUrl,  setImportUrl]  = useState("");
  const [importing,  setImporting]  = useState(false);

  // ── Song drag-and-drop reorder ────────────────────────────────────────────
  const [dragSongUri,  setDragSongUri]  = useState<string | null>(null);
  const [dropSongIdx,  setDropSongIdx]  = useState<number | null>(null);
  const dragSongIdxRef = useRef<number>(-1);

  // ── Context menus ──────────────────────────────────────────────────────────
  const [menuUri,     setMenuUri]     = useState<string | null>(null);
  // likeMenuUri: opens ContextMenu in "playlist" view (for ♥ click on liked songs)
  const [likeMenuUri, setLikeMenuUri] = useState<string | null>(null);

  // ── Search dropdown ────────────────────────────────────────────────────────
  const [dropdownOpen,  setDropdownOpen]  = useState(false);
  const [searchFilter,  setSearchFilter]  = useState<"all" | "songs" | "playlists">("all");
  const searchWrapRef = useRef<HTMLDivElement>(null);

  // ── Action feedback ────────────────────────────────────────────────────────
  const [actionMsg, setActionMsg] = useState<{ text: string; err: boolean } | null>(null);
  const actionTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const flash = useCallback((text: string, err = false) => {
    setActionMsg({ text, err });
    if (actionTimer.current) clearTimeout(actionTimer.current);
    actionTimer.current = setTimeout(() => setActionMsg(null), 3000);
  }, []);

  // ── Load home data ─────────────────────────────────────────────────────────
  const loadHome = useCallback(async () => {
    setLoadingHome(true);
    try {
      setPlaylists(await api.getPlaylists());
    } catch { }
    finally { setLoadingHome(false); }
  }, []);

  useEffect(() => {
    if (selectedPlaylistId == null && results == null && previewMeta == null) loadHome();
  }, [selectedPlaylistId, results, previewMeta, loadHome, queueUpdateCount, playlistsRefreshKey]);

  // ── Load saved playlist detail ─────────────────────────────────────────────
  const loadDetail = useCallback(async (id: number) => {
    setLoadingDetail(true);
    try {
      const [pls, allPls, songList] = await Promise.all([
        api.getPlaylists().then(l => l.find(p => p.id === id) ?? null),
        api.getPlaylists(),
        api.getPlaylistSongs(id),
      ]);
      setPlaylist(pls);
      setSongs(songList);
      setPlaylists(allPls);
    } catch { }
    finally { setLoadingDetail(false); }
  }, []);

  useEffect(() => {
    if (selectedPlaylistId != null) {
      setResults(null);
      setPreviewMeta(null);
      setPreviewTracks(null);
      setQuery("");
      setAddQuery("");
      setAddResults([]);
      setImportUrl("");
      loadDetail(selectedPlaylistId);
    }
  }, [selectedPlaylistId, loadDetail, playlistsRefreshKey]);

  // ── Global search — debounced as-you-type ─────────────────────────────────
  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      if (!q) { setResults(null); setDropdownOpen(false); setSearchMsg(""); setSearchFilter("all"); }
      return;
    }
    setSearching(true);
    const t = setTimeout(async () => {
      try {
        const hits = await api.search(q, 15);
        setResults(hits);
        setDropdownOpen(true);
        setSearchMsg(hits.length === 0 ? "Sin resultados" : "");
      } catch { setSearchMsg("Error al buscar"); }
      finally { setSearching(false); }
    }, 420);
    return () => { clearTimeout(t); setSearching(false); };
  }, [query]);

  // Close dropdown on outside click
  useEffect(() => {
    const h = (e: MouseEvent) => {
      if (searchWrapRef.current && !searchWrapRef.current.contains(e.target as Node))
        setDropdownOpen(false);
    };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, []);

  // Close dropdown on ESC
  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if (e.key === "Escape") { setDropdownOpen(false); setQuery(""); setResults(null); setSearchMsg(""); }
    };
    document.addEventListener("keydown", h);
    return () => document.removeEventListener("keydown", h);
  }, []);

  const clearSearch = () => {
    setResults(null); setQuery(""); setSearchMsg("");
    setDropdownOpen(false);
    setPreviewMeta(null); setPreviewTracks(null);
  };

  const goHome = () => {
    clearSearch();
    onClearSelection();
  };

  // ── Unsaved playlist preview ───────────────────────────────────────────────
  const handlePreviewPlaylist = async (pl: Song) => {
    if (!pl.playlistUrl) return;
    setDropdownOpen(false);
    setPreviewMeta(pl);
    setPreviewTracks(null);
    setPreviewLoading(true);
    setSaveNameInput(pl.title);
    setShowSaveForm(false);
    try {
      const tracks = await api.getPlaylistTracks(pl.playlistUrl, 200);
      setPreviewTracks(tracks);
    } catch { flash("Error al cargar la lista", true); }
    finally { setPreviewLoading(false); }
  };

  const handleEnqueueAllPreview = async () => {
    if (!previewMeta?.playlistUrl) return;
    try {
      const r = await api.importPlaylist(previewMeta.playlistUrl, "Admin");
      flash(`✓ ${r.added} canciones añadidas a la cola`);
    } catch (e: unknown) { flash(e instanceof Error ? e.message : "Error", true); }
  };

  const handleSaveToLibrary = async () => {
    const name = saveNameInput.trim() || previewMeta?.title || "Nueva lista";
    if (!previewMeta?.playlistUrl) return;
    setSavingLibrary(true);
    try {
      const created = await api.createPlaylist(name);
      const r = await api.importPlaylistSongs(created.id, previewMeta.playlistUrl);
      flash(`✓ Lista "${name}" guardada con ${r.added} canciones`);
      setShowSaveForm(false);
      onPlaylistsChanged();
      loadHome();
    } catch (e: unknown) { flash(e instanceof Error ? e.message : "Error al guardar", true); }
    finally { setSavingLibrary(false); }
  };

  // ── Enqueue from search ────────────────────────────────────────────────────
  const handleEnqueue = async (song: Song) => {
    try {
      await api.enqueueTrack(song, "Admin");
      flash(`✓ "${song.title}" añadida a la cola`);
    } catch (e: unknown) { flash(e instanceof Error ? e.message : "Error", true); }
  };

  const handlePlayNow = async (song: Song) => {
    try {
      await api.playNow(song, "Admin");
      flash(`Reproduciendo "${song.title}"`);
    } catch (e: unknown) { flash(e instanceof Error ? e.message : "Error", true); }
  };

  // ── Library create / import handlers ─────────────────────────────────────
  const handleLibCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!libCreateName.trim()) return;
    setLibCreating(true);
    try {
      const p = await api.createPlaylist(libCreateName.trim());
      setLibCreateName(""); setShowLibCreate(false);
      onPlaylistsChanged();
      await loadHome();
      onSelectPlaylist(p.id);
    } catch { flash("Error al crear lista", true); }
    finally { setLibCreating(false); }
  };

  const handleLibImport = async (e: React.FormEvent) => {
    e.preventDefault();
    const url = libImportUrl.trim();
    if (!url) return;
    setLibImporting(true);
    setLibImportMsg(null);
    try {
      const name = libImportName.trim() || "Lista importada";
      const p    = await api.createPlaylist(name);
      const r    = await api.importPlaylistSongs(p.id, url);
      setLibImportMsg({ text: `✓ ${r.added} canciones importadas`, err: false });
      setLibImportUrl(""); setLibImportName("");
      onPlaylistsChanged();
      await loadHome();
      onSelectPlaylist(p.id);
      setTimeout(() => { setShowLibImport(false); setLibImportMsg(null); }, 1800);
    } catch (err: unknown) {
      setLibImportMsg({ text: err instanceof Error ? err.message : "Error al importar", err: true });
    } finally {
      setLibImporting(false);
    }
  };

  // ── Saved playlist detail actions ─────────────────────────────────────────
  const handlePlay = async () => {
    if (!selectedPlaylistId) return;
    setActivating(true);
    try {
      const r = await api.activatePlaylist(selectedPlaylistId, shufflePlaylist);
      flash(r.message);
      onPlaylistsChanged();
      loadDetail(selectedPlaylistId);
    } catch (e: unknown) { flash(e instanceof Error ? e.message : "Error al reproducir", true); }
    finally { setActivating(false); }
  };

  const handlePlaySong = async (spotifyUri: string) => {
    if (!selectedPlaylistId) return;
    try {
      const r = await api.playSongFromPlaylist(selectedPlaylistId, spotifyUri, shufflePlaylist);
      flash(r.message);
      onPlaylistsChanged();
    } catch (e: unknown) { flash(e instanceof Error ? e.message : "Error al reproducir", true); }
  };

  const handleDeactivate = async () => {
    try {
      await api.deactivatePlaylist();
      flash("Lista de reproducción detenida");
      onPlaylistsChanged();
      loadDetail(selectedPlaylistId!);
    } catch { flash("Error al detener", true); }
  };

  const handleDeletePlaylist = async () => {
    if (!selectedPlaylistId || !playlist) return;
    const ok = await confirm({ title: `¿Eliminar "${playlist.name}"?`, message: "Esta acción no se puede deshacer.", confirmText: "Eliminar", danger: true });
    if (!ok) return;
    try {
      await api.deletePlaylist(selectedPlaylistId);
      onPlaylistsChanged();
      onClearSelection();
    } catch { flash("Error al eliminar", true); }
  };

  const handleRemoveSong = async (uri: string) => {
    if (!selectedPlaylistId) return;
    try {
      await api.removePlaylistSong(selectedPlaylistId, uri);
      setSongs(s => s.filter(x => x.spotifyUri !== uri));
      setPlaylist(p => p ? { ...p, songCount: p.songCount - 1 } : p);
      onPlaylistsChanged();
    } catch { flash("Error al quitar canción", true); }
  };

  const handleReorderSong = async (uri: string, toIndex: number) => {
    if (!selectedPlaylistId) return;
    setSongs(prev => {
      const next = [...prev];
      const fromIndex = next.findIndex(s => s.spotifyUri === uri);
      if (fromIndex < 0) return prev;
      const [moved] = next.splice(fromIndex, 1);
      next.splice(toIndex, 0, moved);
      return next;
    });
    try {
      await api.reorderPlaylistSong(selectedPlaylistId, uri, toIndex);
    } catch {
      flash("Error al reordenar", true);
      const updated = await api.getPlaylistSongs(selectedPlaylistId).catch(() => null);
      if (updated) setSongs(updated);
    }
  };

  // ── Import ─────────────────────────────────────────────────────────────────
  const handleImport = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!importUrl.trim() || !selectedPlaylistId) return;
    setImporting(true);
    try {
      const r = await api.importPlaylistSongs(selectedPlaylistId, importUrl.trim());
      flash(`✓ ${r.added} canciones importadas de ${r.total}`);
      setImportUrl("");
      loadDetail(selectedPlaylistId);
      onPlaylistsChanged();
    } catch (e: unknown) { flash(e instanceof Error ? e.message : "Error al importar", true); }
    finally { setImporting(false); }
  };

  // ── Search to add in detail ────────────────────────────────────────────────
  const handleAddSearch = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!addQuery.trim()) return;
    setAddSearching(true); setAddResults([]);
    try { setAddResults(await api.search(addQuery.trim(), 10)); }
    catch { }
    finally { setAddSearching(false); }
  };

  const handleAddToPlaylist = async (song: Song) => {
    if (!selectedPlaylistId) return;
    try {
      await api.addPlaylistSong(selectedPlaylistId, song);
      flash(`✓ "${song.title}" agregada`);
      loadDetail(selectedPlaylistId);
      onPlaylistsChanged();
    } catch (e: unknown) { flash(e instanceof Error ? e.message : "Error", true); }
  };

  // ── Derived ────────────────────────────────────────────────────────────────
  const songResults     = results?.filter(r => !r.isPlaylist) ?? [];
  const playlistResults = results?.filter(r => r.isPlaylist)  ?? [];

  const view = selectedPlaylistId != null ? "playlist"
             : previewMeta != null        ? "search-playlist"
             :                              "home";

  const isOnHome = view === "home";

  // ── Render ─────────────────────────────────────────────────────────────────
  return (
    <div className="main-browser">
      {confirmModal}

      {/* ── Tab bar ─────────────────────────────────────────── */}
      <div className="browser-tab-bar">
        {BROWSER_TABS.map(t => (
          <button
            key={t.id}
            className={`browser-tab${browserTab === t.id ? " active" : ""}`}
            onClick={() => {
              setBrowserTab(t.id);
              if (t.id === "home") { /* stay in current home sub-view */ }
            }}
          >
            {t.icon} {t.label}
          </button>
        ))}
      </div>

      {/* ── Settings / Platforms / Overlays / Ticker panels ── */}
      {browserTab === "settings"  && <div className="browser-panel-content"><SettingsPanel settings={settings} /></div>}
      {browserTab === "platforms" && <div className="browser-panel-content"><PlatformConnections tiktokEvents={tiktokEvents} twitchEvents={twitchEvents} kickEvents={kickEvents} /></div>}
      {browserTab === "overlays"  && <div className="browser-panel-content"><OverlayLinks overlayToken={overlayToken} /></div>}
      {browserTab === "ticker"    && <div className="browser-panel-content"><TickerMessages messages={tickerMessages} /></div>}

      {/* ── Search bar (home tab only) ───────────────────────── */}
      {browserTab === "home" && <div className="browser-search-bar">
        {!isOnHome && (
          <button className="browser-home-btn" onClick={goHome} title="Inicio">
            <Home size={16} />
          </button>
        )}
        <div className="browser-search-wrap" ref={searchWrapRef}>
          <form className="browser-search-form" onSubmit={e => e.preventDefault()}>
            {searching
              ? <span className="browser-search-spinner" />
              : <Search size={15} className="browser-search-icon" />}
            <input
              className="browser-search-input"
              placeholder="Buscar canciones, artistas, listas…"
              value={query}
              onChange={e => setQuery(e.target.value)}
              onFocus={() => { if (results != null && results.length > 0) setDropdownOpen(true); }}
              autoComplete="off"
            />
            {query && (
              <button type="button" className="browser-search-clear" onClick={clearSearch}>
                <X size={14} />
              </button>
            )}
          </form>

          {/* ── Dropdown results ─────────────────────────── */}
          {dropdownOpen && (results != null || searching) && (() => {
            const showSongs     = searchFilter !== "playlists";
            const showPlaylists = searchFilter !== "songs";
            const visibleSongs  = showSongs     ? songResults     : [];
            const visiblePls    = showPlaylists ? playlistResults : [];
            const noResults     = results != null && visibleSongs.length === 0 && visiblePls.length === 0;

            return (
              <div className="browser-search-dropdown">
                {/* Filter chips — only show when we have both types */}
                {results != null && songResults.length > 0 && playlistResults.length > 0 && (
                  <div className="browser-dd-filters">
                    {(["all", "songs", "playlists"] as const).map(f => (
                      <button
                        key={f}
                        className={`browser-dd-filter-chip${searchFilter === f ? " active" : ""}`}
                        onClick={e => { e.stopPropagation(); setSearchFilter(f); }}
                      >
                        {f === "all" ? "Todo" : f === "songs" ? "Canciones" : "Listas"}
                        {f === "songs"     && <span className="browser-dd-chip-count">{songResults.length}</span>}
                        {f === "playlists" && <span className="browser-dd-chip-count">{playlistResults.length}</span>}
                      </button>
                    ))}
                  </div>
                )}

                {searching && results == null && (
                  <div className="browser-dd-empty">Buscando…</div>
                )}

                {noResults && !searching && (
                  <div className="browser-dd-empty">Sin resultados para «{query}»</div>
                )}

                {/* Songs */}
                {visibleSongs.length > 0 && (
                  <>
                    {showPlaylists && <div className="browser-dd-section-label">Canciones</div>}
                    {visibleSongs.map(song => (
                      <div key={song.spotifyUri} className="browser-dd-row" style={{ position: "relative" }}>
                        {song.coverUrl
                          ? <img src={song.coverUrl} alt="" className="browser-dd-cover" />
                          : <div className="browser-dd-cover browser-dd-cover-ph"><Music size={16} /></div>}
                        <div className="browser-dd-info">
                          <span className="browser-dd-title">{song.title}</span>
                          <span className="browser-dd-meta">
                            <span className="browser-dd-type">Canción</span>
                            {" · "}{song.artist}
                            {song.durationMs > 0 && ` · ${formatDuration(song.durationMs)}`}
                          </span>
                        </div>
                        <div className="browser-dd-actions">
                          <button
                            className="browser-dd-btn"
                            title="Más opciones"
                            onClick={e => { e.stopPropagation(); setMenuUri(v => v === song.spotifyUri ? null : song.spotifyUri); }}
                          ><MoreHorizontal size={14} /></button>
                          <button
                            className="browser-dd-play-btn"
                            title="Reproducir ahora"
                            onClick={() => { handlePlayNow(song); setDropdownOpen(false); }}
                          ><Play size={13} fill="currentColor" /></button>
                          <button
                            className="browser-dd-add-btn"
                            title="Añadir a cola"
                            onClick={() => handleEnqueue(song)}
                          ><Plus size={14} /></button>
                        </div>
                        {menuUri === song.spotifyUri && (
                          <ContextMenu
                            song={song}
                            isQueue={false}
                            onClose={() => setMenuUri(null)}
                          />
                        )}
                      </div>
                    ))}
                  </>
                )}

                {/* Playlists */}
                {visiblePls.length > 0 && (
                  <>
                    {showSongs && <div className="browser-dd-section-label">Listas de YouTube</div>}
                    {visiblePls.map(pl => (
                      <div
                        key={pl.spotifyUri}
                        className="browser-dd-row browser-dd-row-playlist"
                        onClick={() => handlePreviewPlaylist(pl)}
                        style={{ cursor: "pointer" }}
                      >
                        {pl.coverUrl
                          ? <img src={pl.coverUrl} alt="" className="browser-dd-cover" style={{ borderRadius: 4 }} />
                          : <div className="browser-dd-cover browser-dd-cover-ph"><ListMusic size={16} /></div>}
                        <div className="browser-dd-info">
                          <span className="browser-dd-title">{pl.title}</span>
                          <span className="browser-dd-meta">
                            <span className="browser-dd-type">Lista</span>
                            {" · "}{pl.artist}
                            {pl.playlistVideoCount ? ` · ${pl.playlistVideoCount} videos` : ""}
                          </span>
                        </div>
                        <div className="browser-dd-actions">
                          <button
                            className="browser-dd-add-btn"
                            title="Ver lista"
                            onClick={e => { e.stopPropagation(); handlePreviewPlaylist(pl); }}
                          ><Play size={13} fill="currentColor" /></button>
                        </div>
                      </div>
                    ))}
                  </>
                )}
              </div>
            );
          })()}
        </div>
      </div>}

      {/* ── Action flash ────────────────────────────────────── */}
      {browserTab === "home" && actionMsg && (
        <div className={`browser-flash${actionMsg.err ? " err" : ""}`}>{actionMsg.text}</div>
      )}

      {/* ══ HOME / LIBRARY VIEW ══ */}
      {browserTab === "home" && view === "home" && (
        <div className="browser-content">
          {/* Library toolbar */}
          <div className="browser-lib-toolbar">
            <button
              className={`lib-sidebar-create-btn${showLibImport ? " active" : ""}`}
              title="Importar lista de YouTube"
              onClick={() => { setShowLibImport(v => !v); setShowLibCreate(false); setLibImportMsg(null); }}
            >
              <Download size={15} />
            </button>
            <button
              className={`lib-sidebar-create-btn${showLibCreate ? " active" : ""}`}
              title="Crear lista"
              onClick={() => { setShowLibCreate(v => !v); setShowLibImport(false); }}
            >
              <Plus size={16} />
            </button>
          </div>

          {showLibCreate && (
            <form className="lib-sidebar-create-form" onSubmit={handleLibCreate}>
              <input
                className="input input-sm"
                placeholder="Nombre de la lista…"
                value={libCreateName}
                onChange={e => setLibCreateName(e.target.value)}
                autoFocus
                disabled={libCreating}
              />
              <button type="submit" className="btn btn-primary btn-sm" disabled={libCreating || !libCreateName.trim()}>
                Crear
              </button>
            </form>
          )}

          {showLibImport && (
            <form className="lib-sidebar-create-form lib-sidebar-import-form" onSubmit={handleLibImport}>
              <input
                className="input input-sm"
                placeholder="URL de YouTube o YouTube Music…"
                value={libImportUrl}
                onChange={e => setLibImportUrl(e.target.value)}
                autoFocus
                disabled={libImporting}
              />
              <input
                className="input input-sm"
                placeholder="Nombre (opcional)"
                value={libImportName}
                onChange={e => setLibImportName(e.target.value)}
                disabled={libImporting}
              />
              {libImportMsg && (
                <span className={`lib-import-msg${libImportMsg.err ? " err" : ""}`}>{libImportMsg.text}</span>
              )}
              <button type="submit" className="btn btn-primary btn-sm" disabled={libImporting || !libImportUrl.trim()}>
                {libImporting ? "Importando…" : "Importar"}
              </button>
            </form>
          )}

          {!loadingHome && playlists.length > 0 && (
            <section className="browser-section">
              <div className="browser-playlist-grid">
                {playlists.map(p => (
                  <div
                    key={p.id}
                    className={`browser-playlist-card${p.isActive ? " active" : ""}`}
                    onClick={() => onSelectPlaylist(p.id)}
                    role="button"
                    tabIndex={0}
                    onKeyDown={e => e.key === "Enter" && onSelectPlaylist(p.id)}
                  >
                    <div className="browser-playlist-card-cover">
                      <PlaylistCover coverUrls={p.coverUrls} iconSize={28} className="browser-pl-card-cover-inner" />
                      <button
                        className="browser-playlist-play-btn"
                        title={p.isActive ? "Ya en reproducción" : "Reproducir lista"}
                        onClick={async e => {
                          e.stopPropagation();
                          try {
                            await api.activatePlaylist(p.id);
                            flash(`▶ Reproduciendo "${p.name}"`);
                            onPlaylistsChanged();
                            loadHome();
                          } catch (err: unknown) {
                            flash(err instanceof Error ? err.message : "Error", true);
                          }
                        }}
                      >
                        <Play size={20} fill="currentColor" />
                      </button>
                    </div>
                    <div className="browser-playlist-card-info">
                      <span className="browser-playlist-card-name">{p.name}</span>
                      <span className="browser-playlist-card-meta">
                        {p.isActive && <span className="lib-active-badge">Activa · </span>}
                        {p.songCount} canciones
                      </span>
                    </div>
                  </div>
                ))}
              </div>
            </section>
          )}

          {!loadingHome && playlists.length === 0 && (
            <div className="browser-empty">
              <div className="browser-empty-icon"><Music size={48} /></div>
              <div className="browser-empty-title">Tu librería está vacía</div>
              <div className="browser-empty-sub">Crea una lista con + o importa una desde YouTube.</div>
            </div>
          )}
        </div>
      )}

      {/* ══ UNSAVED PLAYLIST PREVIEW ══ */}
      {browserTab === "home" && view === "search-playlist" && previewMeta && (
        <div className="browser-content">
          <button className="browser-back-btn" onClick={() => { setPreviewMeta(null); setPreviewTracks(null); setDropdownOpen(true); }}>
            <ArrowLeft size={15} /> Resultados de búsqueda
          </button>

          {/* Header */}
          <div className="browser-pl-header">
            <div className="browser-pl-cover-big">
              {previewMeta.coverUrl
                ? <img src={previewMeta.coverUrl} alt="" style={{ width: "100%", height: "100%", objectFit: "cover", borderRadius: 8 }} />
                : <div style={{ display: "flex", alignItems: "center", justifyContent: "center", height: "100%" }}><ListMusic size={40} /></div>
              }
            </div>
            <div className="browser-pl-header-info">
              <span className="browser-pl-label">Lista de YouTube</span>
              <h1 className="browser-pl-name">{previewMeta.title}</h1>
              <span className="browser-pl-meta">
                {previewMeta.artist}
                {previewMeta.playlistVideoCount ? ` · ${previewMeta.playlistVideoCount} videos` : ""}
                {previewTracks ? ` · ${previewTracks.length} cargadas` : ""}
              </span>
            </div>
            <div className="browser-pl-header-actions">
              <button
                className="pl-action-btn pl-action-play"
                onClick={handleEnqueueAllPreview}
                disabled={!previewTracks || previewLoading}
                title="Añadir toda la lista a la cola"
              >
                <Play size={22} fill="currentColor" />
              </button>
              <button
                className="pl-action-btn pl-action-shuffle"
                onClick={() => setShowSaveForm(v => !v)}
                title="Guardar en librería"
              >
                <Download size={16} />
              </button>
            </div>
          </div>

          {/* Save to library form */}
          {showSaveForm && (
            <div className="browser-save-form">
              <span className="browser-save-label">Nombre de la lista:</span>
              <input
                className="input"
                value={saveNameInput}
                onChange={e => setSaveNameInput(e.target.value)}
                placeholder="Nombre…"
                autoFocus
              />
              <button
                className="btn btn-primary"
                onClick={handleSaveToLibrary}
                disabled={savingLibrary || !saveNameInput.trim()}
              >
                {savingLibrary ? "Guardando…" : "Guardar"}
              </button>
              <button className="btn btn-outline" onClick={() => setShowSaveForm(false)}>Cancelar</button>
            </div>
          )}

          {/* Tracks */}
          {previewLoading && <div className="lib-empty">Cargando canciones…</div>}

          {!previewLoading && previewTracks && (
            <div className="browser-pl-songs">
              {previewTracks.length === 0
                ? <div className="lib-empty">La lista no tiene canciones disponibles.</div>
                : previewTracks.map((s, i) => {
                  const isPlaying = nowPlayingUri === s.spotifyUri;
                  return (
                    <div
                      key={s.spotifyUri + i}
                      className={`browser-result-row${isPlaying ? " playing" : ""}`}
                      style={{ position: "relative" }}
                    >
                      <span className="browser-song-num">
                        {isPlaying ? <Play size={13} fill="currentColor" /> : i + 1}
                      </span>
                      {s.coverUrl
                        ? <img src={s.coverUrl} alt="" className="browser-result-cover" />
                        : <div className="browser-result-cover browser-result-cover-ph"><Music size={18} /></div>}
                      <div className="browser-result-info">
                        <span className={`browser-result-title${isPlaying ? " accent" : ""}`}>{s.title}</span>
                        <span className="browser-result-artist">{s.artist} · {formatDuration(s.durationMs)}</span>
                      </div>
                      <div className="browser-result-actions">
                        <button className="pl-row-btn pl-row-btn-play" onClick={() => handlePlayNow(s)} title="Reproducir ahora">
                          <Play size={15} fill="currentColor" />
                        </button>
                        <button className="pl-row-btn" onClick={() => handleEnqueue(s)} title="Añadir a cola">
                          <Plus size={15} />
                        </button>
                        <button
                          className="pl-row-btn"
                          title="Más opciones"
                          onClick={e => { e.stopPropagation(); setMenuUri(v => v === s.spotifyUri + i ? null : s.spotifyUri + i); }}
                        >
                          <MoreHorizontal size={15} />
                        </button>
                      </div>
                      {menuUri === s.spotifyUri + i && (
                        <ContextMenu
                          song={s}
                          isQueue={false}
                          onClose={() => setMenuUri(null)}
                        />
                      )}
                    </div>
                  );
                })
              }
            </div>
          )}
        </div>
      )}

      {/* ══ SAVED PLAYLIST DETAIL VIEW ══ */}
      {browserTab === "home" && view === "playlist" && (
        <div className="browser-content">
          <button className="browser-back-btn" onClick={onClearSelection}>
            <ArrowLeft size={15} /> Volver
          </button>

          {loadingDetail ? (
            <div className="lib-empty">Cargando…</div>
          ) : playlist ? (
            <>
              {/* Playlist header */}
              <div className="browser-pl-header">
                <div className="browser-pl-cover-big">
                  <PlaylistCover
                    coverUrls={songs.map(s => s.coverUrl).filter(Boolean) as string[]}
                    iconSize={40}
                    className="browser-pl-header-cover-inner"
                  />
                </div>
                <div className="browser-pl-header-info">
                  <span className="browser-pl-label">Lista de reproducción</span>
                  <h1 className="browser-pl-name">{playlist.name}</h1>
                  <span className="browser-pl-meta">{playlist.songCount} canciones</span>
                </div>
                <div className="browser-pl-header-actions">
                  {playlist.isActive ? (
                    <button className="pl-action-btn pl-action-stop" onClick={handleDeactivate} title="Detener lista">
                      <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="6" width="12" height="12" rx="1"/></svg>
                    </button>
                  ) : (
                    <button
                      className="pl-action-btn pl-action-play"
                      onClick={handlePlay}
                      disabled={activating || playlist.songCount === 0}
                      title="Reproducir lista"
                    >
                      <Play size={22} fill="currentColor" />
                    </button>
                  )}
                  <button
                    className={`pl-action-btn pl-action-shuffle${shufflePlaylist ? " active" : ""}`}
                    onClick={() => setShufflePlaylist(v => !v)}
                    title={shufflePlaylist ? "Aleatorio activado" : "Reproducción aleatoria"}
                  >
                    <Shuffle size={16} />
                  </button>
                  {!playlist.isSystem && (
                    <button className="pl-action-btn pl-action-danger" onClick={handleDeletePlaylist} title="Eliminar lista">
                      <Trash2 size={16} />
                    </button>
                  )}
                </div>
              </div>

              {/* Import + Add search */}
              <div className="browser-pl-tools">
                <form className="form-row" onSubmit={handleImport} style={{ flex: 1 }}>
                  <input
                    className="input"
                    placeholder="Importar lista de YouTube (URL)…"
                    value={importUrl}
                    onChange={e => setImportUrl(e.target.value)}
                    disabled={importing}
                    autoComplete="off"
                  />
                  <button type="submit" className="btn btn-primary" style={{ whiteSpace: "nowrap" }} disabled={importing || !importUrl.trim()}>
                    {importing ? "Importando…" : "Importar"}
                  </button>
                </form>

                <form className="form-row" onSubmit={handleAddSearch} style={{ flex: 1 }}>
                  <input
                    className="input"
                    placeholder="Buscar y agregar canción…"
                    value={addQuery}
                    onChange={e => setAddQuery(e.target.value)}
                    autoComplete="off"
                  />
                  <button type="submit" className="btn btn-outline" style={{ whiteSpace: "nowrap" }} disabled={addSearching || !addQuery.trim()}>
                    {addSearching ? "…" : "Buscar"}
                  </button>
                </form>
              </div>

              {/* Add search results */}
              {addResults.length > 0 && (
                <div className="browser-add-results">
                  {addResults.filter(s => !s.isPlaylist).map(song => (
                    <div key={song.spotifyUri} className="browser-result-row">
                      {song.coverUrl
                        ? <img src={song.coverUrl} alt="" className="browser-result-cover" />
                        : <div className="browser-result-cover browser-result-cover-ph"><Music size={18} /></div>}
                      <div className="browser-result-info">
                        <span className="browser-result-title">{song.title}</span>
                        <span className="browser-result-artist">{song.artist} · {formatDuration(song.durationMs)}</span>
                      </div>
                      <button className="pl-row-btn pl-row-btn-play" onClick={() => handleAddToPlaylist(song)} title="Agregar a lista">
                        <Plus size={15} />
                      </button>
                    </div>
                  ))}
                </div>
              )}

              {/* Song list */}
              <div className="browser-pl-songs">
                {songs.length === 0 ? (
                  <div className="lib-empty">Lista vacía. Importa o busca canciones arriba.</div>
                ) : songs.map((s, i) => {
                  const isPlaying    = nowPlayingUri === s.spotifyUri;
                  const isLiked      = likedUris?.has(s.spotifyUri) ?? false;
                  const isDragging   = dragSongUri === s.spotifyUri;
                  const isDropTarget = dropSongIdx === i && dragSongUri !== s.spotifyUri;
                  const plSong: SongRef = { spotifyUri: s.spotifyUri, title: s.title, artist: s.artist, coverUrl: s.coverUrl, durationMs: s.durationMs };
                  return (
                    <div
                      key={s.id}
                      className={`browser-result-row browser-pl-song-row${isPlaying ? " playing" : ""}${isDragging ? " queue-row-dragging" : ""}${isDropTarget ? " queue-row-drop-target" : ""}`}
                      style={{ position: "relative" }}
                      draggable={!playlist?.isSystem}
                      onDragStart={() => { setDragSongUri(s.spotifyUri); dragSongIdxRef.current = i; }}
                      onDragOver={e => { e.preventDefault(); e.dataTransfer.dropEffect = "move"; setDropSongIdx(i); }}
                      onDrop={e => { e.preventDefault(); if (dragSongUri && dragSongIdxRef.current !== i) handleReorderSong(dragSongUri, i); setDragSongUri(null); setDropSongIdx(null); }}
                      onDragEnd={() => { setDragSongUri(null); setDropSongIdx(null); }}
                    >
                      {/* Position / play button (CSS hover swap) */}
                      <button
                        className="browser-song-num-btn"
                        onClick={() => handlePlaySong(s.spotifyUri)}
                        title="Reproducir desde aquí"
                      >
                        <span className="browser-song-num-text">
                          {isPlaying ? <Play size={13} fill="currentColor" /> : i + 1}
                        </span>
                        <Play size={13} fill="currentColor" className="browser-song-num-play" />
                      </button>

                      {s.coverUrl
                        ? <img src={s.coverUrl} alt="" className="browser-result-cover" />
                        : <div className="browser-result-cover browser-result-cover-ph"><Music size={18} /></div>}
                      <div className="browser-result-info">
                        <span className={`browser-result-title${isPlaying ? " accent" : ""}`}>{s.title}</span>
                        <span className="browser-result-artist">{s.artist} · {formatDuration(s.durationMs)}</span>
                      </div>
                      <div className="browser-result-actions">
                        {/* Like button — always show if liked, else show on hover via CSS */}
                        <button
                          className={`pl-row-btn pl-row-like-btn${isLiked ? " liked" : ""}`}
                          title={isLiked ? "Guardado en Liked Songs" : "Guardar en Liked Songs"}
                          onClick={e => {
                            e.stopPropagation();
                            if (isLiked) {
                              setLikeMenuUri(v => v === s.spotifyUri ? null : s.spotifyUri);
                              setMenuUri(null);
                            } else {
                              onToggleLike?.(plSong);
                            }
                          }}
                        >
                          <Heart size={14} fill={isLiked ? "currentColor" : "none"} />
                        </button>
                        {/* Ellipsis menu — always visible on hover */}
                        <button
                          className="pl-row-btn"
                          title="Más opciones"
                          onClick={e => { e.stopPropagation(); setMenuUri(v => v === s.spotifyUri ? null : s.spotifyUri); setLikeMenuUri(null); }}
                        >
                          <MoreHorizontal size={15} />
                        </button>
                      </div>

                      {/* Ellipsis context menu */}
                      {menuUri === s.spotifyUri && (
                        <ContextMenu
                          song={plSong}
                          isQueue={false}
                          onClose={() => setMenuUri(null)}
                          onRemove={!playlist?.isSystem ? () => handleRemoveSong(s.spotifyUri) : undefined}
                        />
                      )}
                      {/* Like menu (opens membership view) */}
                      {likeMenuUri === s.spotifyUri && (
                        <ContextMenu
                          song={plSong}
                          isQueue={false}
                          onClose={() => setLikeMenuUri(null)}
                          defaultView="playlist"
                        />
                      )}
                    </div>
                  );
                })}
              </div>
            </>
          ) : (
            <div className="lib-empty">Lista no encontrada</div>
          )}
        </div>
      )}
    </div>
  );
};
