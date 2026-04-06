import React, { useEffect, useState, useCallback } from "react";
import { PlatformState, TikTokConfig, TwitchConfig, KickConfig } from "../types/models";
import { IntegrationEvent } from "../hooks/useSignalR";
import { api } from "../services/api";
import { useConfirm } from "../hooks/useConfirm";

interface Props {
  tiktokEvents: IntegrationEvent[];
  twitchEvents: IntegrationEvent[];
  kickEvents:   IntegrationEvent[];
}

export const PlatformConnections: React.FC<Props> = ({ tiktokEvents, twitchEvents, kickEvents }) => {
  const [platforms, setPlatforms] = useState<PlatformState[]>([]);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    try {
      const data = await api.getPlatforms();
      setPlatforms(data);
    } catch {
      // ignore
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    refresh();
    const interval = setInterval(refresh, 3000);
    return () => clearInterval(interval);
  }, [refresh]);

  if (loading) return null;

  const tiktok = platforms.find((p) => p.platform === "tiktok");
  const twitch = platforms.find((p) => p.platform === "twitch");
  const kick   = platforms.find((p) => p.platform === "kick");

  return (
    <div className="platforms-grid">
      <TikTokCard state={tiktok} onSaved={refresh} events={tiktokEvents} />
      <TwitchCard state={twitch} onSaved={refresh} events={twitchEvents} />
      <KickCard   state={kick}   onSaved={refresh} events={kickEvents} />
    </div>
  );
};

// ── Shared event log ─────────────────────────────────────────────────────────

