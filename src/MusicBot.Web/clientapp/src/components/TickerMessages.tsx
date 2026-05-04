import React, { useState } from "react";
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

function platformsLabel(platforms: string[]) {
  if (platforms.length === 0) return "Ninguna plataforma";
  if (platforms.length === ALL_PLATFORMS.length) return "Todas las plataformas";
  return platforms.map(p => PLATFORM_LABELS[p as Platform] ?? p).join(", ");
}

export const TickerMessages: React.FC<Props> = ({ messages }) => {
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
    if (!form.text.trim()) { setError("Ingresa el texto del mensaje"); return; }
    if (form.platforms.length === 0) { setError("Selecciona al menos una plataforma"); return; }
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
      setError(e.message ?? "Error al guardar");
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id: string) => {
    const ok = await confirm({ title: "¿Eliminar mensaje?", message: "Esta acción no se puede deshacer.", confirmText: "Eliminar", danger: true });
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
          <span className="ticker-stat-label">mensajes</span>
        </div>
        <div className="ticker-stat-divider" />
        <div className="ticker-stat">
          <span className="ticker-stat-num ticker-stat-active">{activeCount}</span>
          <span className="ticker-stat-label">activos</span>
        </div>
        <div className="ticker-stat-divider" />
        <div className="ticker-stat">
          <span className="ticker-stat-num ticker-stat-inactive">{sorted.length - activeCount}</span>
          <span className="ticker-stat-label">inactivos</span>
        </div>
        <button className="btn btn-primary btn-sm ticker-add-btn" onClick={openNew}>
          + Nuevo mensaje
        </button>
      </div>

      {/* ── List ── */}
      {sorted.length === 0 ? (
        <div className="ticker-empty">
          <div className="ticker-empty-icon">💬</div>
          <div className="ticker-empty-title">Sin mensajes aún</div>
          <div className="ticker-empty-sub">Agrega tu primer mensaje para que el bot lo envíe al chat.</div>
        </div>
      ) : (
        <div className="ticker-list">
          {sorted.map((msg, idx) => (
            <div key={msg.id} className={`ticker-card${msg.enabled ? " ticker-card-on" : " ticker-card-off"}`}>
              <div className="ticker-card-order">{idx + 1}</div>

              <div className="ticker-card-preview-area">
                <div className="ticker-card-content">
                  <div className="ticker-card-text">{msg.text || <em className="ticker-card-only-img">Sin texto</em>}</div>
                  <div className="ticker-card-meta">
                    <span className={`ticker-status-pill${msg.enabled ? " on" : " off"}`}>
                      {msg.enabled ? "● Activo" : "○ Inactivo"}
                    </span>
                    <span className="ticker-card-dur">⏱ {msg.intervalMinutes === 60 ? "1 h" : `${msg.intervalMinutes} min`}</span>
                    {msg.minChatMessages > 0 && (
                      <span className="ticker-card-dur">💬 {msg.minChatMessages} msgs mín.</span>
                    )}
                    <span className="ticker-card-platforms">{platformsLabel(msg.platforms ?? [])}</span>
                  </div>
                </div>
              </div>

              <div className="ticker-card-actions">
                <button
                  className={`btn btn-sm ${msg.enabled ? "btn-outline" : "btn-primary"}`}
                  onClick={() => handleToggle(msg)}
                >
                  {msg.enabled ? "Pausar" : "Activar"}
                </button>
                <button className="btn btn-sm btn-outline" onClick={() => openEdit(msg)}>Editar</button>
                <button className="btn btn-sm btn-danger" onClick={() => handleDelete(msg.id)}>✕</button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* ── Modal ── */}
      {modalOpen && (
        <FormModal
          title={editId ? "Editar mensaje" : "Nuevo mensaje"}
          onClose={closeModal}
          width={480}
        >
          <div className="ticker-modal-body">
            {/* Text */}
            <Label text="Texto del mensaje" tooltip="Mensaje que el bot enviará al chat. Se envía tal cual, sin mencionar a ningún usuario." />
            <textarea
              className="input ticker-textarea"
              placeholder="Ej: Usa !play [canción] para pedir canciones"
              value={form.text}
              rows={3}
              autoFocus
              onChange={e => setForm(f => ({ ...f, text: e.target.value }))}
            />

            <div className="ticker-form-row2">
              {/* Interval */}
              <div className="ticker-field ticker-field-sm">
                <Label text="Intervalo" tooltip="Tiempo mínimo que debe pasar entre cada envío de este mensaje. El contador se reinicia tras cada envío." />
                <select
                  className="input settings-input-sm ticker-interval-select"
                  value={form.intervalMinutes}
                  onChange={e => setForm(f => ({ ...f, intervalMinutes: Number(e.target.value) }))}
                >
                  {INTERVAL_OPTIONS.map(m => (
                    <option key={m} value={m}>{m === 60 ? "1 h" : `${m} min`}</option>
                  ))}
                </select>
              </div>

              {/* Min chat messages */}
              <div className="ticker-field ticker-field-sm">
                <Label text="Mín. mensajes" tooltip="Número mínimo de mensajes que deben haberse enviado en el chat desde el último envío de este timer. Si el chat está inactivo, el mensaje se pospone. 0 = sin mínimo." />
                <input
                  type="number"
                  className="input settings-input-sm"
                  min={0} max={9999}
                  value={form.minChatMessages}
                  onChange={e => setForm(f => ({ ...f, minChatMessages: Math.max(0, Number(e.target.value)) }))}
                />
                {form.minChatMessages === 0 && (
                  <span className="ticker-field-hint">Sin mínimo</span>
                )}
              </div>

              {/* Enabled toggle */}
              <div className="ticker-field ticker-field-toggle">
                <Label text="Estado" tooltip="Los mensajes inactivos no se envían al chat aunque haya transcurrido el intervalo." />
                <div className="ticker-toggle-row" onClick={() => setForm(f => ({ ...f, enabled: !f.enabled }))}>
                  <div className={`settings-toggle${form.enabled ? " on" : ""}`}>
                    <div className="settings-toggle-thumb" />
                  </div>
                  <span className={`ticker-toggle-label${form.enabled ? " on" : ""}`}>
                    {form.enabled ? "Activado" : "Inactivo"}
                  </span>
                </div>
              </div>
            </div>

            {/* Platforms */}
            <Label text="Plataformas" tooltip="Plataformas a las que se enviará este mensaje. Solo se envía a las que estén conectadas en el momento del envío." />
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
              <div className="ticker-platforms-warning">⚠ Sin plataformas seleccionadas — el mensaje no se enviará.</div>
            )}

            {error && <div className="ticker-error">{error}</div>}

            <div className="form-modal-actions">
              <button className="btn btn-primary" onClick={handleSave} disabled={saving}>
                {saving ? "Guardando…" : editId ? "Guardar cambios" : "Agregar mensaje"}
              </button>
              <button className="btn btn-outline" onClick={closeModal}>Cancelar</button>
            </div>
          </div>
        </FormModal>
      )}
    </div>
  );
};
