import React, { useEffect, useState, useCallback, useRef } from "react";
import { PlatformState, TikTokConfig, TwitchConfig, KickConfig } from "../types/models";
import { IntegrationEvent } from "../hooks/useSignalR";
import { api } from "../services/api";
import { useConfirm } from "../hooks/useConfirm";

interface Props {
  tiktokEvents:  IntegrationEvent[];
  twitchEvents:  IntegrationEvent[];
  kickEvents:    IntegrationEvent[];
  authUpdatedAt?: number;
}

export const PlatformConnections: React.FC<Props> = ({ tiktokEvents, twitchEvents, kickEvents, authUpdatedAt }) => {
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
      <TikTokCard state={tiktok} onSaved={refresh} events={tiktokEvents} authUpdatedAt={authUpdatedAt} />
      <TwitchCard state={twitch} onSaved={refresh} events={twitchEvents} authUpdatedAt={authUpdatedAt} />
      <KickCard   state={kick}   onSaved={refresh} events={kickEvents}   authUpdatedAt={authUpdatedAt} />
    </div>
  );
};

// ── Role selector ─────────────────────────────────────────────────────────────

const PLATFORM_ROLES: Record<string, { id: string; label: string }[]> = {
  tiktok: [
    { id: "all",        label: "Todos los usuarios" },
    { id: "follower",   label: "Seguidores" },
    { id: "subscriber", label: "Suscriptores" },
    { id: "moderator",  label: "Moderadores" },
    { id: "teamMember", label: "Team Members" },
    { id: "list",       label: "Usuarios de la lista" },
  ],
  twitch: [
    { id: "all",        label: "Todos los usuarios" },
    { id: "follower",   label: "Seguidores" },
    { id: "subscriber", label: "Suscriptores" },
    { id: "vip",        label: "VIPs" },
    { id: "moderator",  label: "Moderadores" },
    { id: "list",       label: "Usuarios de la lista" },
  ],
  kick: [
    { id: "all",        label: "Todos los usuarios" },
    { id: "subscriber", label: "Suscriptores" },
    { id: "og",         label: "OGs" },
    { id: "vip",        label: "VIPs" },
    { id: "moderator",  label: "Moderadores" },
    { id: "list",       label: "Usuarios de la lista" },
  ],
};

// ── Allowed users editor (chips + add input) ──────────────────────────────────

const AllowedUsersEditor: React.FC<{
  users: string[];
  onChange: (users: string[]) => void;
}> = ({ users, onChange }) => {
  const [draft, setDraft] = useState("");

  const add = () => {
    const v = draft.trim().replace(/^@/, "");
    if (!v) return;
    if (users.some(u => u.toLowerCase() === v.toLowerCase())) { setDraft(""); return; }
    onChange([...users, v]);
    setDraft("");
  };

  const remove = (u: string) => onChange(users.filter(x => x !== u));

  return (
    <div className="allowed-users">
      <div className="allowed-users-chips">
        {users.length === 0 ? (
          <span className="allowed-users-empty">Sin usuarios — agregalos abajo</span>
        ) : users.map(u => (
          <span key={u} className="allowed-user-chip">
            @{u}
            <button type="button" className="allowed-user-remove" onClick={() => remove(u)}>×</button>
          </span>
        ))}
      </div>
      <div className="allowed-users-input">
        <input
          type="text" placeholder="usuario (sin @)"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); add(); } }}
        />
        <button type="button" className="btn btn-sm btn-primary" onClick={add} disabled={!draft.trim()}>+</button>
      </div>
    </div>
  );
};

const RoleSelector: React.FC<{
  platform: string;
  roles: string[];
  onChange: (roles: string[]) => void;
}> = ({ platform, roles, onChange }) => {
  const defs = PLATFORM_ROLES[platform] ?? [];
  const isAll = roles.length === 0 || roles.includes("all");

  const toggle = (id: string) => {
    if (id === "all") {
      if (isAll) {
        // Deseleccionar "Todos" → habilita selección específica con el primer rol como default
        const firstSpecific = defs.find(r => r.id !== "all");
        onChange(firstSpecific ? [firstSpecific.id] : ["all"]);
      } else {
        onChange(["all"]);
      }
      return;
    }
    const current = roles.filter(r => r !== "all");
    const next = current.includes(id) ? current.filter(r => r !== id) : [...current, id];
    onChange(next.length === 0 ? ["all"] : next);
  };

  return (
    <div className="role-list">
      {defs.map(({ id, label }) => (
        <label key={id} className={`role-item${id !== "all" && isAll ? " role-item--dim" : ""}`}>
          <input
            type="checkbox"
            checked={id === "all" ? isAll : roles.includes(id)}
            disabled={id !== "all" && isAll}
            onChange={() => toggle(id)}
          />
          {label}
        </label>
      ))}
    </div>
  );
};