const EventLog: React.FC<{ events: IntegrationEvent[]; isConnected: boolean }> = ({ events, isConnected }) => (
  <div className="event-log-section">
    <div className="event-log-header">
      <span className="event-log-title">Actividad</span>
      {events.length > 0 && <span className="event-log-count">{events.length}</span>}
    </div>
    {events.length > 0 ? (
      <div className="event-log">
        {events.map(r => (
          <div key={r.id} className={`event-entry ${r.success ? "ok" : "fail"} ${r.type === "gift" ? "gift" : ""}`}>
            <span className="entry-icon">{r.type === "gift" ? "🎁" : r.success ? "✓" : "✗"}</span>
            <span className="entry-user">@{r.user}</span>
            <span className="entry-query">{r.query}</span>
            <span className="entry-msg">{r.message}</span>
            <span className="entry-time">
              {r.timestamp.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
            </span>
          </div>
        ))}
      </div>
    ) : (
      <p className="event-empty">
        {isConnected ? <>Esperando comandos <code>!play</code> del chat...</> : "Sin actividad"}
      </p>
    )}
  </div>
);

// ── Inline connect error ──────────────────────────────────────────────────────

const ConnectError: React.FC<{ message: string; onDismiss: () => void }> = ({ message, onDismiss }) => (
  <div className="platform-error" style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
    <span>{message}</span>
    <button onClick={onDismiss} style={{ background: "none", border: "none", cursor: "pointer", color: "inherit", fontSize: 14, padding: "0 4px" }}>✕</button>
  </div>
);

// ── TikTok card ───────────────────────────────────────────────────────────────

const TikTokCard: React.FC<{ state?: PlatformState; onSaved: () => void; events: IntegrationEvent[] }> = ({
  state, onSaved, events,
}) => {
  const [confirmModal, confirm] = useConfirm();
  const [autoConnect, setAutoConnect] = useState(state?.autoConnect ?? false);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);
  const [tiktokAuth, setTiktokAuth] = useState<{ authenticated: boolean; username: string | null } | null>(null);
  const [authBusy, setAuthBusy] = useState(false);

  useEffect(() => {
    api.getTikTokAuthStatus()
      .then(setTiktokAuth)
      .catch(() => setTiktokAuth({ authenticated: false, username: null }));
  }, []);

  const handleLogin = async () => {
    setAuthBusy(true);
    try {
      await api.startTikTokLogin();
      const poll = setInterval(async () => {
        const r = await api.getTikTokAuthStatus().catch(() => ({ authenticated: false, username: null, cancelled: false }));
        if (r.authenticated) {
          clearInterval(poll);
          setAuthBusy(false);
          setTiktokAuth(r);
          if (r.username) await api.saveTikTok(r.username, autoConnect).catch(() => {});
        } else if (r.cancelled) {
          clearInterval(poll);
          setAuthBusy(false);
        }
      }, 1500);
      setTimeout(() => { clearInterval(poll); setAuthBusy(false); }, 120_000);
    } catch { setAuthBusy(false); }
  };

  const handleForget = async () => {
    const ok = await confirm({ message: "¿Olvidar cuenta de TikTok? Tendrás que iniciar sesión de nuevo.", confirmText: "Olvidar", danger: true });
    if (!ok) return;
    await api.forgetPlatform("tiktok").catch(() => {});
    setTiktokAuth({ authenticated: false, username: null });
    setAutoConnect(false);
    onSaved();
  };

  const connect = async () => {
    const channel = tiktokAuth?.username;
    if (!channel) return;
    setConnecting(true);
    setConnectError(null);
    try {
      await api.saveTikTok(channel, autoConnect);
      await api.connectPlatform("tiktok");
      onSaved();
    } catch (e) {
      setConnectError(e instanceof Error ? e.message : "Error al conectar");
    } finally {
      setConnecting(false);
    }
  };

  const disconnect = async () => { await api.disconnectPlatform("tiktok"); onSaved(); };

  const status   = state?.status ?? "disconnected";
  const isAuthed = tiktokAuth?.authenticated ?? false;
  const ttUser   = tiktokAuth?.username;

  return (
    <div className="platform-card">
      {confirmModal}
      <div className="platform-card-header">
        <span className="platform-logo platform-tiktok-logo">TikTok</span>
        <StatusBadge status={status} />
      </div>

      {state?.errorMessage && (
        <div className={status === "connecting" ? "platform-note" : "platform-error"}>
          {state.errorMessage}
        </div>
      )}
      {connectError && <ConnectError message={connectError} onDismiss={() => setConnectError(null)} />}

      <div className="platform-form">
        {isAuthed ? (
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <span style={{ color: "var(--green)", fontWeight: 600, fontSize: 14 }}>
              @{ttUser ?? "…"}
            </span>
            <button className="btn btn-sm btn-disconnect" onClick={handleForget}
              style={{ marginLeft: "auto", fontSize: 11, padding: "2px 8px" }}>
              Olvidar cuenta
            </button>
          </div>
        ) : (
          <button className="btn btn-sm btn-primary" onClick={handleLogin} disabled={authBusy}
            style={{ fontSize: 13, width: "100%" }}>
            {authBusy ? "Abriendo ventana de login…" : "Iniciar sesión en TikTok"}
          </button>
        )}

        <label className="platform-auto-label" style={{ marginTop: 10 }}>
          <input type="checkbox" checked={autoConnect}
            onChange={async (e) => {
              setAutoConnect(e.target.checked);
              if (ttUser) await api.saveTikTok(ttUser, e.target.checked).catch(() => {});
            }} />
          Conectar al iniciar la app
        </label>
      </div>

      <div className="platform-actions">
        {status === "disconnected" ? (
          <button className="btn btn-sm btn-connect" onClick={connect} disabled={!isAuthed || !ttUser || connecting}>
            {connecting ? "Conectando..." : "Conectar al chat"}
          </button>
        ) : (
          <button className="btn btn-sm btn-disconnect" onClick={disconnect}>Detener</button>
        )}
      </div>

      <EventLog events={events} isConnected={status === "connected"} />
    </div>
  );
};

// ── Twitch card ───────────────────────────────────────────────────────────────

