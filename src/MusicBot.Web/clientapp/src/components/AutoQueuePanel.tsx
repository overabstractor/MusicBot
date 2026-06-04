import React, { useState, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
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
  const { t } = useTranslation();
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
      if (hits.length === 0) setSearchMsg(t("common.noResults"));
    } catch {
      setSearchMsg(t("autoqueue.searchError"));
    } finally {
      setSearching(false);
    }
  };

  const handleAddCurrentSong = async () => {
    if (!currentSong) return;
    try {
      await api.addAutoQueueSong(currentSong);
      setAddMsg(t("autoqueue.added", { title: currentSong.title }));
      load();
    } catch (e: unknown) {
      setAddMsg(e instanceof Error ? e.message : t("autoqueue.addError"));
    }
    setTimeout(() => setAddMsg(""), 3000);
  };

  const handleAdd = async (song: Song) => {
    try {
      await api.addAutoQueueSong(song);
      setAddMsg(t("autoqueue.added", { title: song.title }));
      load();
    } catch (e: unknown) {
      setAddMsg(e instanceof Error ? e.message : t("autoqueue.addError"));
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
      setImportMsg(t("autoqueue.imported", { added: res.added, total: res.total }));
      setPlaylistUrl("");
      load();
    } catch (ex: unknown) {
      setImportMsg(ex instanceof Error ? ex.message : t("autoqueue.importError"));
    } finally {
      setImporting(false);
    }
  };

  return (
    <div className="autoqueue-panel">
      <div className="autoqueue-header">
        <span className="autoqueue-count">{t("autoqueue.poolCount", { count: songs.length })}</span>
        {songs.length > 0 && (
          confirm ? (
            <span className="autoqueue-confirm">
              {t("autoqueue.clearConfirm")}{" "}
              <button className="btn btn-sm btn-danger" onClick={handleClear}>{t("common.yes")}</button>{" "}
              <button className="btn btn-sm btn-secondary" onClick={() => setConfirm(false)}>{t("common.no")}</button>
            </span>
          ) : (
            <button className="btn btn-sm btn-danger-outline" onClick={() => setConfirm(true)}>{t("autoqueue.clear")}</button>
          )
        )}
      </div>

      {/* Current song shortcut */}
      {currentSong && (
        <div className="autoqueue-add-section">
          <div className="queue-section-label">{t("autoqueue.currentSong")}</div>
          <div className="autoqueue-current-row">
            {currentSong.coverUrl && (
              <img src={currentSong.coverUrl} alt="" className="autoqueue-cover" />
            )}
            <div className="autoqueue-info">
              <span className="autoqueue-title">{currentSong.title}</span>
              <span className="autoqueue-artist">{currentSong.artist} · {formatDuration(currentSong.durationMs)}</span>
            </div>
            <button className="btn btn-sm btn-autoqueue" onClick={handleAddCurrentSong}>{t("autoqueue.addToAuto")}</button>
          </div>
          {addMsg && <div className="form-message" style={{ marginTop: 6 }}>{addMsg}</div>}
        </div>
      )}

      {/* Search to add */}
      <div className="autoqueue-add-section">
        <div className="queue-section-label">{t("autoqueue.searchLabel")}</div>
        <form className="form-row" onSubmit={handleSearch}>
          <input
            type="text"
            className="input"
            placeholder={t("autoqueue.searchPlaceholder")}
            value={query}
            onChange={e => { setQuery(e.target.value); setSearchMsg(""); }}
            autoComplete="off"
          />
          <button type="submit" className="btn btn-primary" style={{ whiteSpace: "nowrap" }} disabled={searching || !query.trim()}>
            {searching ? t("autoqueue.searching") : t("common.search")}
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
        <div className="queue-section-label">{t("autoqueue.importLabel")}</div>
        <form className="form-row" onSubmit={handleImport}>
          <input
            type="text"
            className="input"
            placeholder={t("autoqueue.importPlaceholder")}
            value={playlistUrl}
            onChange={e => setPlaylistUrl(e.target.value)}
            disabled={importing}
          />
          <button type="submit" className="btn btn-primary" style={{ whiteSpace: "nowrap" }} disabled={importing || !playlistUrl.trim()}>
            {importing ? t("autoqueue.importing") : t("autoqueue.import")}
          </button>
        </form>
        {importMsg && <div className="form-message" style={{ marginTop: 6 }}>{importMsg}</div>}
      </div>

      {/* Song list */}
      <div className="queue-section-label">{t("autoqueue.poolLabel")}</div>
      {loading ? (
        <div className="lib-empty">{t("common.loading")}</div>
      ) : songs.length === 0 ? (
        <div className="lib-empty">{t("autoqueue.empty")}</div>
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
                  title={t("autoqueue.enqueueTitle")}
                  onClick={() => api.enqueueTrack({ spotifyUri: song.spotifyUri, title: song.title, artist: song.artist, coverUrl: song.coverUrl, durationMs: song.durationMs }, "Admin").catch(() => {})}
                >
                  <ListPlus size={14} />
                </button>
                <button className="btn-icon-danger" onClick={() => handleRemove(song.spotifyUri)} title={t("common.delete")}>
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
