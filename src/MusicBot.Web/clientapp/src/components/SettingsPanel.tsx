import React, { useCallback, useEffect, useState } from "react";
import { api } from "../services/api";
import { QueueSettings } from "../hooks/useSignalR";

interface Props {
  settings: QueueSettings;
}

export const SettingsPanel: React.FC<Props> = ({ settings }) => {
  const [form,    setForm]    = useState(settings);
  const [saving,  setSaving]  = useState(false);
  const [saved,   setSaved]   = useState(false);

  const [spotifyConnected, setSpotifyConnected] = useState<boolean | null>(null);
  const [spotifyBusy,      setSpotifyBusy]      = useState(false);

  const [relayStatus, setRelayStatus] = useState<{ configured: boolean; reachable: boolean; error: string | null } | null>(null);
  const [relayChecking, setRelayChecking] = useState(false);

  const [ytDlpUpdating, setYtDlpUpdating] = useState(false);
  const [ytDlpMsg,      setYtDlpMsg]      = useState<{ text: string; err: boolean } | null>(null);

  const handleUpdateYtDlp = useCallback(async () => {
    setYtDlpUpdating(true);
    setYtDlpMsg(null);
    try {
      const r = await api.updateYtDlp();
      setYtDlpMsg({ text: r.message, err: false });
    } catch (e: unknown) {
      setYtDlpMsg({ text: e instanceof Error ? e.message : "Error al actualizar", err: true });
    } finally {
      setYtDlpUpdating(false);
    }
  }, []);

  const checkRelay = useCallback(async () => {
    setRelayChecking(true);
    try {
      setRelayStatus(await api.getRelayStatus());
    } catch {
      setRelayStatus({ configured: false, reachable: false, error: "No se pudo contactar el servidor" });
    } finally {
      setRelayChecking(false);
    }
  }, []);

  // Sync if settings change via SignalR from another client
  useEffect(() => { setForm(settings); }, [settings]);

  useEffect(() => {
    api.getSpotifyStatus().then(r => setSpotifyConnected(r.authenticated)).catch(() => setSpotifyConnected(false));
    checkRelay();
  }, [checkRelay]);

  const handleSpotifyConnect = async () => {
    setSpotifyBusy(true);
    try {
      const { url } = await api.getSpotifyAuthUrl();
      window.open(url, "_blank", "width=500,height=700");
      // Poll until connected
      const poll = setInterval(async () => {
        const r = await api.getSpotifyStatus().catch(() => ({ authenticated: false }));
        if (r.authenticated) { setSpotifyConnected(true); clearInterval(poll); setSpotifyBusy(false); }
      }, 2000);
      setTimeout(() => { clearInterval(poll); setSpotifyBusy(false); }, 120_000);
    } catch { setSpotifyBusy(false); }
  };

  const handleSpotifyDisconnect = async () => {
    if (!confirm("¿Desconectar Spotify? Deberás volver a autorizar para usar funciones de Spotify.")) return;
    setSpotifyBusy(true);
    try {
      await api.disconnectSpotify();
      setSpotifyConnected(false);
    } finally {
      setSpotifyBusy(false);
    }
  };

  const handleSave = async () => {
    setSaving(true);
    try {
      await api.updateSettings(form);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      console.error("Failed to save settings", e);
    } finally {
      setSaving(false);
    }
  };

  const set = (key: keyof QueueSettings, value: number | boolean | string) =>
    setForm(f => ({ ...f, [key]: value }));

  return (
    <div className="settings-panel">
      <div className="settings-section">
        <div className="settings-section-title">Cola de reproducción</div>

        <label className="settings-row">
          <span className="settings-label">Tamaño máximo de la cola</span>
          <input
            type="number" min={1} max={500}
            className="input settings-input-sm"
            value={form.maxQueueSize}
            onChange={e => set("maxQueueSize", Number(e.target.value))}
          />
          <span className="settings-unit">canciones</span>
        </label>

        <label className="settings-row">
          <span className="settings-label">Máx. canciones por usuario</span>
          <input
            type="number" min={1} max={50}
            className="input settings-input-sm"
            value={form.maxSongsPerUser}
            onChange={e => set("maxSongsPerUser", Number(e.target.value))}
          />
          <span className="settings-unit">por usuario</span>
        </label>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">Votación de skip</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">Habilitar votación de skip al inicio de canción</span>
          <div
            className={`settings-toggle${form.votingEnabled ? " on" : ""}`}
            onClick={() => set("votingEnabled", !form.votingEnabled)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          Cuando está activa, cada canción abre una votación de 30 segundos en el chat (!si = skip · !no = quedar).
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">Chequeo de presencia</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">Habilitar chequeo de presencia al iniciar canción</span>
          <div
            className={`settings-toggle${form.presenceCheckEnabled ? " on" : ""}`}
            onClick={() => set("presenceCheckEnabled", !form.presenceCheckEnabled)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>

        <label className="settings-row">
          <span className="settings-label">Tiempo de aviso previo</span>
          <input
            type="number" min={5} max={120}
            className="input settings-input-sm"
            value={form.presenceCheckWarningSeconds}
            onChange={e => set("presenceCheckWarningSeconds", Number(e.target.value))}
            disabled={!form.presenceCheckEnabled}
          />
          <span className="settings-unit">segundos antes</span>
        </label>

        <label className="settings-row">
          <span className="settings-label">Tiempo para confirmar al iniciar</span>
          <input
            type="number" min={5} max={120}
            className="input settings-input-sm"
            value={form.presenceCheckConfirmSeconds}
            onChange={e => set("presenceCheckConfirmSeconds", Number(e.target.value))}
            disabled={!form.presenceCheckEnabled}
          />
          <span className="settings-unit">segundos</span>
        </label>

        <p className="settings-hint">
          Se avisa al solicitante N segundos antes y se espera confirmación con <code>!aqui</code> al iniciar. Si no confirma, la canción se saltea. Otros usuarios pueden usar <code>!keep</code> para salvarla.
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">Descargas</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">Guardar archivos descargados permanentemente</span>
          <div
            className={`settings-toggle${form.saveDownloads ? " on" : ""}`}
            onClick={() => set("saveDownloads", !form.saveDownloads)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          Si está desactivado, los archivos se eliminan automáticamente al terminar de reproducirse.
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">Spotify</div>
        <div className="settings-row">
          <span className="settings-label">Estado de conexión</span>
          <span style={{ fontWeight: 600, color: spotifyConnected ? "var(--color-success, #1db954)" : "var(--color-muted, #888)" }}>
            {spotifyConnected === null ? "Comprobando…" : spotifyConnected ? "Conectado" : "Desconectado"}
          </span>
        </div>
        <div className="settings-row" style={{ gap: 8 }}>
          {spotifyConnected
            ? <button className="btn btn-sm btn-danger" onClick={handleSpotifyDisconnect} disabled={spotifyBusy}>
                {spotifyBusy ? "Desconectando…" : "Desconectar Spotify"}
              </button>
            : <button className="btn btn-sm btn-primary" onClick={handleSpotifyConnect} disabled={spotifyBusy}>
                {spotifyBusy ? "Abriendo…" : "Conectar Spotify"}
              </button>
          }
        </div>
        <p className="settings-hint">
          Si el import de playlists devuelve error 403, desconecta y vuelve a conectar para renovar los permisos.
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">Relay OAuth</div>
        <div className="settings-row">
          <span className="settings-label">Estado</span>
          <span style={{
            fontWeight: 600,
            color: !relayStatus
              ? "var(--color-muted, #888)"
              : relayStatus.reachable
              ? "var(--color-success, #1db954)"
              : "var(--color-danger, #e05252)",
          }}>
            {!relayStatus
              ? "Comprobando…"
              : !relayStatus.configured
              ? "No configurado"
              : relayStatus.reachable
              ? "Activo"
              : `Error${relayStatus.error ? `: ${relayStatus.error}` : ""}`}
          </span>
        </div>
        <div className="settings-row" style={{ gap: 8 }}>
          <button className="btn btn-sm" onClick={checkRelay} disabled={relayChecking}>
            {relayChecking ? "Verificando…" : "Verificar conexión"}
          </button>
        </div>
        <p className="settings-hint">
          Proxy seguro en Cloudflare Workers que gestiona el intercambio de tokens OAuth con Spotify, Twitch y Kick. Los <code>client_secret</code> se almacenan solo en el Worker, nunca en el cliente.
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">Aplicación</div>

        <div className="settings-row" style={{ flexDirection: "column", alignItems: "flex-start", gap: 6 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <button className="btn btn-sm" onClick={handleUpdateYtDlp} disabled={ytDlpUpdating}>
              {ytDlpUpdating ? "Actualizando…" : "Actualizar yt-dlp"}
            </button>
          </div>
          {ytDlpMsg && (
            <span style={{ fontSize: 12, color: ytDlpMsg.err ? "var(--color-error, #ef4444)" : "var(--color-success, #22c55e)" }}>
              {ytDlpMsg.text}
            </span>
          )}
        </div>
        <p className="settings-hint">
          Descarga la última versión de yt-dlp desde GitHub. Necesario cuando YouTube cambia su sistema anti-bot (error "Sign in to confirm you're not a bot"). La app también actualiza automáticamente al inicio si el binario tiene más de 7 días.
        </p>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">Abrir ventana de logs al iniciar</span>
          <div
            className={`settings-toggle${form.openLogOnStart ? " on" : ""}`}
            onClick={() => set("openLogOnStart", !form.openLogOnStart)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          Abre automáticamente la ventana de logs del sistema cuando MusicBot inicia. Útil para depuración.
        </p>
      </div>

      <div className="settings-actions">
        <button className="btn btn-primary" onClick={handleSave} disabled={saving}>
          {saved ? "✓ Guardado" : saving ? "Guardando…" : "Guardar cambios"}
        </button>
      </div>
    </div>
  );
};
