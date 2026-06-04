import React, { useState } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../services/api";

export const ImportPlaylist: React.FC = () => {
  const { t } = useTranslation();
  const [url,         setUrl]         = useState("");
  const [requestedBy, setRequestedBy] = useState("Admin");
  const [loading,     setLoading]     = useState(false);
  const [message,     setMessage]     = useState<{ text: string; ok: boolean } | null>(null);

  const handleImport = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!url.trim()) return;
    setLoading(true);
    setMessage(null);
    try {
      const res = await api.importPlaylist(url.trim(), requestedBy);
      setMessage({ text: `✓ ${t("importPlaylist.success", { added: res.added, skipped: res.skipped, total: res.total })}`, ok: true });
      if (res.added > 0) setUrl("");
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : t("importPlaylist.errImport");
      setMessage({ text: msg, ok: false });
    } finally {
      setLoading(false);
    }
  };

  return (
    <form className="import-playlist-form" onSubmit={handleImport}>
      <p className="import-playlist-hint">
        {t("importPlaylist.hint")}
      </p>
      <div className="form-row">
        <input
          type="text"
          className="input"
          placeholder={t("importPlaylist.urlPlaceholder")}
          value={url}
          onChange={e => setUrl(e.target.value)}
          disabled={loading}
        />
        <input
          type="text"
          className="input input-sm"
          placeholder={t("importPlaylist.requestedBy")}
          value={requestedBy}
          onChange={e => setRequestedBy(e.target.value)}
        />
        <button
          type="submit"
          className="btn btn-primary"
          style={{ width: "100%" }}
          disabled={loading || !url.trim()}
        >
          {loading ? t("importPlaylist.importing") : t("importPlaylist.importBtn")}
        </button>
      </div>
      {message && (
        <div className={`form-message${message.ok ? "" : " form-message-error"}`}>
          {message.text}
        </div>
      )}
    </form>
  );
};
