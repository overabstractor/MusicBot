import React, { useState } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { api } from "../services/api";
import { TickerMessage } from "../hooks/useSignalR";
import { useConfirm } from "../hooks/useConfirm";
import { FormModal } from "./FormModal";

const Label: React.FC<{ text: string; tooltip: string }> = ({ text, tooltip }) => (
  <div className="form-modal-label">
    {text}
    <span className="info-icon" data-tooltip={tooltip}>i</span>
  </div>
);

interface Props {
  messages: TickerMessage[];
}

const INTERVAL_OPTIONS = [1, 2, 5, 10, 15, 30, 60];
const ALL_PLATFORMS = ["tiktok", "twitch", "kick"] as const;
type Platform = typeof ALL_PLATFORMS[number];

const PLATFORM_LABELS: Record<Platform, string> = {
  tiktok: "TikTok",
  twitch: "Twitch",
  kick:   "Kick",
};

const EMPTY_FORM = { text: "", intervalMinutes: 5, minChatMessages: 0, platforms: [...ALL_PLATFORMS] as Platform[], enabled: true };

function platformsLabel(platforms: string[], t: TFunction) {
  if (platforms.length === 0) return t("ticker.noPlatforms");
  if (platforms.length === ALL_PLATFORMS.length) return t("ticker.allPlatforms");
  return platforms.map(p => PLATFORM_LABELS[p as Platform] ?? p).join(", ");
}

