import React, { useState } from "react";
import { api } from "../services/api";
import { TickerMessage } from "../hooks/useSignalR";

interface Props {
  messages: TickerMessage[];
}

const EMPTY_FORM = { text: "", imageUrl: "", durationSec: 8, enabled: true };

export const TickerMessages: React.FC<Props> = ({ messages }) => {
  const [form,   setForm]   = useState(EMPTY_FORM);
  const [editId, setEditId] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error,  setError]  = useState<string | null>(null);

  const resetForm = () => { setForm(EMPTY_FORM); setEditId(null); setError(null); };

  const startEdit = (msg: TickerMessage) => {
    setEditId(msg.id);
    setForm({ text: msg.text, imageUrl: msg.imageUrl ?? "", durationSec: msg.durationSec, enabled: msg.enabled });
    setError(null);
    window.scrollTo({ top: 0, behavior: "smooth" });
  };

  const handleSave = async () => {
    if (!form.text.trim() && !form.imageUrl.trim()) { setError("Ingresa texto o imagen"); return; }
    setSaving(true);
    setError(null);
    try {
      const payload = { text: form.text.trim(), imageUrl: form.imageUrl.trim() || undefined, durationSec: form.durationSec, enabled: form.enabled };
      if (editId) {
        await api.updateTicker(editId, payload);
      } else {
        await api.addTicker(payload);
      }
      resetForm();
    } catch (e: any) {
      setError(e.message ?? "Error al guardar");
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id: string) => {
    if (!confirm("¿Eliminar este mensaje?")) return;
    try { await api.deleteTicker(id); } catch {}
  };

  const handleToggle = async (msg: TickerMessage) => {
    try { await api.updateTicker(msg.id, { text: msg.text, imageUrl: msg.imageUrl, durationSec: msg.durationSec, enabled: !msg.enabled }); }
    catch {}
  };

  const sorted = [...messages].sort((a, b) => a.order - b.order);
  const activeCount = sorted.filter(m => m.enabled).length;

  return (
    <div className="ticker-panel">

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
        <div className="ticker-stat-hint">
          Los mensajes activos rotan exclusivamente en el overlay <strong>Player</strong>, diseñado para usarse en pantalla completa.
        </div>
      </div>

      {/* ── Form ── */}
      <div className={`ticker-form-card${editId ? " ticker-form-editing" : ""}`}>
        <div className="ticker-form-header">
          <span className="ticker-form-icon">{editId ? "✏️" : "✨"}</span>
          <span className="ticker-form-title">{editId ? "Editando mensaje" : "Nuevo mensaje"}</span>
          {editId && (
            <button className="ticker-form-cancel-x" onClick={resetForm} title="Cancelar">✕</button>
          )}
        </div>

        <div className="ticker-form-body">
          <div className="ticker-field">
            <label className="ticker-field-label">Texto del mensaje</label>
            <input
              type="text" className="input"
              placeholder="Ej: Usa !play [canción] para pedir canciones"
              value={form.text}
              onChange={e => setForm(f => ({ ...f, text: e.target.value }))}
            />
          </div>

          <div className="ticker-field">
            <label className="ticker-field-label">URL de imagen <span className="ticker-optional">(opcional)</span></label>
            <input
              type="text" className="input"
              placeholder="https://..."
              value={form.imageUrl}
              onChange={e => setForm(f => ({ ...f, imageUrl: e.target.value }))}
            />
          </div>

          <div className="ticker-form-row2">
            <div className="ticker-field ticker-field-sm">
              <label className="ticker-field-label">Duración</label>
              <div className="ticker-duration-wrap">
                <input
                  type="number" className="input settings-input-sm"
                  min={2} max={120}
                  value={form.durationSec}
                  onChange={e => setForm(f => ({ ...f, durationSec: Number(e.target.value) }))}
                />
                <span className="ticker-unit">seg</span>
              </div>
            </div>

            <div className="ticker-field ticker-field-toggle">
              <label className="ticker-field-label">Estado</label>
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

          {/* Preview */}
          {(form.text || form.imageUrl) && (
            <div className="ticker-preview">
              <span className="ticker-preview-label">Vista previa</span>
              <div className="ticker-preview-box">
                {form.imageUrl && <img src={form.imageUrl} alt="" className="ticker-preview-img" />}
                {form.text && <span className="ticker-preview-text">{form.text}</span>}
              </div>
            </div>
          )}
        </div>

        {error && <div className="ticker-error">{error}</div>}

        <div className="ticker-form-actions">
          <button className="btn btn-primary" onClick={handleSave} disabled={saving}>
            {saving ? "Guardando…" : editId ? "Guardar cambios" : "Agregar mensaje"}
          </button>
          {editId && (
            <button className="btn btn-outline" onClick={resetForm}>Cancelar</button>
          )}
        </div>
      </div>

      {/* ── List ── */}
      {sorted.length === 0 ? (
        <div className="ticker-empty">
          <div className="ticker-empty-icon">📢</div>
          <div className="ticker-empty-title">Sin mensajes aún</div>
          <div className="ticker-empty-sub">Agrega tu primer mensaje arriba para que aparezca en el overlay.</div>
        </div>
      ) : (
        <div className="ticker-list">
          {sorted.map((msg, idx) => (
            <div key={msg.id} className={`ticker-card${msg.enabled ? " ticker-card-on" : " ticker-card-off"}`}>
              <div className="ticker-card-order">{idx + 1}</div>

              <div className="ticker-card-preview-area">
                {msg.imageUrl && <img src={msg.imageUrl} alt="" className="ticker-card-img" />}
                <div className="ticker-card-content">
                  <div className="ticker-card-text">{msg.text || <em className="ticker-card-only-img">Solo imagen</em>}</div>
                  <div className="ticker-card-meta">
                    <span className={`ticker-status-pill${msg.enabled ? " on" : " off"}`}>
                      {msg.enabled ? "● Activo" : "○ Inactivo"}
                    </span>
                    <span className="ticker-card-dur">⏱ {msg.durationSec}s</span>
                  </div>
                </div>
              </div>

              <div className="ticker-card-actions">
                <button
                  className={`btn btn-sm ${msg.enabled ? "btn-outline" : "btn-primary"}`}
                  onClick={() => handleToggle(msg)}
                  title={msg.enabled ? "Desactivar" : "Activar"}
                >
                  {msg.enabled ? "Pausar" : "Activar"}
                </button>
                <button className="btn btn-sm btn-outline" onClick={() => startEdit(msg)}>Editar</button>
                <button className="btn btn-sm btn-danger" onClick={() => handleDelete(msg.id)}>✕</button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