// ── Platform section (collapsible) ────────────────────────────────────────────

const PlatformSection: React.FC<{
  title: string;
  summary?: string;
  defaultOpen?: boolean;
  children: React.ReactNode;
}> = ({ title, summary, defaultOpen = false, children }) => {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className={`platform-section${open ? " platform-section--open" : ""}`}>
      <button type="button" className="platform-section-header" onClick={() => setOpen(o => !o)}>
        <span className="platform-section-chevron">{open ? "▾" : "▸"}</span>
        <span className="platform-section-title">{title}</span>
        {!open && summary && <span className="platform-section-summary">{summary}</span>}
      </button>
      {open && <div className="platform-section-body">{children}</div>}
    </div>
  );
};

// Format helpers for collapsed-state summaries

const ROLE_SHORT: Record<string, string> = {
  all: "Todos", follower: "Seguidores", subscriber: "Subs", moderator: "Mods",
  vip: "VIPs", og: "OGs", teamMember: "Team", list: "Lista",
};

function formatRolesSummary(roles: string[]): string {
  if (roles.length === 0 || roles.includes("all")) return "Todos los usuarios";
  const labels = roles.map(r => ROLE_SHORT[r] ?? r);
  if (labels.length <= 2) return labels.join(" · ");
  return `${labels.slice(0, 2).join(" · ")} · +${labels.length - 2}`;
}

function formatGiftsSummary(bumpEn: boolean, intEn: boolean, threshold: number, coinsPerBump: number): string {
  if (!bumpEn && !intEn) return "Desactivado";
  const parts: string[] = [];
  if (bumpEn) parts.push(`Bump: 1/${coinsPerBump}m`);
  if (intEn) parts.push(`Interrumpir: ${threshold}m`);
  return parts.join(" · ");
}

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

