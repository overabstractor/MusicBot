import React, { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
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
  const { t } = useTranslation();
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
            placeholder={t("history.searchPlaceholder")}
            value={search}
            onChange={e => setSearch(e.target.value)}
          />
          {search && (
            <button className="history-search-clear" onClick={() => setSearch("")}>✕</button>
          )}
        </div>
        <div className="history-toolbar-right">
          <span className="history-count">
            {filtered.length}{search ? ` / ${history.length}` : ""} {t("history.songsLabel")}
          </span>
          {!confirm ? (
            <button className="btn btn-sm btn-danger-outline" onClick={() => setConfirm(true)} disabled={history.length === 0}>
              {t("history.clearHistory")}
            </button>
          ) : (
            <div className="history-confirm">
              <span>{t("history.confirmClear", { count: history.length })}</span>
              <button className="btn btn-sm btn-danger" onClick={handleClear}>{t("common.yes")}</button>
              <button className="btn btn-sm btn-secondary" onClick={() => setConfirm(false)}>{t("common.no")}</button>
            </div>
          )}
        </div>
      </div>

      {loading ? (
        <div className="history-empty">{t("history.loadingHistory")}</div>
      ) : filtered.length === 0 ? (
        <div className="history-empty">{history.length === 0 ? t("history.emptyHistory") : t("history.noResults")}</div>
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
                  <button className="btn btn-sm btn-primary" onClick={() => handlePlay(item)} title={t("history.playNowTitle")}>▶ {t("history.playNow")}</button>
                  <button className="btn btn-sm btn-outline" onClick={() => handleEnqueue(item)} title={t("history.addToQueueTitle")}>+ {t("history.queueShort")}</button>
                  {onAddToAutoQueue && (
                    <button
                      className="btn btn-sm btn-autoqueue"
                      onClick={() => onAddToAutoQueue({ spotifyUri: item.trackId, title: item.title, artist: item.artist, coverUrl: item.coverUrl ?? undefined, durationMs: item.durationMs })}
                      title={t("history.addToAutoQueueTitle")}
                    >+ {t("history.autoQueueShort")}</button>
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
