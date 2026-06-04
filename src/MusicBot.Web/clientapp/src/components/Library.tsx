import React, { useEffect, useState, useCallback } from "react";
import { useTranslation } from "react-i18next";
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
  const { t } = useTranslation();
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

  if (loading) return <div className="lib-empty">{t("library.loadingLibrary")}</div>;

  return (
    <div className="library-panel">
      {!saveDownloads && (
        <div className="lib-temp-banner">
          ⚠️ {t("library.tempBanner")}
        </div>
      )}
      <div className="lib-toolbar">
        <div className="lib-toolbar-left">
          <div className="lib-search-wrap">
            <span className="lib-search-icon">🔍</span>
            <input
              className="lib-search-input"
              type="text"
              placeholder={t("library.searchPlaceholder")}
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
            {search && (
              <button className="lib-search-clear" onClick={() => setSearch("")}>✕</button>
            )}
          </div>
          <div className="lib-stats">
            <span>{filtered.length}{search ? ` / ${tracks.length}` : ""} {t("library.songsLabel")}</span>
            <span>·</span>
            <span>{totalPlays} {t("library.playsLabel")}</span>
            <span>·</span>
            <span>{formatBytes(totalSize)}</span>
          </div>
        </div>
        <div className="lib-toolbar-right">
          <button className="btn btn-sm btn-outline lib-open-folder" onClick={handleOpenFolder} disabled={opening}>
            {opening ? t("library.opening") : `📁 ${t("library.openFolder")}`}
          </button>
          {!confirm ? (
            <button className="btn btn-sm btn-danger-outline" onClick={() => setConfirm(true)} disabled={tracks.length === 0}>
              {t("library.clearLibrary")}
            </button>
          ) : (
            <div className="lib-confirm">
              <span>{t("library.confirmClear", { count: tracks.length })}</span>
              <button className="btn btn-sm btn-danger" onClick={handleClear}>{t("library.yesDelete")}</button>
              <button className="btn btn-sm btn-secondary" onClick={() => setConfirm(false)}>{t("common.cancel")}</button>
            </div>
          )}
        </div>
      </div>

      {tracks.length === 0 ? (
        <div className="lib-empty">{t("library.noDownloads")}</div>
      ) : filtered.length === 0 ? (
        <div className="lib-empty">{t("library.noResultsFor", { query: search })}</div>
      ) : (
        <div className="lib-table-wrap">
          <table className="lib-table">
            <thead>
              <tr>
                <th className="lib-th lib-th-cover" />
                <SortBtn col="title"         label={t("library.colSong")} />
                <SortBtn col="downloadedAt"  label={t("library.colDownloaded")} />
                <SortBtn col="playCount"     label={t("library.colRequested")} />
                <SortBtn col="totalPlayedMs" label={t("library.colTotalTime")} />
                <SortBtn col="fileSizeBytes" label={t("library.colSize")} />
                <th className="lib-th" />
              </tr>
            </thead>
            <tbody>
              {sorted.map(tr => (
                <tr key={tr.id} className={`lib-row ${!tr.fileExists ? "lib-row-missing" : ""}`}>
                  <td className="lib-td lib-td-cover">
                    {tr.coverUrl
                      ? <img src={tr.coverUrl} alt="" className="lib-cover" />
                      : <div className="lib-cover lib-cover-placeholder">♫</div>}
                  </td>
                  <td className="lib-td lib-td-info">
                    <div className="lib-title">{tr.title}</div>
                    <div className="lib-artist">{tr.artist}</div>
                    <div className="lib-duration">{formatDuration(tr.durationMs)}</div>
                    {!tr.fileExists && <span className="lib-missing-badge">{t("library.fileMissing")}</span>}
                  </td>
                  <td className="lib-td lib-td-center">{formatDate(tr.downloadedAt)}</td>
                  <td className="lib-td lib-td-center lib-stat">{tr.playCount}</td>
                  <td className="lib-td lib-td-center">{tr.totalPlayedMs > 0 ? formatDuration(tr.totalPlayedMs) : "—"}</td>
                  <td className="lib-td lib-td-center">{formatBytes(tr.fileSizeBytes)}</td>
                  <td className="lib-td lib-td-action">
                    {libMsg?.id === tr.id ? (
                      <span className="lib-feedback">{libMsg.text}</span>
                    ) : (
                      <div className="lib-actions">
                        <button className="btn btn-sm btn-primary" title={t("library.playNow")} disabled={!tr.fileExists} onClick={() => handlePlay(tr)}>▶</button>
                        <button className="btn btn-sm btn-outline" title={t("library.addToQueue")} disabled={!tr.fileExists} onClick={() => handleEnqueue(tr)}>+ {t("library.queueShort")}</button>
                        <button className="btn btn-icon-danger" title={t("common.delete")} onClick={() => handleDelete(tr.id)}>🗑</button>
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
