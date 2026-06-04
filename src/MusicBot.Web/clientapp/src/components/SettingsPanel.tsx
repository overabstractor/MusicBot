import React, { useCallback, useEffect, useRef, useState } from "react";
import { Trans, useTranslation } from "react-i18next";
import { api } from "../services/api";
import { QueueSettings } from "../hooks/useSignalR";
import { useConfirm } from "../hooks/useConfirm";

interface Props {
  settings: QueueSettings;
}

export const SettingsPanel: React.FC<Props> = ({ settings }) => {
  const { t } = useTranslation();
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
      setYtDlpMsg({ text: e instanceof Error ? e.message : t("settings.ytDlpUpdateError"), err: true });
    } finally {
      setYtDlpUpdating(false);
    }
  }, [t]);

  const checkRelay = useCallback(async () => {
    setRelayChecking(true);
    try {
      setRelayStatus(await api.getRelayStatus());
    } catch {
      setRelayStatus({ configured: false, reachable: false, error: t("settings.serverUnreachable") });
    } finally {
      setRelayChecking(false);
    }
  }, [t]);

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
      title:       t("settings.ytRiskTitle"),
      message: (
        <Trans
          i18nKey="settings.ytRiskMessage"
          components={{
            br: <br/>,
            warn: <strong style={{
              background:   "#fde047",
              color:        "#1a1a1a",
              padding:      "2px 6px",
              borderRadius: 4,
              fontWeight:   800,
            }} />,
          }}
        />
      ),
      confirmText: t("settings.ytRiskConfirm"),
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
    const ok = await confirm({ title: t("settings.disconnectYoutubeTitle"), message: t("settings.disconnectYoutubeMessage"), confirmText: t("settings.disconnect"), danger: true });
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
    const ok = await confirm({ title: t("settings.disconnectSpotifyTitle"), message: t("settings.disconnectSpotifyMessage"), confirmText: t("settings.disconnect"), danger: true });
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

  const statusLabel = saving ? t("settings.saving") : saved ? t("settings.saved") : t("settings.autosaveHint");
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
        <div className="settings-section-title">{t("settings.queueSection")}</div>

        <label className="settings-row">
          <span className="settings-label">{t("settings.maxQueueSize")}</span>
          <input
            type="number" min={1} max={500}
            className="input settings-input-sm"
            value={form.maxQueueSize}
            onChange={e => set("maxQueueSize", Number(e.target.value))}
          />
          <span className="settings-unit">{t("settings.songsUnit")}</span>
        </label>

        <label className="settings-row">
          <span className="settings-label">{t("settings.maxSongsPerUser")}</span>
          <input
            type="number" min={1} max={50}
            className="input settings-input-sm"
            value={form.maxSongsPerUser}
            onChange={e => set("maxSongsPerUser", Number(e.target.value))}
          />
          <span className="settings-unit">{t("settings.perUserUnit")}</span>
        </label>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">{t("settings.votingSection")}</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">{t("settings.enableVoting")}</span>
          <div
            className={`settings-toggle${form.votingEnabled ? " on" : ""}`}
            onClick={() => set("votingEnabled", !form.votingEnabled)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          {t("settings.votingHint")}
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">{t("settings.presenceSection")}</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">{t("settings.enablePresence")}</span>
          <div
            className={`settings-toggle${form.presenceCheckEnabled ? " on" : ""}`}
            onClick={() => set("presenceCheckEnabled", !form.presenceCheckEnabled)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>

        <label className="settings-row">
          <span className="settings-label">{t("settings.presenceWarnTime")}</span>
          <input
            type="number" min={5} max={120}
            className="input settings-input-sm"
            value={form.presenceCheckWarningSeconds}
            onChange={e => set("presenceCheckWarningSeconds", Number(e.target.value))}
            disabled={!form.presenceCheckEnabled}
          />
          <span className="settings-unit">{t("settings.secondsBeforeUnit")}</span>
        </label>

        <label className="settings-row">
          <span className="settings-label">{t("settings.presenceConfirmTime")}</span>
          <input
            type="number" min={5} max={120}
            className="input settings-input-sm"
            value={form.presenceCheckConfirmSeconds}
            onChange={e => set("presenceCheckConfirmSeconds", Number(e.target.value))}
            disabled={!form.presenceCheckEnabled}
          />
          <span className="settings-unit">{t("settings.secondsUnit")}</span>
        </label>

        <p className="settings-hint">
          <Trans i18nKey="settings.presenceHint" components={{ code: <code /> }} />
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">{t("settings.downloadsSection")}</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">{t("settings.saveDownloads")}</span>
          <div
            className={`settings-toggle${form.saveDownloads ? " on" : ""}`}
            onClick={() => set("saveDownloads", !form.saveDownloads)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          {t("settings.saveDownloadsHint")}
        </p>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">{t("settings.normalizeVolume")}</span>
          <div
            className={`settings-toggle${form.loudnessNormalizationEnabled ? " on" : ""}`}
            onClick={() => set("loudnessNormalizationEnabled", !form.loudnessNormalizationEnabled)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          <Trans i18nKey="settings.normalizeHint" components={{ code: <code /> }} />
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">{t("settings.spotifySection")}</div>
        <div className="settings-row">
          <span className="settings-label">{t("settings.connectionStatus")}</span>
          <span style={{ fontWeight: 600, color: spotifyConnected ? "var(--color-success, #1db954)" : "var(--color-muted, #888)" }}>
            {spotifyConnected === null ? t("settings.checking") : spotifyConnected ? t("settings.connected") : t("settings.disconnected")}
          </span>
        </div>
        <div className="settings-row" style={{ gap: 8 }}>
          {spotifyConnected
            ? <button className="btn btn-sm btn-danger" onClick={handleSpotifyDisconnect} disabled={spotifyBusy}>
                {spotifyBusy ? t("settings.disconnecting") : t("settings.disconnectSpotify")}
              </button>
            : <button className="btn btn-sm btn-primary" onClick={handleSpotifyConnect} disabled={spotifyBusy}>
                {spotifyBusy ? t("settings.opening") : t("settings.connectSpotify")}
              </button>
          }
        </div>
        <p className="settings-hint">
          {t("settings.spotifyHint")}
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">{t("settings.youtubeSection")}</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">{t("settings.useYtCookies")}</span>
          <div
            className={`settings-toggle${ytAuth?.enabled ? " on" : ""}`}
            onClick={() => !ytBusy && handleYouTubeToggle()}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          <Trans i18nKey="settings.ytCookiesHint" components={{ em: <em />, code: <code /> }} />
        </p>

        <div className="settings-row">
          <span className="settings-label">{t("settings.connectionStatus")}</span>
          <span style={{ fontWeight: 600, color:
              ytAuth === null                ? "var(--color-muted, #888)"
            : ytAuth.authenticated           ? "var(--color-success, #1db954)"
            : ytAuth.enabled                 ? "var(--color-danger, #e05252)"
            :                                  "var(--color-muted, #888)" }}>
            {ytAuth === null
              ? t("settings.checking")
              : ytAuth.authenticated
                ? (ytAuth.account ? t("settings.connectedWithAccount", { account: ytAuth.account }) : t("settings.connected"))
                : ytAuth.enabled
                  ? t("settings.noSession")
                  : t("settings.disabled")}
          </span>
        </div>

        <div className="settings-row" style={{ gap: 8 }}>
          {ytAuth?.authenticated
            ? <button className="btn btn-sm btn-danger" onClick={handleYouTubeDisconnect} disabled={ytBusy}>
                {ytBusy ? t("settings.processing") : t("settings.disconnectYoutube")}
              </button>
            : <button className="btn btn-sm btn-primary" onClick={handleYouTubeConnect} disabled={ytBusy}>
                {ytBusy ? t("settings.waitingLogin") : t("settings.connectYoutube")}
              </button>
          }
        </div>
        <p className="settings-hint">
          {t("settings.youtubeHint")}
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">{t("settings.relaySection")}</div>
        <div className="settings-row">
          <span className="settings-label">{t("settings.relayStatusLabel")}</span>
          <span style={{
            fontWeight: 600,
            color: !relayStatus
              ? "var(--color-muted, #888)"
              : relayStatus.reachable
              ? "var(--color-success, #1db954)"
              : "var(--color-danger, #e05252)",
          }}>
            {!relayStatus
              ? t("settings.checking")
              : !relayStatus.configured
              ? t("settings.notConfigured")
              : relayStatus.reachable
              ? t("settings.active")
              : relayStatus.error
              ? t("settings.relayErrorDetail", { error: relayStatus.error })
              : t("common.error")}
          </span>
        </div>
        <div className="settings-row" style={{ gap: 8 }}>
          <button className="btn btn-sm" onClick={checkRelay} disabled={relayChecking}>
            {relayChecking ? t("settings.verifying") : t("settings.verifyConnection")}
          </button>
        </div>
        <p className="settings-hint">
          <Trans i18nKey="settings.relayHint" components={{ code: <code /> }} />
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">{t("settings.autoQueueSection")}</div>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">{t("settings.enableAutoQueue")}</span>
          <div
            className={`settings-toggle${form.autoQueueEnabled ? " on" : ""}`}
            onClick={() => set("autoQueueEnabled", !form.autoQueueEnabled)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          <Trans i18nKey="settings.autoQueueHint" components={{ strong: <strong /> }} />
        </p>
      </div>

      <div className="settings-section">
        <div className="settings-section-title">{t("settings.appSection")}</div>

        <div className="settings-row" style={{ flexDirection: "column", alignItems: "flex-start", gap: 6 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <button className="btn btn-sm" onClick={handleUpdateYtDlp} disabled={ytDlpUpdating}>
              {ytDlpUpdating ? t("settings.updating") : t("settings.updateYtDlp")}
            </button>
          </div>
          {ytDlpMsg && (
            <span style={{ fontSize: 12, color: ytDlpMsg.err ? "var(--color-error, #ef4444)" : "var(--color-success, #22c55e)" }}>
              {ytDlpMsg.text}
            </span>
          )}
        </div>
        <p className="settings-hint">
          {t("settings.ytDlpHint")}
        </p>

        <label className="settings-row settings-row-toggle">
          <span className="settings-label">{t("settings.openLogOnStart")}</span>
          <div
            className={`settings-toggle${form.openLogOnStart ? " on" : ""}`}
            onClick={() => set("openLogOnStart", !form.openLogOnStart)}
          >
            <div className="settings-toggle-thumb" />
          </div>
        </label>
        <p className="settings-hint">
          {t("settings.openLogHint")}
        </p>
      </div>

    </div>
    </>
  );
};