const TikTokCard: React.FC<{ state?: PlatformState; onSaved: () => void; events: IntegrationEvent[]; authUpdatedAt?: number }> = ({
  state, onSaved, events, authUpdatedAt,
}) => {
  const [confirmModal, confirm] = useConfirm();
  const [autoConnect, setAutoConnect] = useState(state?.autoConnect ?? false);
  const cfg = state?.config as TikTokConfig | null;
  const [giftThreshold, setGiftThreshold]             = useState(cfg?.giftInterruptThreshold ?? 100);
  const [giftBumpEnabled, setGiftBumpEnabled]         = useState(cfg?.giftBumpEnabled ?? true);
  const [giftInterruptEnabled, setGiftInterruptEnabled] = useState(cfg?.giftInterruptEnabled ?? true);
  const [coinsPerBump, setCoinsPerBump]               = useState(cfg?.coinsPerBump ?? 1);
  const [commandRoles, setCommandRoles]               = useState<string[]>(cfg?.commandRoles ?? ["all"]);
  const [teamMinLevel, setTeamMinLevel]               = useState(cfg?.teamMinLevel ?? 1);
  const [allowedUsers, setAllowedUsers]               = useState<string[]>(cfg?.allowedUsers ?? []);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);
  const [tiktokAuth, setTiktokAuth] = useState<{ authenticated: boolean; username: string | null } | null>(null);
  const [authBusy, setAuthBusy] = useState(false);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const fetchAuthStatus = useCallback(async () => {
    try {
      const r = await api.getTikTokAuthStatus();
      setTiktokAuth(r);
      return r;
    } catch {
      return { authenticated: false, username: null as string | null, cancelled: false };
    }
  }, []);

  useEffect(() => { fetchAuthStatus(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!authUpdatedAt) return;
    fetchAuthStatus().then(async r => {
      if (r.authenticated) {
        if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
        setAuthBusy(false);
        if (r.username) await api.saveTikTok(r.username, autoConnect, giftThreshold, giftBumpEnabled, giftInterruptEnabled, coinsPerBump, commandRoles, teamMinLevel, allowedUsers).catch(() => {});
        onSaved();
      }
    });
  }, [authUpdatedAt]); // eslint-disable-line react-hooks/exhaustive-deps

  const stopPoll = useCallback(() => {
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
  }, []);

  const handleLogin = async () => {
    setAuthBusy(true);
    try {
      await api.startTikTokLogin();
      pollRef.current = setInterval(async () => {
        const r = await api.getTikTokAuthStatus().catch(() => ({ authenticated: false, username: null as string | null, cancelled: false }));
        if (r.authenticated) {
          stopPoll();
          setAuthBusy(false);
          setTiktokAuth(r);
          if (r.username) await api.saveTikTok(r.username, autoConnect, giftThreshold, giftBumpEnabled, giftInterruptEnabled, coinsPerBump, commandRoles, teamMinLevel, allowedUsers).catch(() => {});
          onSaved();
        } else if (r.cancelled) {
          stopPoll();
          setAuthBusy(false);
        }
      }, 2500);
      setTimeout(() => { stopPoll(); setAuthBusy(false); }, 120_000);
    } catch { setAuthBusy(false); }
  };

  const handleForget = async () => {
    const ok = await confirm({ title: "¿Olvidar cuenta de TikTok?", message: "Tendrás que iniciar sesión de nuevo para volver a conectar.", confirmText: "Olvidar", danger: true });
    if (!ok) return;
    await api.forgetPlatform("tiktok").catch(() => {});
    setTiktokAuth({ authenticated: false, username: null });
    setAutoConnect(false);
    onSaved();
  };

  const save = useCallback((patch?: Partial<{ threshold: number; bumpEn: boolean; intEn: boolean; cpb: number; roles: string[]; teamLevel: number; users: string[] }>) => {
    const u = tiktokAuth?.username;
    if (!u) return;
    const t  = patch?.threshold ?? giftThreshold;
    const be = patch?.bumpEn    ?? giftBumpEnabled;
    const ie = patch?.intEn     ?? giftInterruptEnabled;
    const c  = patch?.cpb       ?? coinsPerBump;
    const r  = patch?.roles     ?? commandRoles;
    const tl = patch?.teamLevel ?? teamMinLevel;
    const au = patch?.users     ?? allowedUsers;
    api.saveTikTok(u, autoConnect, t, be, ie, c, r, tl, au).catch(() => {});
  }, [tiktokAuth, autoConnect, giftThreshold, giftBumpEnabled, giftInterruptEnabled, coinsPerBump, commandRoles, teamMinLevel, allowedUsers]);

  // Auto-save numeric fields (giftThreshold, coinsPerBump, teamMinLevel) on change with 500ms debounce
  const skipFirstAutosave = useRef(true);
  useEffect(() => {
    if (skipFirstAutosave.current) { skipFirstAutosave.current = false; return; }
    if (!tiktokAuth?.username) return;
    const timer = setTimeout(() => save(), 500);
    return () => clearTimeout(timer);
  }, [giftThreshold, coinsPerBump, teamMinLevel]); // eslint-disable-line react-hooks/exhaustive-deps

  const connect = async () => {
    const channel = tiktokAuth?.username;
    if (!channel) return;
    setConnecting(true);
    setConnectError(null);
    try {
      await api.saveTikTok(channel, autoConnect, giftThreshold, giftBumpEnabled, giftInterruptEnabled, coinsPerBump, commandRoles, teamMinLevel, allowedUsers);
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

      <div className="platform-account-row">
        {isAuthed ? (
          <>
            <span className="platform-account-name">@{ttUser ?? "…"}</span>
            <button className="btn btn-sm btn-disconnect platform-account-forget" onClick={handleForget}>
              Olvidar
            </button>
          </>
        ) : (
          <button className="btn btn-sm btn-primary" onClick={handleLogin} disabled={authBusy} style={{ width: "100%" }}>
            {authBusy ? "Abriendo login…" : "Iniciar sesión en TikTok"}
          </button>
        )}
      </div>
      <label className="platform-auto-label-inline">
        <input type="checkbox" checked={autoConnect}
          onChange={async (e) => {
            setAutoConnect(e.target.checked);
            if (ttUser) await api.saveTikTok(ttUser, e.target.checked, giftThreshold, giftBumpEnabled, giftInterruptEnabled, coinsPerBump, commandRoles, teamMinLevel, allowedUsers).catch(() => {});
          }} />
        Conectar al iniciar la app
      </label>

      <div className="platform-sections">
        <PlatformSection title="Permisos" summary={formatRolesSummary(commandRoles)}>
          <RoleSelector
            platform="tiktok"
            roles={commandRoles}
            onChange={(r) => { setCommandRoles(r); save({ roles: r }); }}
          />
          {commandRoles.includes("teamMember") && (
            <div className="role-sub-setting">
              <span className="role-sub-label">Nivel mín. del Team</span>
              <input type="number" className="gift-input" min={1} max={100} value={teamMinLevel}
                onChange={(e) => setTeamMinLevel(Math.max(1, Number(e.target.value)))} />
            </div>
          )}
          {commandRoles.includes("list") && (
            <AllowedUsersEditor
              users={allowedUsers}
              onChange={(u) => { setAllowedUsers(u); save({ users: u }); }}
            />
          )}
        </PlatformSection>

        <PlatformSection title="Regalos" summary={formatGiftsSummary(giftBumpEnabled, giftInterruptEnabled, giftThreshold, coinsPerBump)}>
          <div className="gift-settings">
            <div className="gift-row">
              <label className="gift-toggle">
                <input type="checkbox" checked={giftBumpEnabled}
                  onChange={(e) => { setGiftBumpEnabled(e.target.checked); save({ bumpEn: e.target.checked }); }} />
                Subir posiciones por regalos
              </label>
              {giftBumpEnabled && (
                <div className="gift-input-group">
                  <span className="gift-input-label">Monedas/posición</span>
                  <input type="number" className="gift-input" min={1} value={coinsPerBump}
                    onChange={(e) => setCoinsPerBump(Math.max(1, Number(e.target.value)))} />
                </div>
              )}
            </div>
            <div className="gift-row">
              <label className="gift-toggle">
                <input type="checkbox" checked={giftInterruptEnabled}
                  onChange={(e) => { setGiftInterruptEnabled(e.target.checked); save({ intEn: e.target.checked }); }} />
                Interrumpir canción por regalos
              </label>
              {giftInterruptEnabled && (
                <div className="gift-input-group">
                  <span className="gift-input-label">Umbral (monedas)</span>
                  <input type="number" className="gift-input" min={1} value={giftThreshold}
                    onChange={(e) => setGiftThreshold(Math.max(1, Number(e.target.value)))} />
                </div>
              )}
            </div>
          </div>
        </PlatformSection>
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

const TwitchCard: React.FC<{ state?: PlatformState; onSaved: () => void; events: IntegrationEvent[]; authUpdatedAt?: number }> = ({
  state, onSaved, events, authUpdatedAt,
}) => {
  const [confirmModal, confirm] = useConfirm();
  const [autoConnect, setAutoConnect] = useState(state?.autoConnect ?? false);
  const cfg = state?.config as TwitchConfig | null;
  const [commandRoles, setCommandRoles] = useState<string[]>(cfg?.commandRoles ?? ["all"]);
  const [allowedUsers, setAllowedUsers] = useState<string[]>(cfg?.allowedUsers ?? []);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);
  const [twitchAuth, setTwitchAuth] = useState<{ authenticated: boolean; username: string | null } | null>(null);
  const [authBusy, setAuthBusy] = useState(false);

  useEffect(() => {
    api.getTwitchStatus().then(r => {
      setTwitchAuth(r);
      if (r.authenticated && r.username) {
        setAuthBusy(false);
        onSaved();
      }
    }).catch(() => setTwitchAuth({ authenticated: false, username: null }));
  }, [authUpdatedAt]); // eslint-disable-line react-hooks/exhaustive-deps

  const saveAndConnect = async (username: string, roles = commandRoles, users = allowedUsers) => {
    setConnecting(true);
    setConnectError(null);
    try {
      await api.saveTwitch(username, username, autoConnect, roles, users);
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
      await api.openInBrowser(url);
      const poll = setInterval(async () => {
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
    const ok = await confirm({ title: "¿Olvidar cuenta de Twitch?", message: "Tendrás que autenticarte de nuevo para volver a conectar.", confirmText: "Olvidar", danger: true });
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

      <div className="platform-account-row">
        {isAuthed ? (
          <>
            <span className="platform-account-name">{twitchAuth?.username ?? "Conectado"}</span>
            <button className="btn btn-sm btn-disconnect platform-account-forget" onClick={handleTwitchForget}>
              Olvidar
            </button>
          </>
        ) : (
          <button className="btn btn-sm btn-primary" onClick={handleTwitchOAuth} disabled={authBusy} style={{ width: "100%" }}>
            {authBusy ? "Abriendo..." : "Conectar con Twitch"}
          </button>
        )}
      </div>
      <label className="platform-auto-label-inline">
        <input type="checkbox" checked={autoConnect}
          onChange={async (e) => {
            setAutoConnect(e.target.checked);
            await api.saveTwitch(twitchAuth?.username ?? "", twitchAuth?.username ?? "", e.target.checked, commandRoles, allowedUsers);
          }} />
        Conectar al iniciar
      </label>

      <div className="platform-sections">
        <PlatformSection title="Permisos" summary={formatRolesSummary(commandRoles)}>
          <RoleSelector
            platform="twitch"
            roles={commandRoles}
            onChange={(r) => {
              setCommandRoles(r);
              if (twitchAuth?.username) api.saveTwitch(twitchAuth.username, twitchAuth.username, autoConnect, r, allowedUsers).catch(() => {});
            }}
          />
          {commandRoles.includes("list") && (
            <AllowedUsersEditor
              users={allowedUsers}
              onChange={(u) => {
                setAllowedUsers(u);
                if (twitchAuth?.username) api.saveTwitch(twitchAuth.username, twitchAuth.username, autoConnect, commandRoles, u).catch(() => {});
              }}
            />
          )}
        </PlatformSection>
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

const KickCard: React.FC<{ state?: PlatformState; onSaved: () => void; events: IntegrationEvent[]; authUpdatedAt?: number }> = ({
  state, onSaved, events, authUpdatedAt,
}) => {
  const [confirmModal, confirm] = useConfirm();
  const cfg = state?.config as KickConfig | null;
  const [channel, setChannel] = useState(cfg?.channel ?? "");
  const [autoConnect, setAutoConnect] = useState(state?.autoConnect ?? false);
  const [commandRoles, setCommandRoles] = useState<string[]>(cfg?.commandRoles ?? ["all"]);
  const [allowedUsers, setAllowedUsers] = useState<string[]>(cfg?.allowedUsers ?? []);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);
  const [kickAuth, setKickAuth] = useState<{ authenticated: boolean; channel: string | null } | null>(null);
  const [authBusy, setAuthBusy] = useState(false);

  useEffect(() => {
    if (cfg?.channel && !channel) setChannel(cfg.channel);
  }, [cfg]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    api.getKickStatus().then(r => {
      setKickAuth(r);
      if (r.authenticated && r.channel) {
        if (!channel) setChannel(r.channel);
        setAuthBusy(false);
        onSaved();
      }
    }).catch(() => setKickAuth({ authenticated: false, channel: null }));
  }, [authUpdatedAt]); // eslint-disable-line react-hooks/exhaustive-deps

  const saveAndConnect = async (ch: string, roles = commandRoles, users = allowedUsers) => {
    setConnecting(true);
    setConnectError(null);
    try {
      await api.saveKick(ch, autoConnect, roles, users);
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
      await api.openInBrowser(url);
      const poll = setInterval(async () => {
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
    const ok = await confirm({ title: "¿Olvidar cuenta de Kick?", message: "Tendrás que autenticarte de nuevo para volver a conectar.", confirmText: "Olvidar", danger: true });
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

      <div className="platform-account-row">
        {isAuthed ? (
          <>
            <span className="platform-account-name">{kickAuth?.channel ?? "Conectado"}</span>
            <button className="btn btn-sm btn-disconnect platform-account-forget" onClick={handleKickForget}>
              Olvidar
            </button>
          </>
        ) : (
          <button className="btn btn-sm btn-primary" onClick={handleKickOAuth} disabled={authBusy} style={{ width: "100%" }}>
            {authBusy ? "Abriendo..." : "Conectar con Kick"}
          </button>
        )}
      </div>
      <label className="platform-auto-label-inline">
        <input type="checkbox" checked={autoConnect}
          onChange={async (e) => {
            setAutoConnect(e.target.checked);
            await api.saveKick(channel, e.target.checked, commandRoles, allowedUsers);
          }} />
        Conectar al iniciar
      </label>

      <div className="platform-sections">
        <PlatformSection title="Permisos" summary={formatRolesSummary(commandRoles)}>
          <RoleSelector
            platform="kick"
            roles={commandRoles}
            onChange={(r) => {
              setCommandRoles(r);
              api.saveKick(channel, autoConnect, r, allowedUsers).catch(() => {});
            }}
          />
          {commandRoles.includes("list") && (
            <AllowedUsersEditor
              users={allowedUsers}
              onChange={(u) => {
                setAllowedUsers(u);
                api.saveKick(channel, autoConnect, commandRoles, u).catch(() => {});
              }}
            />
          )}
        </PlatformSection>
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
