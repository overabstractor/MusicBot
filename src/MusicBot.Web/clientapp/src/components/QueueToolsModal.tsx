import React, { useState } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../services/api";

interface Props {
  open: boolean;
  onClose: () => void;
  voteUser: string;
  onVoteUserChange: (v: string) => void;
  giftUser: string;
  onGiftUserChange: (v: string) => void;
  giftCoins: number;
  onGiftCoinsChange: (v: number) => void;
  onVote: (skip: boolean) => void;
  onGiftBump: () => void;
  simMsg: string | null;
}

export const QueueToolsModal: React.FC<Props> = ({
  open, onClose,
  voteUser, onVoteUserChange,
  giftUser, onGiftUserChange,
  giftCoins, onGiftCoinsChange,
  onVote, onGiftBump,
  simMsg,
}) => {
  const { t } = useTranslation();
  const [url,         setUrl]         = useState("");
  const [requestedBy, setRequestedBy] = useState("Admin");
  const [loading,     setLoading]     = useState(false);
  const [importMsg,   setImportMsg]   = useState<{ text: string; ok: boolean } | null>(null);

  const handleImport = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!url.trim()) return;
    setLoading(true);
    setImportMsg(null);
    try {
      const res = await api.importPlaylist(url.trim(), requestedBy);
      setImportMsg({ text: t("queueTools.imported", { added: res.added, skipped: res.skipped, total: res.total }), ok: true });
      if (res.added > 0) setUrl("");
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : t("queueTools.importError");
      setImportMsg({ text: msg, ok: false });
    } finally {
      setLoading(false);
    }
  };

  if (!open) return null;

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-panel" onClick={e => e.stopPropagation()}>

        <div className="modal-header">
          <span className="modal-title">{t("queueTools.title")}</span>
          <button className="modal-close-btn" onClick={onClose} aria-label={t("common.close")}>✕</button>
        </div>

        <div className="modal-body">

          {/* ── Import playlist ── */}
          <div className="modal-section-label">{t("queueTools.importLabel")}</div>
          <p className="import-playlist-hint">
            {t("queueTools.importHint")}
          </p>
          <form onSubmit={handleImport}>
            <div className="form-row">
              <input
                type="text"
                className="input"
                placeholder={t("queueTools.urlPlaceholder")}
                value={url}
                onChange={e => setUrl(e.target.value)}
                disabled={loading}
              />
            </div>
            <div className="form-row" style={{ marginTop: 8 }}>
              <input
                type="text"
                className="input input-sm"
                placeholder={t("queueTools.requestedBy")}
                value={requestedBy}
                onChange={e => setRequestedBy(e.target.value)}
              />
              <button
                type="submit"
                className="btn btn-primary"
                disabled={loading || !url.trim()}
              >
                {loading ? t("queueTools.importing") : t("queueTools.import")}
              </button>
            </div>
            {importMsg && (
              <div className={`form-message${importMsg.ok ? "" : " form-message-error"}`}>
                {importMsg.text}
              </div>
            )}
          </form>

          <div className="modal-divider" />

          {/* ── Simulation ── */}
          <div className="modal-section-label">{t("queueTools.simLabel")}</div>

          <div className="admin-tool-row">
            <span className="admin-tool-label">{t("queueTools.simVote")}</span>
            <input
              className="input admin-tool-input"
              value={voteUser}
              onChange={e => onVoteUserChange(e.target.value)}
              placeholder={t("queueTools.user")}
            />
            <button className="btn btn-sm btn-yes" onClick={() => onVote(true)}>!si</button>
            <button className="btn btn-sm btn-no"  onClick={() => onVote(false)}>!no</button>
          </div>

          <div className="admin-tool-row" style={{ marginTop: 8 }}>
            <span className="admin-tool-label">{t("queueTools.simGift")}</span>
            <input
              className="input admin-tool-input"
              value={giftUser}
              onChange={e => onGiftUserChange(e.target.value)}
              placeholder={t("queueTools.giftUserPlaceholder")}
            />
            <input
              className="input admin-tool-input-sm"
              type="number"
              min={1}
              value={giftCoins}
              onChange={e => onGiftCoinsChange(Number(e.target.value))}
            />
            <span className="admin-tool-label">{t("queueTools.coins")}</span>
            <button className="btn btn-sm btn-gift" onClick={onGiftBump}>{t("queueTools.simulate")}</button>
          </div>

          {simMsg && <div className="admin-tool-msg" style={{ marginTop: 8 }}>{simMsg}</div>}

        </div>
      </div>
    </div>
  );
};