const TwitchCard: React.FC<{ state?: PlatformState; onSaved: () => void; events: IntegrationEvent[] }> = ({
  state, onSaved, events,
}) => {
  const [confirmModal, confirm] = useConfirm();
  const [autoConnect, setAutoConnect] = useState(state?.autoConnect ?? false);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);

  const [twitchAuth, setTwitchAuth] = useState<{ authenticated: boolean; username: string | null } | null>(null);
  const [authBusy, setAuthBusy] = useState(false);

  useEffect(() => {
    api.getTwitchStatus().then(setTwitchAuth).catch(() => setTwitchAuth({ authenticated: false, username: null }));
  }, []);

  const saveAndConnect = async (username: string) => {
    setConnecting(true);
    setConnectError(null);
    try {
      await api.saveTwitch(username, username, autoConnect);
      await api.connectPlatform("twitch");
      onSaved();
    } catch (e) {
      setConnectError(e instanceof Error ? e.message : "Error al conectar");
    } finally {
      setConnecting(false);
    }
  };

  const handleTwitchOAuth = async () => {
    setAuthBusy(true);
    try {
      const { url } = await api.getTwitchAuthUrl();
      const popup = window.open(url, "_blank", "width=500,height=700");
      const poll = setInterval(async () => {
        if (popup?.closed) { clearInterval(poll); setAuthBusy(false); return; }
        const r = await api.getTwitchStatus().catch(() => ({ authenticated: false, username: null }));
        if (r.authenticated) {
          clearInterval(poll);
          setAuthBusy(false);
          setTwitchAuth(r);
          if (r.username) await saveAndConnect(r.username);
        }
      }, 2000);
      setTimeout(() => { clearInterval(poll); setAuthBusy(false); }, 120_000);
    } catch { setAuthBusy(false); }
  };

  const handleTwitchForget = async () => {
    const ok = await confirm({ message: "¿Olvidar cuenta de Twitch? Tendrás que autenticarte de nuevo.", confirmText: "Olvidar", danger: true });
    if (!ok) return;
    await api.forgetPlatform("twitch").catch(() => {});
    setTwitchAuth({ authenticated: false, username: null });
    setAutoConnect(false);
    onSaved();
  };

  const connect = () => saveAndConnect(twitchAuth?.username ?? "");
  const disconnect = async () => { await api.disconnectPlatform("twitch"); onSaved(); };

  const status = state?.status ?? "disconnected";
  const isAuthed = twitchAuth?.authenticated ?? false;

  return (
    <div className="platform-card">
      {confirmModal}
      <div className="platform-card-header">
        <span className="platform-logo platform-twitch-logo">Twitch</span>
        <StatusBadge status={status} />
      </div>

      {state?.errorMessage && <div className="platform-error">{state.errorMessage}</div>}
      {connectError && <ConnectError message={connectError} onDismiss={() => setConnectError(null)} />}

      <div className="platform-form">
        <label>Cuenta de Twitch</label>
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          {isAuthed ? (
            <>
              <span style={{ color: "var(--green)", fontWeight: 600, fontSize: 13 }}>
                {twitchAuth?.username ?? "Conectado"}
              </span>
              <button className="btn btn-sm btn-disconnect" onClick={handleTwitchForget}
                style={{ marginLeft: "auto", fontSize: 11, padding: "2px 8px" }}>
                Olvidar cuenta
              </button>
            </>
          ) : (
            <button className="btn btn-sm btn-primary" onClick={handleTwitchOAuth} disabled={authBusy}
              style={{ fontSize: 12 }}>
              {authBusy ? "Abriendo..." : "Conectar con Twitch"}
            </button>
          )}
        </div>

        <label className="platform-auto-label">
          <input type="checkbox" checked={autoConnect}
            onChange={async (e) => { setAutoConnect(e.target.checked); await api.saveTwitch(twitchAuth?.username ?? "", twitchAuth?.username ?? "", e.target.checked); }} />
          Conectar al iniciar
        </label>
      </div>

      <div className="platform-actions">
        {status === "disconnected" || status === "error" ? (
          <button className="btn btn-sm btn-connect" onClick={connect} disabled={!isAuthed || connecting}>
            {connecting ? "Conectando..." : "Conectar al chat"}
          </button>
        ) : (
          <button className="btn btn-sm btn-disconnect" onClick={disconnect}>Desconectar</button>
        )}
      </div>

      <EventLog events={events} isConnected={status === "connected"} />
    </div>
  );
};

// ── Kick card ─────────────────────────────────────────────────────────────────