export const TickerMessages: React.FC<Props> = ({ messages }) => {
  const { t } = useTranslation();
  const [confirmModal, confirm] = useConfirm();
  const [form,       setForm]       = useState(EMPTY_FORM);
  const [editId,     setEditId]     = useState<string | null>(null);
  const [modalOpen,  setModalOpen]  = useState(false);
  const [saving,     setSaving]     = useState(false);
  const [error,      setError]      = useState<string | null>(null);

  const openNew = () => { setForm(EMPTY_FORM); setEditId(null); setError(null); setModalOpen(true); };

  const openEdit = (msg: TickerMessage) => {
    setEditId(msg.id);
    setForm({
      text:            msg.text,
      intervalMinutes: msg.intervalMinutes,
      minChatMessages: msg.minChatMessages ?? 0,
      platforms:       (msg.platforms ?? []) as Platform[],
      enabled:         msg.enabled,
    });
    setError(null);
    setModalOpen(true);
  };

  const closeModal = () => { setModalOpen(false); setError(null); };

  const togglePlatform = (p: Platform) =>
    setForm(f => ({
      ...f,
      platforms: f.platforms.includes(p)
        ? f.platforms.filter(x => x !== p)
        : [...f.platforms, p],
    }));

  const handleSave = async () => {
    if (!form.text.trim()) { setError(t("ticker.errTextRequired")); return; }
    if (form.platforms.length === 0) { setError(t("ticker.errPlatformRequired")); return; }
    setSaving(true);
    setError(null);
    try {
      const payload = {
        text:            form.text.trim(),
        intervalMinutes: form.intervalMinutes,
        minChatMessages: form.minChatMessages,
        platforms:       form.platforms,
        enabled:         form.enabled,
      };
      if (editId) {
        await api.updateTicker(editId, payload);
      } else {
        await api.addTicker(payload);
      }
      closeModal();
    } catch (e: any) {
      setError(e.message ?? t("ticker.saveError"));
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id: string) => {
    const ok = await confirm({ title: t("ticker.deleteTitle"), message: t("ticker.deleteMessage"), confirmText: t("common.delete"), danger: true });
    if (!ok) return;
    try { await api.deleteTicker(id); } catch {}
  };

  const handleToggle = async (msg: TickerMessage) => {
    try {
      await api.updateTicker(msg.id, {
        text:            msg.text,
        intervalMinutes: msg.intervalMinutes,
        minChatMessages: msg.minChatMessages,
        platforms:       msg.platforms,
        enabled:         !msg.enabled,
      });
    } catch {}
  };

  const sorted = [...messages].sort((a, b) => a.order - b.order);
  const activeCount = sorted.filter(m => m.enabled).length;

  return (
    <div className="ticker-panel">
      {confirmModal}

      {/* ── Stats bar ── */}
      <div className="ticker-stats-bar">
        <div className="ticker-stat">
          <span className="ticker-stat-num">{sorted.length}</span>
          <span className="ticker-stat-label">{t("ticker.statMessages")}</span>
        </div>
        <div className="ticker-stat-divider" />
        <div className="ticker-stat">
          <span className="ticker-stat-num ticker-stat-active">{activeCount}</span>
          <span className="ticker-stat-label">{t("ticker.statActive")}</span>
        </div>
        <div className="ticker-stat-divider" />
        <div className="ticker-stat">
          <span className="ticker-stat-num ticker-stat-inactive">{sorted.length - activeCount}</span>
          <span className="ticker-stat-label">{t("ticker.statInactive")}</span>
        </div>
        <button className="btn btn-primary btn-sm ticker-add-btn" onClick={openNew}>
          {t("ticker.newMessage")}
        </button>
      </div>

      {/* ── List ── */}
      {sorted.length === 0 ? (
        <div className="ticker-empty">
          <div className="ticker-empty-icon">💬</div>
          <div className="ticker-empty-title">{t("ticker.emptyTitle")}</div>
          <div className="ticker-empty-sub">{t("ticker.emptySub")}</div>
        </div>
      ) : (
        <div className="ticker-list">
          {sorted.map((msg, idx) => (
            <div key={msg.id} className={`ticker-card${msg.enabled ? " ticker-card-on" : " ticker-card-off"}`}>
              <div className="ticker-card-order">{idx + 1}</div>

              <div className="ticker-card-preview-area">
                <div className="ticker-card-content">
                  <div className="ticker-card-text">{msg.text || <em className="ticker-card-only-img">{t("ticker.noText")}</em>}</div>
                  <div className="ticker-card-meta">
                    <span className={`ticker-status-pill${msg.enabled ? " on" : " off"}`}>
                      {msg.enabled ? t("ticker.active") : t("ticker.inactive")}
                    </span>
                    <span className="ticker-card-dur">⏱ {msg.intervalMinutes === 60 ? t("ticker.hour1") : t("ticker.minutes", { count: msg.intervalMinutes })}</span>
                    {msg.minChatMessages > 0 && (
                      <span className="ticker-card-dur">💬 {t("ticker.minMsgs", { count: msg.minChatMessages })}</span>
                    )}
                    <span className="ticker-card-platforms">{platformsLabel(msg.platforms ?? [], t)}</span>
                  </div>
                </div>
              </div>

              <div className="ticker-card-actions">
                <button
                  className={`btn btn-sm ${msg.enabled ? "btn-outline" : "btn-primary"}`}
                  onClick={() => handleToggle(msg)}
                >
                  {msg.enabled ? t("ticker.pause") : t("ticker.activate")}
                </button>
                <button className="btn btn-sm btn-outline" onClick={() => openEdit(msg)}>{t("common.edit")}</button>
                <button className="btn btn-sm btn-danger" onClick={() => handleDelete(msg.id)}>✕</button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* ── Modal ── */}
      {modalOpen && (
        <FormModal
          title={editId ? t("ticker.editTitle") : t("ticker.newTitle")}
          onClose={closeModal}
          width={480}
        >
          <div className="ticker-modal-body">
            {/* Text */}
            <Label text={t("ticker.textLabel")} tooltip={t("ticker.textTip")} />
            <textarea
              className="input ticker-textarea"
              placeholder={t("ticker.textPlaceholder")}
              value={form.text}
              rows={3}
              autoFocus
              onChange={e => setForm(f => ({ ...f, text: e.target.value }))}
            />

            <div className="ticker-form-row2">
              {/* Interval */}
              <div className="ticker-field ticker-field-sm">
                <Label text={t("ticker.intervalLabel")} tooltip={t("ticker.intervalTip")} />
                <select
                  className="input settings-input-sm ticker-interval-select"
                  value={form.intervalMinutes}
                  onChange={e => setForm(f => ({ ...f, intervalMinutes: Number(e.target.value) }))}
                >
                  {INTERVAL_OPTIONS.map(m => (
                    <option key={m} value={m}>{m === 60 ? t("ticker.hour1") : t("ticker.minutes", { count: m })}</option>
                  ))}
                </select>
              </div>

              {/* Min chat messages */}
              <div className="ticker-field ticker-field-sm">
                <Label text={t("ticker.minMsgLabel")} tooltip={t("ticker.minMsgTip")} />
                <input
                  type="number"
                  className="input settings-input-sm"
                  min={0} max={9999}
                  value={form.minChatMessages}
                  onChange={e => setForm(f => ({ ...f, minChatMessages: Math.max(0, Number(e.target.value)) }))}
                />
                {form.minChatMessages === 0 && (
                  <span className="ticker-field-hint">{t("ticker.noMin")}</span>
                )}
              </div>

              {/* Enabled toggle */}
              <div className="ticker-field ticker-field-toggle">
                <Label text={t("ticker.statusLabel")} tooltip={t("ticker.statusTip")} />
                <div className="ticker-toggle-row" onClick={() => setForm(f => ({ ...f, enabled: !f.enabled }))}>
                  <div className={`settings-toggle${form.enabled ? " on" : ""}`}>
                    <div className="settings-toggle-thumb" />
                  </div>
                  <span className={`ticker-toggle-label${form.enabled ? " on" : ""}`}>
                    {form.enabled ? t("ticker.toggleOn") : t("ticker.toggleOff")}
                  </span>
                </div>
              </div>
            </div>

            {/* Platforms */}
            <Label text={t("ticker.platformsLabel")} tooltip={t("ticker.platformsTip")} />
            <div className="ticker-platforms-row">
              {ALL_PLATFORMS.map(p => {
                const selected = form.platforms.includes(p);
                return (
                  <button
                    key={p}
                    type="button"
                    className={`ticker-platform-chip ticker-platform-${p}${selected ? " selected" : ""}`}
                    onClick={() => togglePlatform(p)}
                  >
                    {PLATFORM_LABELS[p]}
                  </button>
                );
              })}
            </div>
            {form.platforms.length === 0 && (
              <div className="ticker-platforms-warning">{t("ticker.noPlatformWarn")}</div>
            )}

            {error && <div className="ticker-error">{error}</div>}

            <div className="form-modal-actions">
              <button className="btn btn-primary" onClick={handleSave} disabled={saving}>
                {saving ? t("ticker.saving") : editId ? t("ticker.saveChanges") : t("ticker.addMessage")}
              </button>
              <button className="btn btn-outline" onClick={closeModal}>{t("common.cancel")}</button>
            </div>
          </div>
        </FormModal>
      )}
    </div>
  );
};
