import { uiText } from "../i18n/ui";
import React, { useEffect, useState, useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { PlatformState, KickConfig } from "../types/models";
import { IntegrationEvent } from "../hooks/useSignalR";
import { api } from "../services/api";
import { useConfirm } from "../hooks/useConfirm";
interface Props {
    kickEvents: IntegrationEvent[];
    authUpdatedAt?: number;
}
export const PlatformConnections: React.FC<Props> = ({ kickEvents, authUpdatedAt }) => {
    const [platforms, setPlatforms] = useState<PlatformState[]>([]);
    const [loading, setLoading] = useState(true);
    const refresh = useCallback(async () => {
        try {
            const data = await api.getPlatforms();
            setPlatforms(data);
        }
        catch {
            // ignore
        }
        finally {
            setLoading(false);
        }
    }, []);
    useEffect(() => {
        refresh();
        const interval = setInterval(refresh, 3000);
        return () => clearInterval(interval);
    }, [refresh]);
    if (loading)
        return null;
    const kick = platforms.find((p) => p.platform === "kick");
    return (<div className="platforms-grid">
      <KickCard state={kick} onSaved={refresh} events={kickEvents} authUpdatedAt={authUpdatedAt}/>
    </div>);
};
// ── Role selector ─────────────────────────────────────────────────────────────
const KICK_ROLES: {
    id: string;
    labelKey: string;
}[] = [
    { id: "all", labelKey: "platforms.roles.all" },
    { id: "subscriber", labelKey: "platforms.roles.subscriber" },
    { id: "og", labelKey: "platforms.roles.og" },
    { id: "vip", labelKey: "platforms.roles.vip" },
    { id: "moderator", labelKey: "platforms.roles.moderator" },
    { id: "list", labelKey: "platforms.roles.list" },
];
const AllowedUsersEditor: React.FC<{
    users: string[];
    onChange: (users: string[]) => void;
}> = ({ users, onChange }) => {
    const { t } = useTranslation();
    const [draft, setDraft] = useState("");
    const add = () => {
        const v = draft.trim().replace(/^@/, "");
        if (!v)
            return;
        if (users.some(u => u.toLowerCase() === v.toLowerCase())) {
            setDraft("");
            return;
        }
        onChange([...users, v]);
        setDraft("");
    };
    const remove = (u: string) => onChange(users.filter(x => x !== u));
    return (<div className="platform-allowed-users">
      <div className="platform-allowed-chips">
        {users.map(u => (<span key={u} className="platform-chip">
            @{u}
            <button type="button" onClick={() => remove(u)} aria-label="Remove">×</button>
          </span>))}
      </div>
      <div className="platform-allowed-add">
        <input type="text" className="form-input platform-allowed-input" placeholder={t("platforms.addUserPlaceholder")} value={draft} onChange={e => setDraft(e.target.value)} onKeyDown={e => {
            if (e.key === "Enter") {
                e.preventDefault();
                add();
            }
        }}/>
        <button type="button" className="btn btn-sm btn-ghost" onClick={add}>
          {t("platforms.add")}
        </button>
      </div>
    </div>);
};
const RoleSelector: React.FC<{
    roles: string[];
    onChange: (roles: string[]) => void;
}> = ({ roles, onChange }) => {
    const { t } = useTranslation();
    const toggle = (id: string) => {
        if (id === "all") {
            onChange(["all"]);
            return;
        }
        const withoutAll = roles.filter(r => r !== "all");
        const next = withoutAll.includes(id)
            ? withoutAll.filter(r => r !== id)
            : [...withoutAll, id];
        onChange(next.length === 0 ? ["all"] : next);
    };
    return (<div className="platform-roles">
      {KICK_ROLES.map(({ id, labelKey }) => (<label key={id} className={`platform-role-chip ${roles.includes(id) ? "active" : ""}`}>
          <input type="checkbox" checked={roles.includes(id)} onChange={() => toggle(id)}/>
          {t(labelKey)}
        </label>))}
    </div>);
};
function formatRolesSummary(roles: string[], t: TFunction): string {
    if (!roles || roles.length === 0 || roles.includes("all"))
        return t("platforms.roles.all");
    return roles
        .map(r => {
        const match = KICK_ROLES.find(x => x.id === r);
        return match ? t(match.labelKey) : r;
    })
        .join(", ");
}
const PlatformSection: React.FC<{
    title: string;
    summary?: string;
    children: React.ReactNode;
}> = ({ title, summary, children }) => {
    const [open, setOpen] = useState(false);
    return (<div className={`platform-section ${open ? "open" : ""}`}>
      <button type="button" className="platform-section-header" onClick={() => setOpen(!open)}>
        <span className="platform-section-title">{title}</span>
        {summary && !open && <span className="platform-section-summary">{summary}</span>}
        <span className="platform-section-chevron">{open ? "▾" : "▸"}</span>
      </button>
      {open && <div className="platform-section-body">{children}</div>}
    </div>);
};
const EventLog: React.FC<{
    events: IntegrationEvent[];
    isConnected: boolean;
}> = ({ events, isConnected }) => {
    const { t } = useTranslation();
    const [expanded, setExpanded] = useState(false);
    const scrollRef = React.useRef<HTMLDivElement>(null);
    useEffect(() => {
        if (expanded && scrollRef.current) {
            scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
        }
    }, [events, expanded]);
    return (<div className="platform-event-log-wrapper">
      <button type="button" className="platform-event-log-toggle" onClick={() => setExpanded(e => !e)}>
        <span>{t("platforms.chatLog")}</span>
        <span className="platform-event-log-badge">
          {events.length} {events.length === 1 ? t("platforms.msgSingular") : t("platforms.msgPlural")}
        </span>
        <span className="platform-event-log-arrow">{expanded ? "▾" : "▸"}</span>
      </button>

      {expanded && (<div className="platform-event-log" ref={scrollRef}>
          {events.length === 0 ? (<div className="platform-event-log-empty">
              {isConnected ? t("platforms.logEmptyConnected") : t("platforms.logEmptyDisconnected")}
            </div>) : (events.map((ev, i) => (<div key={i} className={`platform-event-item ${ev.success ? "success" : "failure"}`}>
                <span className="platform-event-time">
                  {new Date(ev.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })}
                </span>
                <span className="platform-event-user">@{ev.user}</span>
                <span className="platform-event-msg">{ev.message}</span>
              </div>)))}
        </div>)}
    </div>);
};
const ConnectError: React.FC<{
    message: string;
    onDismiss: () => void;
}> = ({ message, onDismiss }) => {
    return (<div className="platform-connect-error">
      <span>{message}</span>
      <button type="button" onClick={onDismiss} aria-label="Dismiss">×</button>
    </div>);
};
// ── Kick card ─────────────────────────────────────────────────────────────────
const KickCard: React.FC<{
    state?: PlatformState;
    onSaved: () => void;
    events: IntegrationEvent[];
    authUpdatedAt?: number;
}> = ({ state, onSaved, events, authUpdatedAt, }) => {
    const { t } = useTranslation();
    const [confirmModal, confirm] = useConfirm();
    const cfg = state?.config as KickConfig | null;
    const [channel, setChannel] = useState(cfg?.channel ?? "");
    const [autoConnect, setAutoConnect] = useState(state?.autoConnect ?? false);
    const [commandRoles, setCommandRoles] = useState<string[]>(cfg?.commandRoles ?? ["all"]);
    const [allowedUsers, setAllowedUsers] = useState<string[]>(cfg?.allowedUsers ?? []);
    const [connecting, setConnecting] = useState(false);
    const [connectError, setConnectError] = useState<string | null>(null);
    const [kickAuth, setKickAuth] = useState<{
        authenticated: boolean;
        channel: string | null;
    } | null>(null);
    const [authBusy, setAuthBusy] = useState(false);
    // ── Kick Credentials State ────────────────────────────────────────────────
    const [kickClientId, setKickClientId] = useState("");
    const [kickClientSecret, setKickClientSecret] = useState("");
    const [kickRedirectUri, setKickRedirectUri] = useState("http://localhost:3050/api/auth/kick/callback");
    const [showCreds, setShowCreds] = useState(false);
    const [showSecret, setShowSecret] = useState(false);
    const [credSaving, setCredSaving] = useState(false);
    const [credSavedMsg, setCredSavedMsg] = useState<string | null>(null);
    const loadCreds = useCallback(async () => {
        try {
            const c = await api.getCredentials();
            setKickClientId(c.kickClientId ?? "");
            setKickClientSecret(c.kickClientSecret ?? "");
            if (c.kickRedirectUri)
                setKickRedirectUri(c.kickRedirectUri);
            if (!c.kickClientId) {
                setShowCreds(true);
            }
        }
        catch {
            // ignore
        }
    }, []);
    useEffect(() => {
        loadCreds();
    }, [loadCreds]);
    const handleSaveKickCreds = async (andConnect = false) => {
        setCredSaving(true);
        setConnectError(null);
        try {
            await api.updateCredentials({
                kickClientId: kickClientId.trim(),
                kickClientSecret: kickClientSecret.trim(),
            });
            setCredSavedMsg("Kick API bilgileri kaydedildi!");
            setTimeout(() => setCredSavedMsg(null), 3000);
            if (andConnect) {
                setShowCreds(false);
                await handleKickOAuth();
            }
        }
        catch (e: unknown) {
            setConnectError(e instanceof Error ? e.message : "API bilgileri kaydedilemedi.");
        }
        finally {
            setCredSaving(false);
        }
    };
    useEffect(() => {
        if (cfg?.channel && !channel)
            setChannel(cfg.channel);
    }, [cfg]);
    useEffect(() => {
        api.getKickStatus().then(r => {
            setKickAuth(r);
            if (r.authenticated && r.channel) {
                if (!channel)
                    setChannel(r.channel);
                setAuthBusy(false);
                onSaved();
            }
        }).catch(() => setKickAuth({ authenticated: false, channel: null }));
    }, [authUpdatedAt]);
    const saveAndConnect = async (ch: string, roles = commandRoles, users = allowedUsers) => {
        setConnecting(true);
        setConnectError(null);
        try {
            await api.saveKick(ch, autoConnect, roles, users);
            await api.connectPlatform("kick");
            onSaved();
        }
        catch (e) {
            setConnectError(e instanceof Error ? e.message : t("platforms.connectErrorFallback"));
        }
        finally {
            setConnecting(false);
        }
    };
    const handleKickOAuth = async () => {
        setAuthBusy(true);
        setConnectError(null);
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
                    if (!channel && r.channel)
                        setChannel(r.channel);
                    if (ch)
                        await saveAndConnect(ch);
                }
            }, 2000);
            setTimeout(() => { clearInterval(poll); setAuthBusy(false); }, 120000);
        }
        catch (e: unknown) {
            setAuthBusy(false);
            const msg = e instanceof Error ? e.message : uiText("Kick ba\u011Flant\u0131 ba\u015Flat\u0131lamad\u0131.");
            setConnectError(msg);
            setShowCreds(true);
        }
    };
    const handleKickForget = async () => {
        const ok = await confirm({ title: t("platforms.forgetKickTitle"), message: t("platforms.forgetKickMessage"), confirmText: t("platforms.forget"), danger: true });
        if (!ok)
            return;
        await api.forgetPlatform("kick").catch(() => { });
        setKickAuth({ authenticated: false, channel: null });
        setAutoConnect(false);
        onSaved();
    };
    const connect = () => saveAndConnect(channel);
    const disconnect = async () => { await api.disconnectPlatform("kick"); onSaved(); };
    const status = state?.status ?? "disconnected";
    const isAuthed = kickAuth?.authenticated ?? false;
    return (<div className="platform-card">
      {confirmModal}
      <div className="platform-card-header">
        <span className="platform-logo platform-kick-logo">{uiText("Kick")}</span>
        <StatusBadge status={status}/>
      </div>

      {state?.errorMessage && <div className="platform-error">{state.errorMessage}</div>}
      {connectError && <ConnectError message={connectError} onDismiss={() => setConnectError(null)}/>}

      <div className="platform-account-row">
        {isAuthed ? (<>
            <span className="platform-account-name">{kickAuth?.channel ?? t("platforms.connected")}</span>
            <button className="btn btn-sm btn-disconnect platform-account-forget" onClick={handleKickForget}>
              {t("platforms.forget")}
            </button>
          </>) : (<button className="btn btn-sm btn-primary" onClick={handleKickOAuth} disabled={authBusy} style={{ width: "100%" }}>
            {authBusy ? t("platforms.opening") : t("platforms.connectKick")}
          </button>)}
      </div>

      {/* Quick API Credentials toggle / box */}
      <div style={{ marginTop: 6, marginBottom: 6 }}>
        <button type="button" className="btn btn-sm btn-ghost" style={{ fontSize: 11, padding: uiText("5px 10px"), color: "var(--text-dim)", width: "100%", justifyContent: "center" }} onClick={() => setShowCreds(!showCreds)}>
          {showCreds ? uiText("\u25B4 Kick API Bilgilerini Gizle") : (kickClientId ? uiText("\u2699\uFE0F Kick API Bilgilerini D\u00FCzenle") : uiText("\u26A0\uFE0F Kick API Bilgilerini Tan\u0131mla"))}
        </button>

        {showCreds && (<div style={{
                marginTop: 6,
                padding: 10,
                background: "rgba(0,0,0,0.25)",
                border: uiText("1px solid var(--border)"),
                borderRadius: 6,
                display: "flex",
                flexDirection: "column",
                gap: 8,
            }}>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
              <span style={{ fontSize: 12, fontWeight: 600, color: "#53fc18" }}>{uiText("Kick Developer API")}</span>
              <span style={{ fontSize: 10, color: "var(--text-muted)" }}>{uiText("dev.kick.com")}</span>
            </div>

            <label style={{ display: "flex", flexDirection: "column", gap: 2 }}>
              <span style={{ fontSize: 11, color: "var(--text-dim)" }}>{uiText("Client ID")}</span>
              <input type="text" className="input" placeholder={uiText("Kick Client ID")} value={kickClientId} onChange={e => setKickClientId(e.target.value)} style={{ fontSize: 12, padding: uiText("5px 8px") }}/>
            </label>

            <label style={{ display: "flex", flexDirection: "column", gap: 2 }}>
              <span style={{ fontSize: 11, color: "var(--text-dim)" }}>{uiText("Client Secret")}</span>
              <div style={{ display: "flex", gap: 6 }}>
                <input type={showSecret ? "text" : "password"} className="input" placeholder={uiText("Kick Client Secret")} value={kickClientSecret} onChange={e => setKickClientSecret(e.target.value)} style={{ fontSize: 12, padding: uiText("5px 8px"), flex: 1 }}/>
                <button type="button" className="btn btn-sm btn-ghost" onClick={() => setShowSecret(!showSecret)} style={{ fontSize: 11, padding: uiText("3px 8px") }}>
                  {showSecret ? "Gizle" : uiText("G\u00F6ster")}
                </button>
              </div>
            </label>

            <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
              <span style={{ fontSize: 11, color: "var(--text-dim)" }}>{uiText("Redirect URI (dev.kick.com'a yap\u0131\u015Ft\u0131r\u0131n):")}</span>
              <div style={{ display: "flex", gap: 6 }}>
                <input type="text" readOnly value={kickRedirectUri} className="input" style={{ fontSize: 10, padding: uiText("5px 8px"), background: "rgba(0,0,0,0.3)", color: "var(--text-muted)" }}/>
                <button type="button" className="btn btn-sm" style={{ fontSize: 11, padding: uiText("3px 8px"), whiteSpace: "nowrap" }} onClick={() => {
                navigator.clipboard.writeText(kickRedirectUri);
                setCredSavedMsg(uiText("Redirect URI kopyaland\u0131!"));
                setTimeout(() => setCredSavedMsg(null), 2500);
            }}>{uiText("Kopyala")}</button>
              </div>
            </div>

            <div style={{ display: "flex", gap: 6, marginTop: 4, alignItems: "center" }}>
              <button type="button" className="btn btn-sm btn-primary" onClick={() => handleSaveKickCreds(false)} disabled={credSaving}>
                {credSaving ? uiText("Kaydediliyor...") : uiText("Kaydet")}
              </button>
              <button type="button" className="btn btn-sm btn-ghost" onClick={() => handleSaveKickCreds(true)} disabled={credSaving || !kickClientId.trim()}>{uiText("Kaydet ve Ba\u011Flan")}</button>
              {credSavedMsg && (<span style={{ fontSize: 11, color: uiText("var(--color-success, #22c55e)") }}>
                  {credSavedMsg}
                </span>)}
            </div>
          </div>)}
      </div>
      <label className="platform-auto-label-inline">
        <input type="checkbox" checked={autoConnect} onChange={async (e) => {
            setAutoConnect(e.target.checked);
            await api.saveKick(channel, e.target.checked, commandRoles, allowedUsers);
        }}/>
        {t("platforms.connectOnStartupShort")}
      </label>

      <div className="platform-sections">
        <PlatformSection title={t("platforms.permissions")} summary={formatRolesSummary(commandRoles, t)}>
          <RoleSelector roles={commandRoles} onChange={(r) => {
            setCommandRoles(r);
            api.saveKick(channel, autoConnect, r, allowedUsers).catch(() => { });
        }}/>
          {commandRoles.includes("list") && (<AllowedUsersEditor users={allowedUsers} onChange={(u) => {
                setAllowedUsers(u);
                api.saveKick(channel, autoConnect, commandRoles, u).catch(() => { });
            }}/>)}
        </PlatformSection>
      </div>

      <div className="platform-actions">
        {status === "disconnected" || status === "error" ? (<button className="btn btn-sm btn-connect" onClick={connect} disabled={!isAuthed || connecting}>
            {connecting ? t("platforms.connecting") : t("platforms.connectToChat")}
          </button>) : (<button className="btn btn-sm btn-disconnect" onClick={disconnect}>{t("platforms.disconnect")}</button>)}
      </div>

      <EventLog events={events} isConnected={status === "connected"}/>
    </div>);
};
const STATUS_KEYS: Record<string, string> = {
    connected: "platforms.status.connected",
    connecting: "platforms.status.connecting",
    disconnected: "platforms.status.disconnected",
    error: "platforms.status.error",
};
const StatusBadge: React.FC<{
    status: string;
}> = ({ status }) => {
    const { t } = useTranslation();
    return (<span className={`platform-status platform-status-${status}`}>
      <span className="platform-status-dot"/>
      {STATUS_KEYS[status] ? t(STATUS_KEYS[status]) : status}
    </span>);
};
