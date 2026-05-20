import React, { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../services/api";
import { QueueSettings } from "../hooks/useSignalR";
import { useConfirm } from "../hooks/useConfirm";

interface Props {
  settings: QueueSettings;
}

export const SettingsPanel: React.FC<Props> = ({ settings }) => {
  const [confirmModal, confirm] = useConfirm();
  const [form,    setForm]    = useState(settings);
  const [saving,  setSaving]  = useState(false);
  const [saved,   setSaved]   = useState(false);

  const [spotifyConnected, setSpotifyConnected] = useState<boolean | null>(null);
  const [spotifyBusy,      setSpotifyBusy]      = useState(false);

  const [ytAuth, setYtAuth] = useState<{ enabled: boolean; authenticated: boolean; account: string | null; savedAt: string | null } | null>(null);
  const [ytBusy, setYtBusy] = useState(false);

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

  // Sync if settings change via SignalR (our own save echo or another client).
  // Update lastSavedRef so this incoming state doesn't trigger a redundant PUT.
  useEffect(() => {
    setForm(settings);
    lastSavedRef.current = settings;
  }, [settings]);

  const refreshYtAuth = useCallback(() => {
    api.getYouTubeAuthStatus()
      .then(r => setYtAuth({ enabled: r.enabled, authenticated: r.authenticated, account: r.account, savedAt: r.savedAt }))
      .catch(() => setYtAuth({ enabled: false, authenticated: false, account: null, savedAt: null }));
  }, []);

  useEffect(() => {
    api.getSpotifyStatus().then(r => setSpotifyConnected(r.authenticated)).catch(() => setSpotifyConnected(false));
    refreshYtAuth();
    checkRelay();
  }, [checkRelay, refreshYtAuth]);

  const handleYouTubeConnect = async () => {
    const ok = await confirm({
      title:       "Usa una cuenta desechable",
      message: (
        <>
          Vas a iniciar sesión con una cuenta de Google. Las cookies se guardarán en disco y cualquier
          proceso con acceso al archivo podrá impersonar esa cuenta. YouTube también puede banearla por
          uso de yt-dlp.
          <br/><br/>
          Usa una cuenta nueva creada solo para esto,{" "}
          <strong style={{
            background:   "#fde047",
            color:        "#1a1a1a",
            padding:      "2px 6px",
            borderRadius: 4,
            fontWeight:   800,
          }}>
            NUNCA tu cuenta personal
          </strong>.
        </>
      ),
      confirmText: "Entiendo el riesgo, continuar",
      danger:      true,
    });
    if (!ok) return;
    setYtBusy(true);
    try {
      await api.startYouTubeLogin();
      // Poll until cookies are captured
      const poll = setInterval(async () => {
        const r = await api.getYouTubeAuthStatus().catch(() => null);
        if (r?.authenticated) {
          setYtAuth({ enabled: r.enabled, authenticated: true, account: r.account, savedAt: r.savedAt });
          clearInterval(poll);
          setYtBusy(false);
        } else if (r?.cancelled) {
          clearInterval(poll);
          setYtBusy(false);
        }
      }, 1500);
      setTimeout(() => { clearInterval(poll); setYtBusy(false); }, 180_000);
    } catch { setYtBusy(false); }
  };

  const handleYouTubeDisconnect = async () => {
    const ok = await confirm({ title: "¿Desconectar YouTube?", message: "Se eliminarán las cookies guardadas. Las descargas que requieran autenticación volverán a fallar con 'Sign in to confirm you're not a bot'.", confirmText: "Desconectar", danger: true });
    if (!ok) return;
    setYtBusy(true);
    try {
      await api.disconnectYouTubeAuth();
      refreshYtAuth();
    } finally { setYtBusy(false); }
  };

  const handleYouTubeToggle = async () => {
    if (!ytAuth) return;
    setYtBusy(true);
    try {
      if (ytAuth.enabled) await api.disableYouTubeAuth();
      else                 await api.enableYouTubeAuth();
      refreshYtAuth();
    } finally { setYtBusy(false); }
  };

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
    const ok = await confirm({ title: "¿Desconectar Spotify?", message: "Deberás volver a autorizar la aplicación para usar funciones de Spotify.", confirmText: "Desconectar", danger: true });
    if (!ok) return;
    setSpotifyBusy(true);
    try {
      await api.disconnectSpotify();
      setSpotifyConnected(false);
    } finally {
      setSpotifyBusy(false);
    }
  };

  // ── Auto-save ────────────────────────────────────────────────────────────
  // Every change to `form` debounces a PUT to /api/settings (500ms). No save
  // button, no risk of forgetting to persist. We compare against the last
  // value we successfully saved so SignalR pushes from the server (which arrive
  // via the `settings` prop) don't loop back as a redundant save.
  const lastSavedRef = useRef<QueueSettings>(settings);
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Suppress the first auto-save effect run so the initial sync from props
  // doesn't trigger a no-op PUT on mount.
  const isInitialSyncRef = useRef(true);

  useEffect(() => {
    if (isInitialSyncRef.current) {
      isInitialSyncRef.current = false;
      lastSavedRef.current = form;
      return;
    }
    // Skip if `form` matches whatever the server last confirmed — covers the
    // re-sync after our own save completes and SignalR echoes the new state back.
    if (JSON.stringify(form) === JSON.stringify(lastSavedRef.current)) return;

    if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
    setSaving(true);
    setSaved(false);
    saveTimerRef.current = setTimeout(async () => {
      try {
        await api.updateSettings(form);
        lastSavedRef.current = form;
        setSaved(true);
        setTimeout(() => setSaved(false), 1500);
      } catch (e) {
        console.error("Failed to save settings", e);
      } finally {
        setSaving(false);
      }
    }, 500);

    return () => { if (saveTimerRef.current) clearTimeout(saveTimerRef.current); };
  }, [form]);

  const set = (key: keyof QueueSettings, value: number | boolean | string) =>
    setForm(f => ({ ...f, [key]: value }));

  const statusLabel = saving ? "Guardando…" : saved ? "✓ Guardado" : "Los cambios se guardan automáticamente";
  const statusColor = saving
    ? "var(--color-muted, #888)"
    : saved
      ? "var(--color-success, #1db954)"
      : "var(--color-muted, #888)";

  return (
    <>{confirmModal}
    <div className="settings-panel">
      <div
        className="settings-autosave-status"
        style={{
          position:     "sticky",
          top:          0,
          zIndex:       10,
          padding:      "6px 10px",
          marginBottom: 8,
          fontSize:     12,
          fontWeight:   500,
          color:        statusColor,
          background:   "var(--color-bg, #1a1a1a)",
          borderBottom: "1px solid var(--color-border, rgba(255,255,255,0.08))",
        }}
      >
        {statusLabel}
      </div>
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

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">Normalizar volumen al descargar</span>
          <div
            className={`settings-toggle${form.loudnessNormalizationEnabled ? " on" : ""}`}
            onClick={() => set("loudnessNormalizationEnabled", !form.loudnessNormalizationEnabled)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          Iguala el volumen de las canciones descargadas a ~-14 LUFS (estilo Spotify/YouTube Music) usando ffmpeg
          <code>loudnorm</code> en dos pasadas con <code>linear=true</code> — aplica una única ganancia lineal sin compresión dinámica,
          así se preserva la dinámica original (sin "pumping" ni distorsión). Solo afecta descargas nuevas;
          si tienes canciones ya cacheadas que sonaban mal con la versión anterior, bórralas de la librería para re-descargarlas.
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
        <div className="settings-section-title">YouTube (cookies para descargas)</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">Usar cookies de YouTube en yt-dlp</span>
          <div
            className={`settings-toggle${ytAuth?.enabled ? " on" : ""}`}
            onClick={() => !ytBusy && handleYouTubeToggle()}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          Cuando está activo y tienes una sesión conectada, yt-dlp usa tus cookies para evitar el bloqueo
          <em> "Sign in to confirm you're not a bot"</em> y errores HTTP 429. Las cookies se guardan localmente en
          <code> %LOCALAPPDATA%/MusicBot/youtube_cookies.txt</code> y nunca se envían a ningún servidor externo.
        </p>

        <div className="settings-row">
          <span className="settings-label">Estado de conexión</span>
          <span style={{ fontWeight: 600, color:
              ytAuth === null                ? "var(--color-muted, #888)"
            : ytAuth.authenticated           ? "var(--color-success, #1db954)"
            : ytAuth.enabled                 ? "var(--color-danger, #e05252)"
            :                                  "var(--color-muted, #888)" }}>
            {ytAuth === null
              ? "Comprobando…"
              : ytAuth.authenticated
                ? `Conectado${ytAuth.account ? ` (${ytAuth.account})` : ""}`
                : ytAuth.enabled
                  ? "Sin sesión — conecta para activar"
                  : "Desactivado"}
          </span>
        </div>

        <div className="settings-row" style={{ gap: 8 }}>
          {ytAuth?.authenticated
            ? <button className="btn btn-sm btn-danger" onClick={handleYouTubeDisconnect} disabled={ytBusy}>
                {ytBusy ? "Procesando…" : "Desconectar YouTube"}
              </button>
            : <button className="btn btn-sm btn-primary" onClick={handleYouTubeConnect} disabled={ytBusy}>
                {ytBusy ? "Esperando login…" : "Conectar YouTube"}
              </button>
          }
        </div>
        <p className="settings-hint">
          Inicia sesión una vez con tu cuenta de Google en la ventana embebida. La sesión se restaura automáticamente al iniciar la app.
          Si las cookies expiran (~6 meses), vuelve a conectar.
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
        <div className="settings-section-title">Auto-cola</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">Habilitar auto-cola</span>
          <div
            className={`settings-toggle${form.autoQueueEnabled ? " on" : ""}`}
            onClick={() => set("autoQueueEnabled", !form.autoQueueEnabled)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          Cuando la cola de solicitudes está vacía, reproduce canciones aleatoriamente del pool de la auto-cola.
          Administra el pool en el tab <strong>Auto-cola</strong>.
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

    </div>
    </>
  );
};
