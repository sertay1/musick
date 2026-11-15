import { LoadingState, ErrorState } from "./PanelState";
import { BannedSongsPanel } from "./BannedSongsPanel";
import { SecretField } from "./SecretField";
import { SettingsBackupPanel } from "./SettingsBackupPanel";
import { uiText } from "../i18n/ui";
import React, { useCallback, useEffect, useRef, useState } from "react";
import { useAutoSave } from "../hooks/useAutoSave";
import { api } from "../services/api";
import { QueueSettings } from "../hooks/useSignalR";
import { useConfirm } from "../hooks/useConfirm";
import { UpdatesPanel } from "./UpdatesPanel";
import i18n from "../i18n";
import { Link2, Volume2, Wrench, Check, AlertCircle, Copy, ExternalLink, ChevronDown, ChevronUp, RefreshCw, FolderOpen, FileText, Sliders, ShieldCheck, Headphones, Save, } from "lucide-react";
interface Props {
    settings: QueueSettings;
    onKickUpdated?: () => void;
}
type SettingsTab = "accounts" | "player" | "advanced";
const KICK_ROLES = [
    { id: "all", label: uiText("Herkes (T\u00FCm \u0130zleyiciler)") },
    { id: "subscriber", label: "Aboneler (Subscribers)" },
    { id: "og", label: uiText("OG (\u0130lk \u0130zleyiciler)") },
    { id: "vip", label: uiText("VIP \u0130zleyiciler") },
    { id: "moderator", label: uiText("Moderat\u00F6rler") },
    { id: "list", label: uiText("Yaln\u0131zca \u0130zinli Liste") },
];
export const SettingsPanel: React.FC<Props> = ({ settings, onKickUpdated }) => {
    const [confirmModal, confirm] = useConfirm();
    const [backupBusy, setBackupBusy] = useState(false);
    const [activeTab, setActiveTab] = useState<SettingsTab>("accounts");
    const [audioError, setAudioError] = useState("");
    // ── Form State (QueueSettings) ──
    const [form, setForm] = useState(settings);
    const [saving, setSaving] = useState(false);
    const [saved, setSaved] = useState(false);
    const [loaded, setLoaded] = useState(false);
    const [loadError, setLoadError] = useState("");
    const [credentialsLoaded, setCredentialsLoaded] = useState(false);
    // ── Kick Platform State ──
    const [kickChannel, setKickChannel] = useState("");
    const [kickAutoConnect, setKickAutoConnect] = useState(true);
    const [kickRoles, setKickRoles] = useState<string[]>(["all"]);
    const [kickAllowedUsers, setKickAllowedUsers] = useState<string[]>([]);
    const [kickAuth, setKickAuth] = useState<{
        authenticated: boolean;
        channel: string | null;
    } | null>(null);
    const [kickBusy, setKickBusy] = useState(false);
    const [kickUserDraft, setKickUserDraft] = useState("");
    const [kickSaveMsg, setKickSaveMsg] = useState<string | null>(null);
    // ── Spotify Platform State ──
    const [spotifyConnected, setSpotifyConnected] = useState<boolean | null>(null);
    const [spotifyLibraryAccess, setSpotifyLibraryAccess] = useState<boolean | null>(null);
    const [spotifyBusy, setSpotifyBusy] = useState(false);
    // ── YouTube Auth State ──
    const [ytAuth, setYtAuth] = useState<{
        configured?: boolean;
        enabled: boolean;
        authenticated: boolean;
        account: string | null;
        savedAt: string | null;
    } | null>(null);
    const [ytBusy, setYtBusy] = useState(false);
    // ── Developer API Credentials State ──
    const [kickClientId, setKickClientId] = useState("");
    const [kickClientSecret, setKickClientSecret] = useState("");
    const [kickSecretConfigured, setKickSecretConfigured] = useState(false);
    const [kickRedirectUri, setKickRedirectUri] = useState("http://localhost:3050/api/auth/kick/callback");
    const [spotifyClientId, setSpotifyClientId] = useState("");
    const [spotifyClientSecret, setSpotifyClientSecret] = useState("");
    const [spotifySecretConfigured, setSpotifySecretConfigured] = useState(false);
    const [spotifyRedirectUri, setSpotifyRedirectUri] = useState("http://localhost:3050/api/auth/spotify/callback");
    const [showKickCreds, setShowKickCreds] = useState(false);
    const [showSpotifyCreds, setShowSpotifyCreds] = useState(false);
    const [credSaving, setCredSaving] = useState(false);
    const [credMsg, setCredMsg] = useState<{
        text: string;
        err: boolean;
    } | null>(null);
    const [copyMsg, setCopyMsg] = useState<string | null>(null);
    // ── Audio Devices State ──
    const [audioDevices, setAudioDevices] = useState<{
        id: string;
        name: string;
        isDefault: boolean;
        isActive?: boolean;
        type?: "local" | "spotify";
    }[]>([]);
    const [activeDeviceId, setActiveDeviceId] = useState<string | null>(null);
    const [activeSpotifyDeviceId, setActiveSpotifyDeviceId] = useState<string | null>(null);
    const [devicesLoading, setDevicesLoading] = useState(false);
    // ── Advanced / yt-dlp State ──
    const [ytDlpUpdating, setYtDlpUpdating] = useState(false);
    const [ytDlpMsg, setYtDlpMsg] = useState<{
        text: string;
        err: boolean;
    } | null>(null);
    // ── Load Platforms & Credentials ──
    const kickLoadedRef = useRef(false);
    const kickSavedRef = useRef<{
        channel: string;
        autoConnect: boolean;
        roles: string[];
        allowedUsers: string[];
    }>({
        channel: "",
        autoConnect: true,
        roles: ["all"],
        allowedUsers: [],
    });
    const loadData = useCallback(async () => {
        setLoadError("");
        try {
            const [creds, platforms, spStatus, ytStatus, freshSettings] = await Promise.all([
                api.getCredentials().catch(() => null),
                api.getPlatforms().catch(() => []),
                api.getSpotifyStatus().catch(() => ({ authenticated: false })),
                api.getYouTubeAuthStatus().catch(() => ({ enabled: false, authenticated: false, account: null, savedAt: null })),
                api.getSettings().catch(() => null),
            ]);
            if (freshSettings) {
                setForm(freshSettings);
                lastSavedRef.current = freshSettings;
            }
            if (creds) {
                setCredentialsLoaded(true);
                setKickSecretConfigured(creds.kickClientSecretConfigured);
                setSpotifySecretConfigured(creds.spotifyClientSecretConfigured);
                if (creds.kickClientId)
                    setKickClientId(creds.kickClientId);
                if (creds.kickClientSecret)
                    setKickClientSecret(creds.kickClientSecret);
                if (creds.kickRedirectUri)
                    setKickRedirectUri(creds.kickRedirectUri);
                if (creds.spotifyClientId)
                    setSpotifyClientId(creds.spotifyClientId);
                if (creds.spotifyClientSecret)
                    setSpotifyClientSecret(creds.spotifyClientSecret);
                if (creds.spotifyRedirectUri)
                    setSpotifyRedirectUri(creds.spotifyRedirectUri);
            }
            const kick = platforms.find(p => p.platform === "kick");
            if (kick) {
                const cfg = kick.config as {
                    channel?: string;
                    commandRoles?: string[];
                    allowedUsers?: string[];
                };
                const ch = cfg?.channel || "";
                const roles = cfg?.commandRoles || ["all"];
                const users = cfg?.allowedUsers || [];
                const auto = true;
                if (ch)
                    setKickChannel(ch);
                if (roles.length > 0)
                    setKickRoles(roles);
                if (users)
                    setKickAllowedUsers(users);
                setKickAutoConnect(true);
                kickSavedRef.current = {
                    channel: ch,
                    autoConnect: auto,
                    roles,
                    allowedUsers: users,
                };
            }
            setSpotifyConnected(spStatus.authenticated);
            setSpotifyLibraryAccess("libraryAccess" in spStatus ? spStatus.libraryAccess ?? null : null);
            setYtAuth(ytStatus);
            const kAuth = await api.getKickStatus().catch(() => ({ authenticated: false, channel: null }));
            setKickAuth(kAuth);
            kickLoadedRef.current = true;
            setLoaded(true);
        }
        catch (e) {
            setLoadError(uiText("Ayarlar alınamadı. Bağlantıyı kontrol edip yeniden deneyin."));
            console.error("Failed to load initial settings data", e);
        }
    }, []);
    useEffect(() => {
        loadData();
    }, [loadData]);
    const audioRequest = useRef(0);
    const loadDevices = async () => {
        const requestId = ++audioRequest.current;
        setDevicesLoading(true);
        try {
            const res = await api.getAudioDevices(true);
            if (requestId !== audioRequest.current)
                return;
            setAudioDevices(res.devices || []);
            setActiveDeviceId(res.activeDeviceId);
            setActiveSpotifyDeviceId(res.activeSpotifyDeviceId);
        }
        catch {
            // ignore
        }
        finally {
            if (requestId === audioRequest.current)
                setDevicesLoading(false);
        }
    };
    useEffect(() => {
        if (activeTab === "player") {
            loadDevices();
        }
    }, [activeTab]);
    useEffect(() => {
        const refresh = () => {
            if (activeTab === "player")
                void loadDevices();
        };
        window.addEventListener("player:devices-changed", refresh);
        return () => window.removeEventListener("player:devices-changed", refresh);
    }, [activeTab]);
    const lastSavedRef = useRef<QueueSettings>(settings);
    const queueSave = useAutoSave(form, async (value) => { await api.updateSettings(value); lastSavedRef.current = value; }, loaded && !backupBusy);
    const kickSave = useAutoSave({ channel: kickChannel.trim().replace(/^@/, ""), auto: kickAutoConnect, roles: kickRoles, users: kickAllowedUsers }, async (value) => {
        if (!value.channel)
            throw new Error(uiText("Kanal ad\u0131 gerekli"));
        await api.saveKick(value.channel, value.auto, value.roles, value.users);
        onKickUpdated?.();
    }, loaded && !backupBusy);
    const credentialSave = useAutoSave({ kickClientId, kickClientSecret, spotifyClientId, spotifyClientSecret }, async value => { await api.updateCredentials({ ...value, kickClientSecret: value.kickClientSecret.trim() || undefined, spotifyClientSecret: value.spotifyClientSecret.trim() || undefined }); if (value.kickClientSecret.trim()) setKickSecretConfigured(true); if (value.spotifyClientSecret.trim()) setSpotifySecretConfigured(true); }, credentialsLoaded && !backupBusy);
    const saveError = [queueSave.status, kickSave.status, credentialSave.status].includes("error");
    useEffect(() => {
        setSaving([queueSave.status, kickSave.status, credentialSave.status].includes("saving"));
        setSaved([queueSave.status, kickSave.status, credentialSave.status].includes("saved"));
    }, [queueSave.status, kickSave.status, credentialSave.status]);
    const setSetting = (key: keyof QueueSettings, value: number | boolean | string) => setForm(f => ({ ...f, [key]: value }));
    const copyText = (text: string, label: string) => {
        navigator.clipboard.writeText(text);
        setCopyMsg(uiText("{{v0}} kopyaland\u0131!", { v0: label }));
        setTimeout(() => setCopyMsg(null), 2500);
    };
    // ── Handle Kick Actions ──
    const handleSaveKickPlatform = async () => {
        const ch = kickChannel.trim().replace(/^@/, "");
        if (!ch) {
            alert(uiText("L\u00FCtfen ge\u00E7erli bir Kick kanal ad\u0131 girin."));
            return;
        }
        setKickBusy(true);
        try {
            await api.saveKick(ch, kickAutoConnect, kickRoles, kickAllowedUsers);
            kickSavedRef.current = { channel: ch, autoConnect: kickAutoConnect, roles: kickRoles, allowedUsers: kickAllowedUsers };
            await api.connectPlatform("kick");
            setKickSaveMsg(uiText("Kick kanal ve izin ayarlar\u0131 ba\u015Far\u0131yla kaydedildi!"));
            setTimeout(() => setKickSaveMsg(null), 3000);
            if (onKickUpdated)
                onKickUpdated();
        }
        catch (e: unknown) {
            alert(e instanceof Error ? e.message : uiText("Kick ayarlar\u0131 kaydedilemedi."));
        }
        finally {
            setKickBusy(false);
        }
    };
    const handleKickConnectOAuth = async () => {
        setKickBusy(true);
        try {
            const { url } = await api.getKickAuthUrl();
            await api.openInBrowser(url);
            const poll = setInterval(async () => {
                const r = await api.getKickStatus().catch(() => ({ authenticated: false, channel: null }));
                if (r.authenticated) {
                    clearInterval(poll);
                    setKickAuth(r);
                    const ch = kickChannel.trim().replace(/^@/, "") || r.channel || "";
                    if (ch) {
                        setKickChannel(ch);
                        await api.saveKick(ch, true, kickRoles, kickAllowedUsers);
                        await api.connectPlatform("kick");
                    }
                    setKickBusy(false);
                    if (onKickUpdated)
                        onKickUpdated();
                }
            }, 2000);
            setTimeout(() => { clearInterval(poll); setKickBusy(false); }, 120000);
        }
        catch (e: unknown) {
            setKickBusy(false);
            alert(e instanceof Error ? e.message : uiText("Kick ba\u011Flant\u0131s\u0131 ba\u015Flat\u0131lamad\u0131. L\u00FCtfen Kick API Client ID ve Secret bilgilerini kontrol edin."));
            setShowKickCreds(true);
        }
    };
    const handleKickDisconnect = async () => {
        const ok = await confirm({
            title: uiText("Kick Ba\u011Flant\u0131s\u0131n\u0131 Kes"),
            message: uiText("Kick botunun sohbet ba\u011Flant\u0131s\u0131 kesilecek. Onayl\u0131yor musunuz?"),
            confirmText: uiText("Ba\u011Flant\u0131y\u0131 Kes"),
            danger: true,
        });
        if (!ok)
            return;
        setKickBusy(true);
        try {
            await api.disconnectPlatform("kick");
            await api.forgetPlatform("kick");
            setKickAuth({ authenticated: false, channel: null });
            if (onKickUpdated)
                onKickUpdated();
        }
        finally {
            setKickBusy(false);
        }
    };
    // ── Handle Spotify Actions ──
    const handleSpotifyConnect = async () => {
        setSpotifyBusy(true);
        try {
            const { url } = await api.getSpotifyAuthUrl();
            await api.openInBrowser(url);
            const poll = setInterval(async () => {
                const r = await api.getSpotifyStatus().catch(() => ({ authenticated: false }));
                if (r.authenticated && (!("libraryAccess" in r) || r.libraryAccess !== false)) {
                    setSpotifyLibraryAccess("libraryAccess" in r ? r.libraryAccess ?? null : null);
                    setSpotifyConnected(true);
                    clearInterval(poll);
                    setSpotifyBusy(false);
                }
            }, 2000);
            setTimeout(() => { clearInterval(poll); setSpotifyBusy(false); }, 120000);
        }
        catch {
            setSpotifyBusy(false);
            setShowSpotifyCreds(true);
        }
    };
    const handleSpotifyDisconnect = async () => {
        const ok = await confirm({
            title: uiText("Spotify Ba\u011Flant\u0131s\u0131n\u0131 Kes"),
            message: uiText("Spotify hesab\u0131n\u0131z\u0131n ba\u011Flant\u0131s\u0131 kald\u0131r\u0131lacak. Onayl\u0131yor musunuz?"),
            confirmText: uiText("Ba\u011Flant\u0131y\u0131 Kes"),
            danger: true,
        });
        if (!ok)
            return;
        setSpotifyBusy(true);
        try {
            await api.disconnectSpotify();
            setSpotifyConnected(false);
        }
        finally {
            setSpotifyBusy(false);
        }
    };
    // ── Handle YouTube Actions ──
    const handleYouTubeConnect = async () => {
        setYtBusy(true);
        try {
            await api.startYouTubeLogin();
            const poll = setInterval(async () => {
                const r = await api.getYouTubeAuthStatus().catch(() => null);
                if (r?.authenticated) {
                    setYtAuth({ configured: r.configured, enabled: r.enabled, authenticated: true, account: r.account, savedAt: r.savedAt });
                    clearInterval(poll);
                    setYtBusy(false);
                }
                else if (r?.error || r?.cancelled) {
                    if (r.error) setCredMsg({ text: r.error, err: true });
                    clearInterval(poll);
                    setYtBusy(false);
                }
            }, 1500);
            setTimeout(() => { clearInterval(poll); setYtBusy(false); }, 180000);
        }
        catch (error) {
            setCredMsg({ text: error instanceof Error ? error.message : uiText("YouTube bağlantısı başlatılamadı."), err: true });
            setYtBusy(false);
        }
    };
    const handleYouTubeDisconnect = async () => {
        const ok = await confirm({
            title: "YouTube Oturumunu Kapat",
            message: uiText("Kay\u0131tl\u0131 YouTube \u00E7erezleri ve oturumu temizlenecek."),
            confirmText: uiText("Oturumu Kapat"),
            danger: true,
        });
        if (!ok)
            return;
        setYtBusy(true);
        try {
            await api.disconnectYouTubeAuth();
            const r = await api.getYouTubeAuthStatus().catch(() => null);
            setYtAuth(r);
        }
        finally {
            setYtBusy(false);
        }
    };
    const handleYouTubeToggle = async () => {
        if (!ytAuth)
            return;
        setYtBusy(true);
        try {
            if (ytAuth.enabled)
                await api.disableYouTubeAuth();
            else
                await api.enableYouTubeAuth();
            const r = await api.getYouTubeAuthStatus().catch(() => null);
            setYtAuth(r);
        }
        finally {
            setYtBusy(false);
        }
    };
    // ── Handle Save Credentials ──
    const handleSaveCredentials = async () => {
        setCredSaving(true);
        setCredMsg(null);
        try {
            await api.updateCredentials({
                kickClientId: kickClientId.trim(),
                kickClientSecret: kickClientSecret.trim() || undefined,
                spotifyClientId: spotifyClientId.trim(),
                spotifyClientSecret: spotifyClientSecret.trim() || undefined,
            });
            if (kickClientSecret.trim()) setKickSecretConfigured(true);
            if (spotifyClientSecret.trim()) setSpotifySecretConfigured(true);
            setCredMsg({ text: uiText("Geli\u015Ftirici API bilgileri ba\u015Far\u0131yla kaydedildi! (appsettings.user.json)"), err: false });
            setTimeout(() => setCredMsg(null), 4000);
        }
        catch (e: unknown) {
            setCredMsg({ text: e instanceof Error ? e.message : "API bilgileri kaydedilemedi.", err: true });
        }
        finally {
            setCredSaving(false);
        }
    };
    // ── Handle Audio Device Selection ──
    const handleSelectDevice = async (dev: {
        id: string;
        type?: "local" | "spotify";
    }) => {
        const oldLocal = activeDeviceId, oldSpotify = activeSpotifyDeviceId;
        if (dev.type === "spotify")
            setActiveSpotifyDeviceId(dev.id);
        else
            setActiveDeviceId(dev.id);
        setAudioDevices(items => items.map(d => d.type === dev.type ? { ...d, isActive: d.id === dev.id } : d));
        try {
            setAudioError("");
            const result = await api.setAudioDevice(dev.id, dev.type);
            await loadDevices();
            if (result.warning)
                setAudioError(result.warning);
        }
        catch (e) {
            setActiveDeviceId(oldLocal);
            setActiveSpotifyDeviceId(oldSpotify);
            setAudioError(e instanceof Error ? e.message : uiText("Ses \u00E7\u0131k\u0131\u015F\u0131 de\u011Fi\u015Ftirilemedi"));
        }
    };
    // ── Handle yt-dlp Update ──
    const handleUpdateYtDlp = async () => {
        setYtDlpUpdating(true);
        setYtDlpMsg(null);
        try {
            const r = await api.updateYtDlp();
            setYtDlpMsg({ text: r.message, err: false });
        }
        catch (e: unknown) {
            setYtDlpMsg({ text: e instanceof Error ? e.message : uiText("yt-dlp g\u00FCncellenirken hata olu\u015Ftu."), err: true });
        }
        finally {
            setYtDlpUpdating(false);
        }
    };
    const addAllowedUser = () => {
        const u = kickUserDraft.trim().replace(/^@/, "");
        if (!u)
            return;
        if (kickAllowedUsers.some(x => x.toLowerCase() === u.toLowerCase())) {
            setKickUserDraft("");
            return;
        }
        setKickAllowedUsers([...kickAllowedUsers, u]);
        setKickUserDraft("");
    };
    const removeAllowedUser = (u: string) => {
        setKickAllowedUsers(kickAllowedUsers.filter(x => x.toLowerCase() !== u.toLowerCase()));
    };
    const toggleKickRole = (id: string) => {
        if (id === "all") {
            setKickRoles(["all"]);
            return;
        }
        const filtered = kickRoles.filter(r => r !== "all");
        const next = filtered.includes(id) ? filtered.filter(r => r !== id) : [...filtered, id];
        setKickRoles(next.length === 0 ? ["all"] : next);
    };
    if (!loaded) return loadError ? <ErrorState message={loadError} retry={() => void loadData()}/> : <LoadingState label={uiText("Ayarlar yükleniyor…")}/>;
    return (<>
      {confirmModal}
      <fieldset disabled={backupBusy} style={{ border: 0, margin: 0, padding: 0, minWidth: 0, display: "contents" }}>
      <div className="settings-panel" style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        {saveError && <button className="btn btn-sm" onClick={() => { queueSave.retry(); kickSave.retry(); credentialSave.retry(); }}>{uiText("Kayd\u0131 tekrar dene")}</button>}
        {/* ── Auto-save Status Bar ── */}
        <div className="studio-save-status" style={{
            padding: uiText("8px 14px"),
            borderRadius: 8,
            fontSize: 12,
            fontWeight: 500,
            display: "flex",
            justifyContent: "space-between",
            alignItems: "center",
            background: uiText("var(--surface2, rgba(255,255,255,0.03))"),
            border: uiText("1px solid var(--border)"),
        }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <span style={{ color: saving ? "#f59e0b" : saved ? "var(--green)" : "var(--text-muted)" }}>●</span>
            <span>
              {saveError ? uiText("Baz\u0131 de\u011Fi\u015Fiklikler kaydedilemedi.") : saving ? uiText("Ayarlar otomatik kaydediliyor...") : saved ? uiText("De\u011Fi\u015Fiklikler kaydedildi!") : uiText("Ayarlar otomatik olarak kaydedilir.")}
            </span>
          </div>

          {copyMsg && (<span style={{ color: "var(--green)", fontWeight: 600 }}>{copyMsg}</span>)}
        </div>

        {/* ── Settings tabs ── */}
        <div className="studio-tabs" style={{
            display: "flex",
            flexWrap: "wrap",
            gap: 6,
            borderBottom: uiText("1px solid var(--border)"),
            paddingBottom: 10,
        }}>
          <button type="button" className={`btn btn-sm ${activeTab === "accounts" ? "btn-primary" : "btn-ghost"}`} onClick={() => setActiveTab("accounts")} style={{ fontWeight: 600, display: "flex", alignItems: "center", gap: 6 }}>
            <Link2 size={15}/>
            <span>{uiText("Hesaplar")}</span>
          </button>

          <button type="button" className={`btn btn-sm ${activeTab === "player" ? "btn-primary" : "btn-ghost"}`} onClick={() => setActiveTab("player")} style={{ fontWeight: 600, display: "flex", alignItems: "center", gap: 6 }}>
            <Volume2 size={15}/>
            <span>{uiText("Oynatıcı ve ses")}</span>
          </button>

          <button type="button" className={`btn btn-sm ${activeTab === "advanced" ? "btn-primary" : "btn-ghost"}`} onClick={() => setActiveTab("advanced")} style={{ fontWeight: 600, display: "flex", alignItems: "center", gap: 6 }}>
            <Wrench size={15}/>
            <span>{uiText("Sistem")}</span>
          </button>
        </div>

        {/* ════════════════════════════════════════════════════════════════════
            KATEGORİ 1: HESAP BAĞLANTILARI
           ════════════════════════════════════════════════════════════════════ */}
        {activeTab === "accounts" && (<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            {/* Kick Hesap Kartı */}
            <div className="studio-panel" style={{
                background: "rgba(83, 252, 24, 0.02)",
                border: uiText("1px solid rgba(83, 252, 24, 0.2)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 12,
            }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                  <span style={{ fontSize: 18, fontWeight: 800, color: "var(--green)" }}>{uiText("Kick")}</span>
                  <span style={{
                fontSize: 11,
                fontWeight: 700,
                padding: uiText("2px 8px"),
                borderRadius: 4,
                background: kickAuth?.authenticated ? "rgba(83, 252, 24, 0.15)" : "rgba(239, 68, 68, 0.15)",
                color: kickAuth?.authenticated ? "var(--green)" : "var(--red)",
            }}>
                    {kickAuth?.authenticated ? uiText("\uD83D\uDFE2 Ba\u011Fl\u0131: @{{v0}}", { v0: kickAuth.channel || kickChannel || uiText("Yay\u0131nc\u0131") }) : uiText("\uD83D\uDD34 Ba\u011Fl\u0131 De\u011Fil")}
                  </span>
                </div>

                <div style={{ display: "flex", gap: 8 }}>
                  {kickAuth?.authenticated ? (<button type="button" className="btn btn-sm btn-ghost" style={{ color: "var(--red)" }} onClick={handleKickDisconnect} disabled={kickBusy}>
                      {kickBusy ? uiText("\u0130\u015Fleniyor...") : uiText("Ba\u011Flant\u0131y\u0131 Kes")}
                    </button>) : (<button type="button" className="btn btn-sm btn-primary" onClick={handleKickConnectOAuth} disabled={kickBusy}>
                      {kickBusy ? uiText("Pencere A\u00E7\u0131l\u0131yor...") : uiText("Kick Hesab\u0131n\u0131 Ba\u011Fla (Giri\u015F Yap)")}
                    </button>)}
                </div>
              </div>

              <label className="kick-channel-field">
                <span>{uiText("Kick kanal adı")}</span>
                <input type="text" className="input" placeholder="ornek_yayinci" value={kickChannel} onChange={e => setKickChannel(e.target.value)} aria-describedby="kick-channel-help" autoComplete="off" spellCheck={false}/>
              </label>
              <p id="kick-channel-help" className="kick-account-help">{uiText("Musick’i hangi yayın sohbetinde kullanacaksanız o kanalın kullanıcı adını yazın. Kanal adı otomatik kaydedilir.")}</p>
              <p className="kick-account-help">{uiText("Kick hesabını bağlarken sohbete mesaj göndermesini istediğiniz hesapla giriş yapın. Yayıncı hesabınızı veya ayrı bir bot hesabını kullanabilirsiniz.")}</p>

              {/* Collapsible Kick Developer API Box */}
              <div style={{ borderTop: uiText("1px solid rgba(255,255,255,0.06)"), paddingTop: 10 }}>
                <button type="button" className="btn btn-sm btn-ghost" onClick={() => setShowKickCreds(!showKickCreds)} style={{ fontSize: 11, color: "var(--text-muted)", padding: uiText("4px 8px") }}>
                  {showKickCreds ? uiText("\u25B2 Kick Geli\u015Ftirici API Bilgilerini Gizle") : uiText("\u2699\uFE0F Kick Geli\u015Ftirici API Bilgileri (Client ID / Secret)")}
                </button>

                {showKickCreds && (<div style={{ marginTop: 10, display: "flex", flexDirection: "column", gap: 10, background: "rgba(0,0,0,0.25)", padding: 12, borderRadius: 6 }}>
                    <div className="kick-api-help">
                      <p>{uiText("Kick’te bir uygulama oluşturup Client ID ve Client Secret bilgilerini buraya girin.")}</p>
                      <a href="https://kick.com/settings/developer" target="_blank" rel="noopener noreferrer"><ExternalLink size={13} aria-hidden="true"/>{uiText("Kick Geliştirici Ayarları")}</a>
                      <p>{uiText("Gerekli izinler")}</p>
                      <ul>
                        <li><code>user:read</code> · {uiText("Bağlanan hesabın bilgilerini okumak")}</li>
                        <li><code>chat:write</code> · {uiText("Sohbete mesaj göndermek")}</li>
                      </ul>
                      <p>{uiText("Aşağıdaki Redirect URI’yi Kick uygulamasının yönlendirme adresi olarak ekleyin.")}</p>
                    </div>

                    <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Kick Client ID")}</span>
                      <input type="text" className="input" placeholder={uiText("Kick Client ID")} value={kickClientId} onChange={e => setKickClientId(e.target.value)} style={{ fontSize: 12 }}/>
                    </label>

                    <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Kick Client Secret")} {kickSecretConfigured && <span style={{ color: "var(--accent)", marginLeft: 8 }}>✓ {uiText("Kayıtlı")}</span>}</span>
                      <SecretField platform="kick" value={kickClientSecret} configured={kickSecretConfigured} onChange={setKickClientSecret}/>
                      {kickSecretConfigured && <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("Anahtarınız kayıtlı. Değiştirmek için yeni anahtarı yazın.")}</span>}
                    </label>

                    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Kick Redirect URI")}</span>
                      <div style={{ display: "flex", gap: 8 }}>
                        <input type="text" readOnly className="input" value={kickRedirectUri} style={{ fontSize: 11, color: "var(--text-muted)", background: "rgba(0,0,0,0.3)" }}/>
                        <button type="button" className="btn btn-sm btn-ghost" onClick={() => copyText(kickRedirectUri, uiText("Kick Redirect URI"))}>
                          <Copy size={13} style={{ marginRight: 4 }}/>{uiText("Kopyala")}</button>
                      </div>
                    </div>

                    <button type="button" className="btn btn-sm btn-primary" onClick={handleSaveCredentials} disabled={credSaving} style={{ alignSelf: "flex-start", marginTop: 4 }}>
                      {credSaving ? uiText("Kaydediliyor...") : uiText("Kick API Bilgilerini Kaydet")}
                    </button>
                  </div>)}
              </div>
            </div>

            {/* Spotify Hesap Kartı */}
            {spotifyConnected && spotifyLibraryAccess === false && <div role="alert" style={{ padding: 14, border: uiText("1px solid #f59e0b"), borderRadius: 8, color: "#f59e0b" }}><p>{uiText("Spotify ba\u011Fl\u0131, ancak mevcut oturum be\u011Feni kitapl\u0131\u011F\u0131 izni ta\u015F\u0131m\u0131yor. Yeni izinleri almak i\u00E7in Spotify ba\u011Flant\u0131s\u0131n\u0131 yenileyin.")}</p><button className="btn btn-secondary" onClick={handleSpotifyConnect} disabled={spotifyBusy}>{uiText("Spotify izinlerini yenile")}</button></div>}
            <div className="studio-panel" style={{
                background: "rgba(30, 215, 96, 0.02)",
                border: uiText("1px solid rgba(30, 215, 96, 0.2)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 12,
            }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                  <span style={{ fontSize: 18, fontWeight: 800, color: "var(--green)" }}>{uiText("Spotify")}</span>
                  <span style={{
                fontSize: 11,
                fontWeight: 700,
                padding: uiText("2px 8px"),
                borderRadius: 4,
                background: spotifyConnected ? "rgba(30, 215, 96, 0.15)" : "rgba(239, 68, 68, 0.15)",
                color: spotifyConnected ? "var(--green)" : "var(--red)",
            }}>
                    {spotifyConnected ? uiText("\uD83D\uDFE2 Ba\u011Fl\u0131 & Senkronize") : uiText("\uD83D\uDD34 Ba\u011Fl\u0131 De\u011Fil")}
                  </span>
                </div>

                <div style={{ display: "flex", gap: 8 }}>
                  {spotifyConnected ? (<button type="button" className="btn btn-sm btn-ghost" style={{ color: "var(--red)" }} onClick={handleSpotifyDisconnect} disabled={spotifyBusy}>
                      {spotifyBusy ? uiText("\u0130\u015Fleniyor...") : uiText("Ba\u011Flant\u0131y\u0131 Kes")}
                    </button>) : (<button type="button" className="btn btn-sm btn-primary" onClick={handleSpotifyConnect} disabled={spotifyBusy}>
                      {spotifyBusy ? uiText("Pencere A\u00E7\u0131l\u0131yor...") : uiText("Spotify Hesab\u0131n\u0131 Ba\u011Fla (Giri\u015F Yap)")}
                    </button>)}
                </div>
              </div>

              <p style={{ margin: 0, fontSize: 12, color: "var(--text-muted)" }}>{uiText("Spotify \u00E7alma listelerinizi i\u00E7e aktarmak, \u015Fark\u0131 be\u011Fenilerini senkronize etmek ve Spotify \u00FCzerinden \u015Fark\u0131 \u00E7almak i\u00E7in ba\u011Flay\u0131n.")}</p>

              <div className="studio-help" style={{
                fontSize: 11,
                lineHeight: 1.45,
                padding: uiText("8px 10px"),
                background: "rgba(30, 215, 96, 0.08)",
                border: uiText("1px solid rgba(30, 215, 96, 0.2)"),
                borderRadius: 6,
                color: "var(--text-main)",
            }}>
                💡 <strong>{uiText("Be\u011Fenilen \u015Eark\u0131lar & \u00C7alma Listeleri Senkronizasyonu:")}</strong>{uiText("Uygulama i\u00E7i \u015Fark\u0131 be\u011Fendi\u011Finizde Spotify \"Be\u011Fenilen \u015Eark\u0131lar\"a otomatik eklenmesi ve \u00E7alma listelerinizin sorunsuz e\u015Fitlenebilmesi i\u00E7in en g\u00FCncel izinler eklenmi\u015Ftir. Daha \u00F6nce ba\u011Flad\u0131ysan\u0131z l\u00FCtfen bir kez")}<em>{uiText("Ba\u011Flant\u0131y\u0131 Kes")}</em>{uiText("yap\u0131p tekrar giri\u015F yap\u0131n.")}</div>

              {/* Collapsible Spotify Developer API Box */}
              <div style={{ borderTop: uiText("1px solid rgba(255,255,255,0.06)"), paddingTop: 10 }}>
                <button type="button" className="btn btn-sm btn-ghost" onClick={() => setShowSpotifyCreds(!showSpotifyCreds)} style={{ fontSize: 11, color: "var(--text-muted)", padding: uiText("4px 8px") }}>
                  {showSpotifyCreds ? uiText("\u25B2 Spotify Geli\u015Ftirici API Bilgilerini Gizle") : uiText("\u2699\uFE0F Spotify Geli\u015Ftirici API Bilgileri (Client ID / Secret)")}
                </button>

                {showSpotifyCreds && (<div style={{ marginTop: 10, display: "flex", flexDirection: "column", gap: 10, background: "rgba(0,0,0,0.25)", padding: 12, borderRadius: 6 }}>
                    <div style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("developer.spotify.com Dashboard portal\u0131ndaki uygulaman\u0131z\u0131n bilgilerini girin.")}</div>

                    <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Spotify Client ID")}</span>
                      <input type="text" className="input" placeholder={uiText("Spotify Client ID")} value={spotifyClientId} onChange={e => setSpotifyClientId(e.target.value)} style={{ fontSize: 12 }}/>
                    </label>

                    <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Spotify Client Secret")} {spotifySecretConfigured && <span style={{ color: "var(--accent)", marginLeft: 8 }}>✓ {uiText("Kayıtlı")}</span>}</span>
                      <SecretField platform="spotify" value={spotifyClientSecret} configured={spotifySecretConfigured} onChange={setSpotifyClientSecret}/>
                      {spotifySecretConfigured && <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("Anahtarınız kayıtlı. Değiştirmek için yeni anahtarı yazın.")}</span>}
                    </label>

                    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Spotify Redirect URI (Spotify Dashboard'a girilecek)")}</span>
                      <div style={{ display: "flex", gap: 8 }}>
                        <input type="text" readOnly className="input" value={spotifyRedirectUri} style={{ fontSize: 11, color: "var(--text-muted)", background: "rgba(0,0,0,0.3)" }}/>
                        <button type="button" className="btn btn-sm btn-ghost" onClick={() => copyText(spotifyRedirectUri, uiText("Spotify Redirect URI"))}>
                          <Copy size={13} style={{ marginRight: 4 }}/>{uiText("Kopyala")}</button>
                      </div>
                    </div>

                    <button type="button" className="btn btn-sm btn-primary" onClick={handleSaveCredentials} disabled={credSaving} style={{ alignSelf: "flex-start", marginTop: 4 }}>
                      {credSaving ? uiText("Kaydediliyor...") : uiText("Spotify API Bilgilerini Kaydet")}
                    </button>
                  </div>)}
              </div>
            </div>

            {/* YouTube Oturum Kartı */}
            <div className="studio-panel" style={{
                background: "rgba(255, 0, 0, 0.02)",
                border: uiText("1px solid rgba(255, 0, 0, 0.2)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 12,
            }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                  <span style={{ fontSize: 18, fontWeight: 800, color: "var(--red)" }}>{uiText("YouTube")}</span>
                  <span style={{
                fontSize: 11,
                fontWeight: 700,
                padding: uiText("2px 8px"),
                borderRadius: 4,
                background: ytAuth?.authenticated ? "rgba(34, 197, 94, 0.15)" : "rgba(255,255,255,0.06)",
                color: ytAuth?.authenticated ? "var(--green)" : "var(--text-muted)",
            }}>
                    {ytAuth?.authenticated ? uiText("\uD83D\uDFE2 Oturum A\u00E7\u0131k ({{v0}})", { v0: ytAuth.account || uiText("Ba\u011Fl\u0131") }) : uiText("\u26AA Hesap ba\u011Fl\u0131 de\u011Fil")}
                  </span>
                </div>

                <div style={{ display: "flex", gap: 8 }}>
                  {ytAuth?.authenticated && <button type="button" className="btn btn-sm btn-ghost" onClick={handleYouTubeConnect} disabled={ytBusy || !ytAuth?.configured}>{uiText("Tarayıcıda yeniden bağla")}</button>}
                  {ytAuth?.authenticated ? (<button type="button" className="btn btn-sm btn-ghost" style={{ color: "var(--red)" }} onClick={handleYouTubeDisconnect} disabled={ytBusy}>
                      {ytBusy ? uiText("\u0130\u015Fleniyor...") : uiText("Oturumu Kapat")}
                    </button>) : (<button type="button" className="btn btn-sm btn-primary" onClick={handleYouTubeConnect} disabled={ytBusy || !ytAuth?.configured}>
                      {ytBusy ? uiText("Giri\u015F Bekleniyor...") : uiText("YouTube ile Oturum A\u00E7")}
                    </button>)}
                </div>
              </div>

              {!ytAuth?.configured && <p role="status" style={{ margin: 0, fontSize: 12, color: "var(--text-muted)" }}>{uiText("Bu sürümde YouTube hesap bağlantısı henüz hazır değil. Şarkı çalabilirsiniz; hesap eşitlemesi sonraki güncellemeyle açılacak.")}</p>}
              <p style={{ margin: 0, fontSize: 12, color: "var(--text-muted)" }}>{uiText("YouTube listelerini ve beğenileri eşitlemek için hesabınızı varsayılan tarayıcıdan bağlayın.")}</p>
            </div>

            {credMsg && (<div style={{
                    padding: 10,
                    borderRadius: 6,
                    fontSize: 12,
                    background: credMsg.err ? "rgba(239,68,68,0.15)" : "rgba(34,197,94,0.15)",
                    color: credMsg.err ? "var(--red)" : "var(--green)",
                }}>
                {credMsg.text}
              </div>)}
          </div>)}

        {/* ════════════════════════════════════════════════════════════════════
            KATEGORİ 3: OYNATICI & SES AYARLARI
           ════════════════════════════════════════════════════════════════════ */}
        {activeTab === "player" && (<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <p style={{ padding: 14, border: uiText("1px solid var(--border)"), borderRadius: 8 }}>{uiText("Spotify \u015Fark\u0131lar\u0131 Spotify uygulamas\u0131ndan \u00E7al\u0131n\u0131r. YouTube ba\u011Flant\u0131lar\u0131 Musick oynat\u0131c\u0131s\u0131nda a\u00E7\u0131l\u0131r. Oynatma kayna\u011F\u0131 otomatik se\u00E7ilir.")}</p>
            <button type="button" className="btn btn-sm" onClick={() => { void api.openSpotify().catch(error => setAudioError(String(error.message || error))); }}>{uiText("Spotify’ı aç")}</button>
            {/* Ses Seviyesi & Dengeleme (Loudness Normalization) */}
            <div className="studio-panel" style={{ padding: 16, border: uiText("1px solid var(--border)"), borderRadius: 8, display: "grid", gap: 12 }}>
              <strong>{uiText("M\u00FCzik kaynaklar\u0131")}</strong>
              <label className="music-source-option">
                <input type="checkbox" checked={form.spotifyEnabled !== false} onChange={e => setSetting("spotifyEnabled", e.target.checked)}/>
                <span>{uiText("Spotify isteklerine ve oynatmaya izin ver")}</span>
              </label>
              <label className="music-source-option">
                <input type="checkbox" checked={form.youTubeEnabled !== false} onChange={e => setSetting("youTubeEnabled", e.target.checked)}/>
                <span>{uiText("YouTube m\u00FCzik isteklerine ve oynatmaya izin ver")}</span>
              </label>
              <label className="music-source-option">
                <input type="checkbox" checked={form.chatPlaylistRequestsEnabled === true} onChange={e => setSetting("chatPlaylistRequestsEnabled", e.target.checked)}/>
                <span>{uiText("Sohbetten çalma listesi ve albüm eklemeye izin ver")}</span>
              </label>
              <p style={{ fontSize: 12, color: "var(--text-muted)" }}>{uiText("Kapalıyken yalnızca tek şarkı bağlantıları kabul edilir. YouTube şarkısındaki liste parametresi tüm listeyi eklemez. Kitaplığa liste aktarma bu ayardan etkilenmez.")}</p>
              <p style={{ fontSize: 12, color: "var(--text-muted)" }}>{uiText("YouTube yalnızca şarkı ve sanatçı bilgisi doğrulanan kayıtları kabul eder. Kategori veya video başlığı tek başına yeterli değildir.")}</p>
            </div>
            <div className="studio-panel" style={{ padding: 16, display: "grid", gap: 10 }}>
              <label htmlFor="youtube-balance"><strong>{uiText("YouTube ses dengesi")}</strong> · %{form.youTubeVolumePercent ?? 70}</label>
              <input id="youtube-balance" type="range" min={0} max={100} value={form.youTubeVolumePercent ?? 70} onChange={e => setSetting("youTubeVolumePercent", Number(e.target.value))} />
              <p style={{ fontSize: 12, color: "var(--text-muted)", margin: 0 }}>{uiText("YouTube sesini Spotify'a göre bir kez dengeleyin. Bu oran otomatik kaydedilir ve her YouTube parçasına uygulanır; ana ses düğmesi değişmez. Gömülü YouTube oynatıcısında parça başına EBU R128 ölçümü yapılamaz.")}</p>
            </div>

            {audioError && <p role="alert" style={{ color: uiText("var(--danger, #ef4444)") }}>{audioError}</p>}
            {/* Ses Çıkış Aygıtları (WASAPI & Spotify Connect) */}
            <div className="studio-panel" style={{
                background: "rgba(255,255,255,0.02)",
                border: uiText("1px solid var(--border)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 12,
            }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div>
                  <span style={{ fontSize: 14, fontWeight: 700, color: "var(--text)" }}>{uiText("Ses \u00C7\u0131k\u0131\u015F Ayg\u0131t\u0131 (Kulakl\u0131k / Hoparl\u00F6r / Spotify Connect)")}</span>
                  <p style={{ margin: "4px 0 0", fontSize: 12, color: "var(--text-muted)" }}>{uiText("Bu bilgisayardaki Spotify ve Musick i\u00E7in kulakl\u0131k / hoparl\u00F6r se\u00E7in. \u0130sterseniz Spotify Connect ile ba\u015Fka bir oynat\u0131c\u0131ya ge\u00E7in.")}</p>
                </div>

                <button type="button" className="btn btn-sm btn-ghost" onClick={loadDevices} disabled={devicesLoading}>
                  <RefreshCw size={13} style={{ marginRight: 4 }} className={devicesLoading ? "spin" : ""}/>{uiText("Yenile")}</button>
              </div>

              <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                {audioDevices.length === 0 ? (<span style={{ fontSize: 12, color: "var(--text-muted)" }}>{uiText("Kullan\u0131labilir ses \u00E7\u0131k\u0131\u015F cihaz\u0131 bulunamad\u0131.")}</span>) : (audioDevices.map(d => {
                const isSelected = d.type === "spotify"
                    ? (d.id === activeSpotifyDeviceId || d.isActive)
                    : (activeDeviceId ? d.id === activeDeviceId : d.isDefault);
                return (<div className="studio-device" data-selected={isSelected} key={`${d.type}-${d.id}`} onClick={() => handleSelectDevice(d)} style={{
                        padding: uiText("8px 12px"),
                        borderRadius: 6,
                        cursor: "pointer",
                        background: isSelected ? "rgba(99, 102, 241, 0.15)" : "rgba(255,255,255,0.02)",
                        border: isSelected ? uiText("1px solid var(--accent)") : uiText("1px solid var(--border)"),
                        display: "flex",
                        alignItems: "center",
                        justifyContent: "space-between",
                    }}>
                        <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                          <Headphones size={15} style={{ color: d.type === "spotify" ? "var(--green)" : "var(--text-muted)" }}/>
                          <div style={{ display: "flex", flexDirection: "column" }}>
                            <span style={{ fontSize: 13, fontWeight: isSelected ? 700 : 500 }}>{d.name}</span>
                            <span style={{ fontSize: 10, color: "var(--text-muted)" }}>
                              {d.type === "spotify" ? uiText("Spotify Connect Cihaz\u0131") : uiText("Spotify + Musick \u00B7 Bu bilgisayar")}
                            </span>
                          </div>
                        </div>

                        {isSelected && <span style={{ fontSize: 11, fontWeight: 700, color: "var(--accent)" }}>{uiText("\u2713 Aktif")}</span>}
                      </div>);
            }))}
              </div>
            </div>

            {/* Şarkı Atlama Oylaması (Vote Skip) */}
            <div className="studio-panel" style={{
                background: "rgba(255,255,255,0.02)",
                border: uiText("1px solid var(--border)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 8,
            }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div>
                  <span style={{ fontSize: 14, fontWeight: 700, color: "var(--text)" }}>{uiText("\u015Eark\u0131 Atlama Oylamas\u0131 (Vote Skip)")}</span>
                  <p style={{ margin: "4px 0 0", fontSize: 12, color: "var(--text-muted)" }}>{uiText("\u0130zleyiciler sohbetten")}<code>{uiText("!skip")}</code>{uiText("yazd\u0131\u011F\u0131nda 30 saniyelik demokratik atlama oylamas\u0131 ba\u015Flar.")}</p>
                </div>
                <button type="button" role="switch" aria-label="Şarkı atlama oylaması" aria-checked={form.votingEnabled} className={`settings-toggle${form.votingEnabled ? " on" : ""}`} onClick={() => setSetting("votingEnabled", !form.votingEnabled)} style={{ cursor: "pointer" }}>
                  <div className="settings-toggle-thumb"/>
                </button>
              </div>
            </div>

            {/* Yayında Bulunma Kontrolü (Presence Check) */}
            <div className="studio-panel" style={{
                background: "rgba(255,255,255,0.02)",
                border: uiText("1px solid var(--border)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 12,
            }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div>
                  <span style={{ fontSize: 14, fontWeight: 700, color: "var(--text)" }}>{uiText("Yay\u0131nda Bulunma Kontrol\u00FC (AFK Check)")}</span>
                  <p style={{ margin: "4px 0 0", fontSize: 12, color: "var(--text-muted)" }}>{uiText("\u015Eark\u0131s\u0131 yakla\u015Fan izleyiciye sohbette bildirim at\u0131l\u0131r. Belirtilen s\u00FCrede")}<code>{uiText("!buradayim")}</code>{uiText("yazmayan izleyicinin \u015Fark\u0131s\u0131 atlan\u0131r.")}</p>
                </div>
                <button type="button" role="switch" aria-label="Yayında bulunma kontrolü" aria-checked={form.presenceCheckEnabled} className={`settings-toggle${form.presenceCheckEnabled ? " on" : ""}`} onClick={() => setSetting("presenceCheckEnabled", !form.presenceCheckEnabled)} style={{ cursor: "pointer" }}>
                  <div className="settings-toggle-thumb"/>
                </button>
              </div>

              {form.presenceCheckEnabled && (<div style={{ display: "grid", gridTemplateColumns: uiText("1fr 1fr"), gap: 16, paddingTop: 10, borderTop: uiText("1px solid rgba(255,255,255,0.06)") }}>
                  <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                    <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("\u00D6n Uyar\u0131 S\u00FCresi (Saniye)")}</span>
                    <input type="number" min={5} max={120} className="input" value={form.presenceCheckWarningSeconds} onChange={e => setSetting("presenceCheckWarningSeconds", Number(e.target.value))} style={{ maxWidth: 120, fontSize: 13 }}/>
                    <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("\u015Eark\u0131 ba\u015Flamadan ka\u00E7 saniye \u00F6nce sohbet uyar\u0131s\u0131 g\u00F6nderilsin.")}</span>
                  </label>

                  <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                    <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Onay Bekleme S\u00FCresi (Saniye)")}</span>
                    <input type="number" min={5} max={120} className="input" value={form.presenceCheckConfirmSeconds} onChange={e => setSetting("presenceCheckConfirmSeconds", Number(e.target.value))} style={{ maxWidth: 120, fontSize: 13 }}/>
                    <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("\u0130zleyiciye onay vermesi i\u00E7in tan\u0131nan s\u00FCre.")}</span>
                  </label>
                </div>)}
            </div>
          </div>)}

        {/* ════════════════════════════════════════════════════════════════════
            KATEGORİ 5: GELİŞMİŞ & SİSTEM
           ════════════════════════════════════════════════════════════════════ */}
        {activeTab === "advanced" && (<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <BannedSongsPanel/><SettingsBackupPanel disabled={saving || backupBusy} onBusy={setBackupBusy}/><UpdatesPanel />
        <section className="studio-panel" style={{ padding: 16, display: "flex", justifyContent: "space-between", alignItems: "center", gap: 16 }}>
          <div><strong>{uiText("Masaüstü bildirimleri")}</strong><p style={{ margin: "4px 0 0", fontSize: 12, color: "var(--text-muted)" }}>{uiText("Pencere kapatılıp sistem tepsisine alındığında bildirim göster.")}</p></div>
          <button type="button" role="switch" aria-label={uiText("Masaüstü bildirimleri")} aria-checked={form.notificationsEnabled !== false}
            className={`settings-toggle${form.notificationsEnabled !== false ? " on" : ""}`} onClick={() => setSetting("notificationsEnabled", form.notificationsEnabled === false)} style={{ padding: 0, cursor: "pointer", flexShrink: 0 }}><span className="settings-toggle-thumb"/></button>
        </section>

        <label style={{ display: "flex", gap: 12, alignItems: "center" }}>{uiText("Aray\u00FCz dili")}<select className="input" style={{ width: 180 }} value={i18n.language} onChange={e => { localStorage.setItem("musick-language", e.target.value); void i18n.changeLanguage(e.target.value).then(() => window.location.reload()); }}><option value="tr">{uiText("T\u00FCrk\u00E7e")}</option><option value="en">{uiText("English")}</option></select></label>

            {/* yt-dlp Güncelleyici */}
            <div className="studio-panel" style={{
                background: "rgba(255,255,255,0.02)",
                border: uiText("1px solid var(--border)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 12,
            }}>
              <div style={{ fontSize: 14, fontWeight: 700, color: "var(--text)" }}>{uiText("yt-dlp Motoru & G\u00FCncelleme")}</div>
              <p style={{ margin: 0, fontSize: 12, color: "var(--text-muted)" }}>{uiText("YouTube algoritmalar\u0131 de\u011Fi\u015Ftik\u00E7e \u015Fark\u0131 indirme hatalar\u0131 ya\u015Fanmamas\u0131 i\u00E7in yt-dlp motorunu tek t\u0131kla en son s\u00FCr\u00FCme g\u00FCncelleyin.")}</p>

              <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
                <button type="button" className="btn btn-sm btn-primary" onClick={handleUpdateYtDlp} disabled={ytDlpUpdating}>
                  <RefreshCw size={13} style={{ marginRight: 4 }} className={ytDlpUpdating ? "spin" : ""}/>
                  {ytDlpUpdating ? uiText("G\u00FCncelleniyor...") : uiText("yt-dlp'yi En Son S\u00FCr\u00FCme G\u00FCncelle")}
                </button>

                {ytDlpMsg && (<span style={{ fontSize: 12, color: ytDlpMsg.err ? "var(--red)" : "var(--green)" }}>
                    {ytDlpMsg.text}
                  </span>)}
              </div>
            </div>

            {/* Sistem Logları & Başlangıç */}
            <div className="studio-panel" style={{
                background: "rgba(255,255,255,0.02)",
                border: uiText("1px solid var(--border)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 12,
            }}>
              <div style={{ fontSize: 14, fontWeight: 700, color: "var(--text)" }}>{uiText("Sistem Loglar\u0131 & Tan\u0131lama")}</div>

              <label style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div>
                  <span style={{ fontSize: 13, fontWeight: 600 }}>{uiText("Uygulama Ba\u015Flang\u0131c\u0131nda Log Penceresini Otomatik A\u00E7")}</span>
                  <p style={{ margin: "4px 0 0", fontSize: 12, color: "var(--text-muted)" }}>{uiText("Hata tespiti ve detayl\u0131 i\u015Flem takibi i\u00E7in harici log penceresini a\u00E7ar.")}</p>
                </div>
                <button type="button" role="switch" aria-label="Başlangıçta günlük penceresini aç" aria-checked={form.openLogOnStart} className={`settings-toggle${form.openLogOnStart ? " on" : ""}`} onClick={() => setSetting("openLogOnStart", !form.openLogOnStart)} style={{ cursor: "pointer" }}>
                  <div className="settings-toggle-thumb"/>
                </button>
              </label>

              <div style={{ display: "flex", gap: 10, marginTop: 4 }}>
                <button type="button" className="btn btn-sm btn-ghost" onClick={() => api.openLog()}>
                  <FileText size={14} style={{ marginRight: 4 }}/>{uiText("Canl\u0131 Loglar\u0131 A\u00E7")}</button>

                <button type="button" className="btn btn-sm btn-ghost" onClick={() => api.openLogDir()}>
                  <FolderOpen size={14} style={{ marginRight: 4 }}/>{uiText("Log Klas\u00F6r\u00FCn\u00FC G\u00F6ster")}</button>
              </div>
            </div>

            {/* Veri Yolu Bilgisi */}
            <div className="studio-panel" style={{
                background: "rgba(255,255,255,0.02)",
                border: uiText("1px solid var(--border)"),
                borderRadius: 8,
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 6,
            }}>
              <span style={{ fontSize: 13, fontWeight: 700, color: "var(--text)" }}>{uiText("Veri Depolama ve Yap\u0131land\u0131rma Yolu")}</span>
              <p style={{ margin: 0, fontSize: 12, color: "var(--text-muted)" }}>{uiText("API anahtarlar\u0131n\u0131z")}<code>{uiText("appsettings.user.json")}</code>{uiText("dosyas\u0131nda, k\u00FCt\u00FCphane ve \u00E7alma listeleriniz ise yerel SQLite veri taban\u0131nda (")}<code>{uiText("musicbot.db")}</code>{uiText(") saklan\u0131r.")}</p>
            </div>
          </div>)}
      </div>
    </fieldset></>);
};