const KickCard: React.FC<{ state?: PlatformState; onSaved: () => void; events: IntegrationEvent[] }> = ({
  state, onSaved, events,
}) => {
  const [confirmModal, confirm] = useConfirm();
  const cfg = state?.config as KickConfig | null;
  const [channel, setChannel] = useState(cfg?.channel ?? "");
  const [autoConnect, setAutoConnect] = useState(state?.autoConnect ?? false);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);

  const [kickAuth, setKickAuth] = useState<{ authenticated: boolean; channel: string | null } | null>(null);
  const [authBusy, setAuthBusy] = useState(false);

  useEffect(() => {
    if (cfg?.channel && !channel) setChannel(cfg.channel);
  }, [cfg]);

  useEffect(() => {
    api.getKickStatus().then(r => {
      setKickAuth(r);
      if (r.authenticated && r.channel && !channel) setChannel(r.channel);
    }).catch(() => setKickAuth({ authenticated: false, channel: null }));
  }, []);

  const saveAndConnect = async (ch: string) => {
    setConnecting(true);
    setConnectError(null);
    try {
      await api.saveKick(ch, autoConnect);
      await api.connectPlatform("kick");
      onSaved();
    } catch (e) {
      setConnectError(e instanceof Error ? e.message : "Error al conectar");
    } finally {
      setConnecting(false);
    }
  };

  const handleKickOAuth = async () => {
    setAuthBusy(true);
    try {
      const { url } = await api.getKickAuthUrl();
      const popup = window.open(url, "_blank", "width=500,height=700");
      const poll = setInterval(async () => {
        if (popup?.closed) {
          clearInterval(poll);
          setAuthBusy(false);
          return;
        }
        const r = await api.getKickStatus().catch(() => ({ authenticated: false, channel: null }));
        if (r.authenticated) {
          clearInterval(poll);
          setAuthBusy(false);
          setKickAuth(r);
          const ch = channel || r.channel || "";
          if (!channel && r.channel) setChannel(r.channel);
          if (ch) await saveAndConnect(ch);
        }
      }, 2000);
      setTimeout(() => { clearInterval(poll); setAuthBusy(false); }, 120_000);
    } catch { setAuthBusy(false); }
  };

  const handleKickForget = async () => {
    const ok = await confirm({ message: "¿Olvidar cuenta de Kick? Tendrás que autenticarte de nuevo.", confirmText: "Olvidar", danger: true });
    if (!ok) return;
    await api.forgetPlatform("kick").catch(() => {});
    setKickAuth({ authenticated: false, channel: null });
    setAutoConnect(false);
    onSaved();
  };

  const connect = () => saveAndConnect(channel);
  const disconnect = async () => { await api.disconnectPlatform("kick"); onSaved(); };

  const status = state?.status ?? "disconnected";
  const isAuthed = kickAuth?.authenticated ?? false;

  return (
    <div className="platform-card">
      {confirmModal}
      <div className="platform-card-header">
        <span className="platform-logo platform-kick-logo">Kick</span>
        <StatusBadge status={status} />
      </div>

      {state?.errorMessage && <div className="platform-error">{state.errorMessage}</div>}
      {connectError && <ConnectError message={connectError} onDismiss={() => setConnectError(null)} />}

      <div className="platform-form">
        <label>Cuenta de Kick</label>
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          {isAuthed ? (
            <>
              <span style={{ color: "var(--green)", fontWeight: 600, fontSize: 13 }}>
                {kickAuth?.channel ?? "Conectado"}
              </span>
              <button className="btn btn-sm btn-disconnect" onClick={handleKickForget}
                style={{ marginLeft: "auto", fontSize: 11, padding: "2px 8px" }}>
                Olvidar cuenta
              </button>
            </>
          ) : (
            <button className="btn btn-sm btn-primary" onClick={handleKickOAuth} disabled={authBusy}
              style={{ fontSize: 12 }}>
              {authBusy ? "Abriendo..." : "Conectar con Kick"}
            </button>
          )}
        </div>

        <label className="platform-auto-label">
          <input type="checkbox" checked={autoConnect}
            onChange={async (e) => { setAutoConnect(e.target.checked); await api.saveKick(channel, e.target.checked); }} />
          Conectar al iniciar
        </label>
      </div>

      <div className="platform-actions">
        {status === "disconnected" || status === "error" ? (
          <button className="btn btn-sm btn-connect" onClick={connect} disabled={!isAuthed || connecting}>
            {connecting ? "Conectando..." : "Conectar al chat"}
          </button>
        ) : (
          <button className="btn btn-sm btn-disconnect" onClick={disconnect}>Desconectar</button>
        )}
      </div>

      <EventLog events={events} isConnected={status === "connected"} />
    </div>
  );
};

// ── Status badge ─────────────────────────────────────────────────────────────

const STATUS_LABELS: Record<string, string> = {
  connected: "Conectado",
  connecting: "Conectando...",
  disconnected: "Desconectado",
  error: "Error",
};

const StatusBadge: React.FC<{ status: string }> = ({ status }) => (
  <span className={`platform-status platform-status-${status}`}>
    <span className="platform-status-dot" />
    {STATUS_LABELS[status] ?? status}
  </span>
);
