import { LoadingState } from "./PanelState";
import { uiText } from "../i18n/ui";
import React, { useEffect, useState, useMemo, useRef } from "react";
import { Download, Upload, Search, RotateCcw, Plus, Trash2, ChevronDown, ChevronUp, Check, AlertCircle, Sparkles, MessageSquare, Crown, Shield, Award, Star, Users } from "lucide-react";
import { useAutoSave } from "../hooks/useAutoSave";
import { api } from "../services/api";

function AliasInput({ aliases, placeholder, onChange }: { aliases: string[]; placeholder: string; onChange: (aliases: string[]) => void }) {
    const [draft, setDraft] = useState(() => aliases.join(", "));
    const focused = useRef(false);
    const serialized = JSON.stringify(aliases);
    useEffect(() => { if (!focused.current) setDraft(aliases.join(", ")); }, [serialized]);
    return <input type="text" className="input" placeholder={placeholder} value={draft}
        onFocus={() => { focused.current = true; }}
        onChange={e => {
            const value = e.target.value;
            setDraft(value);
            onChange([...new Set(value.split(/[,;\s]+/u).map(x => x.replace(/^!+/, "")).filter(Boolean))]);
        }}
        onBlur={() => { focused.current = false; setDraft(aliases.join(", ")); }} style={{ fontSize: 13 }} />;
}

export interface RoleProfileItem {
    role: string;
    name: string;
    canRequestSongs: boolean;
    cooldownSeconds: number;
    maxSongsInQueue: number;
    bypassCooldown: boolean;
    bypassLimits: boolean;
}
export const DEFAULT_ROLE_PROFILES: Record<string, RoleProfileItem> = {
    broadcaster: { role: "broadcaster", name: uiText("Yay\u0131nc\u0131"), canRequestSongs: true, cooldownSeconds: 0, maxSongsInQueue: 50, bypassCooldown: true, bypassLimits: true },
    moderator: { role: "moderator", name: uiText("Moderat\u00F6r"), canRequestSongs: true, cooldownSeconds: 0, maxSongsInQueue: 10, bypassCooldown: true, bypassLimits: false },
    vip: { role: "vip", name: uiText("VIP"), canRequestSongs: true, cooldownSeconds: 15, maxSongsInQueue: 5, bypassCooldown: false, bypassLimits: false },
    subscriber: { role: "subscriber", name: uiText("Abone"), canRequestSongs: true, cooldownSeconds: 30, maxSongsInQueue: 3, bypassCooldown: false, bypassLimits: false },
    everyone: { role: "everyone", name: uiText("Herkes"), canRequestSongs: true, cooldownSeconds: 60, maxSongsInQueue: 2, bypassCooldown: false, bypassLimits: false },
};
export interface CommandItem {
    name: string;
    aliases: string[];
    enabled: boolean;
    cooldownSeconds: number;
    allowedRoles?: string[];
    requiredRole: string; // everyone, subscriber, moderator, broadcaster
    maxPerUser: number; // 0 = unlimited
    roleCooldowns?: Record<string, number>;
    exemptRoles?: string[];
    templates?: Record<string, string>;
}
export interface CustomCommandItem {
    name: string;
    aliases: string[];
    responseText: string;
    allowedRoles?: string[];
    requiredRole: string;
    cooldownSeconds: number;
    roleCooldowns?: Record<string, number>;
    exemptRoles?: string[];
    enabled: boolean;
}
export interface CommandsConfigResponse {
    commands?: Record<string, CommandItem>;
    customCommands?: Record<string, CustomCommandItem>;
    roleProfiles?: Record<string, RoleProfileItem>;
}
const ROLES = ["broadcaster", "moderator", "vip", "subscriber", "everyone"];
const ROLE_NAMES: Record<string, string> = { broadcaster: uiText("Yay\u0131nc\u0131"), moderator: uiText("Moderat\u00F6r"), vip: "VIP / OG", subscriber: uiText("Abone"), everyone: uiText("\u0130zleyici") };
function inheritedRoles(cmd: CommandItem | CustomCommandItem, isPlay: boolean, profiles: Record<string, RoleProfileItem>) {
    if (cmd.allowedRoles)
        return cmd.allowedRoles;
    const required = cmd.requiredRole || "everyone";
    return ROLES.filter(role => {
        const permitted = role === "broadcaster" || required === "everyone" || required === "all"
            || role === required || (role === "moderator" && required !== "broadcaster");
        return permitted && (!isPlay || (profiles[role] ?? DEFAULT_ROLE_PROFILES[role]).canRequestSongs);
    });
}
interface KickRoleMatrixProps {
    cmdKey: string;
    isPlay: boolean;
    cmd: CommandItem | CustomCommandItem;
    roleProfiles: Record<string, RoleProfileItem>;
    onUpdateRoleProfile: (role: string, field: keyof RoleProfileItem, value: any) => void;
    onUpdateRoleCooldown: (role: string, seconds: number) => void;
    onSetAllowedRoles: (roles: string[]) => void;
}
const KickRoleMatrix: React.FC<KickRoleMatrixProps> = ({ isPlay, cmd, roleProfiles, onUpdateRoleProfile, onUpdateRoleCooldown, onSetAllowedRoles }) => {
    const allowed = inheritedRoles(cmd, isPlay, roleProfiles);
    return <section style={{ padding: 16, border: uiText("1px solid var(--border)"), borderRadius: 8 }}>
    <strong>{uiText("Rol izinleri ve bekleme s\u00FCreleri")}</strong>
    <p style={{ fontSize: 12, color: "var(--text-muted)" }}>{uiText("Her rol ba\u011F\u0131ms\u0131zd\u0131r. Bir rol\u00FC de\u011Fi\u015Ftirmek di\u011Fer rollerin iznini de\u011Fi\u015Ftirmez. Birden fazla rol\u00FC olan ki\u015Fiye en y\u00FCksek rol\u00FCn\u00FCn ayar\u0131 uygulan\u0131r.")}</p>
    <div style={{ display: "flex", gap: 8, marginBottom: 12 }}>
      <button className="btn btn-sm btn-ghost" onClick={() => onSetAllowedRoles([...ROLES])}>{uiText("T\u00FCm\u00FCne izin ver")}</button>
      <button className="btn btn-sm btn-ghost" onClick={() => onSetAllowedRoles(["broadcaster", "moderator"])}>{uiText("Yay\u0131nc\u0131 ve moderat\u00F6r")}</button>
      <button className="btn btn-sm btn-ghost" onClick={() => onSetAllowedRoles([])}>{uiText("T\u00FCm\u00FCn\u00FC engelle")}</button>
    </div>
    <div style={{ overflowX: "auto" }}><table style={{ width: "100%", textAlign: "left", fontSize: 13 }}>
      <thead><tr><th>{uiText("Rol")}</th><th>{uiText("\u0130zin")}</th><th>{uiText("Bekleme (saniye)")}</th>{isPlay && <th>{uiText("Ki\u015Fi ba\u015F\u0131 \u015Fark\u0131 (0 = s\u0131n\u0131rs\u0131z)")}</th>}</tr></thead>
      <tbody>{ROLES.map(role => {
            const profile = roleProfiles[role] ?? DEFAULT_ROLE_PROFILES[role];
            const enabled = allowed.includes(role);
            const inherited = isPlay ? (profile.bypassCooldown ? 0 : profile.cooldownSeconds) : ((cmd.exemptRoles || []).includes(role) ? 0 : cmd.cooldownSeconds);
            return <tr key={role}>
          <td style={{ padding: "10px 0" }}>{ROLE_NAMES[role]}</td>
          <td><button className={`btn btn-sm permission-toggle ${enabled ? "allowed" : "blocked"}`} aria-pressed={enabled} onClick={() => onSetAllowedRoles(enabled ? allowed.filter(r => r !== role) : [...allowed, role])}>{enabled ? uiText("\u0130zinli") : uiText("Engelli")}</button></td>
          <td><input aria-label={ROLE_NAMES[role] + uiText(" bekleme s\u00FCresi")} className="input" type="number" min={0} max={3600} style={{ width: 90 }} value={cmd.roleCooldowns?.[role] ?? inherited} onChange={e => onUpdateRoleCooldown(role, Math.max(0, Number(e.target.value)))}/></td>
          {isPlay && <td><input aria-label={ROLE_NAMES[role] + uiText(" \u015Fark\u0131 limiti")} className="input" type="number" min={0} style={{ width: 90 }} value={profile.bypassLimits ? 0 : profile.maxSongsInQueue} onChange={e => {
                        const n = Math.max(0, Number(e.target.value));
                        onUpdateRoleProfile(role, "maxSongsInQueue", n);
                        onUpdateRoleProfile(role, "bypassLimits", n === 0);
                    }}/></td>}
        </tr>;
        })}</tbody>
    </table></div>
  </section>;
};
const TEMPLATE_KEY_LABELS: Record<string, string> = {
    success: uiText("Ba\u015Far\u0131l\u0131 \u0130stek / \u0130\u015Flem Yan\u0131t\u0131"),
    queue_full: uiText("Kuyruk dolu"),
    user_limit: uiText("Kullanıcı istek sınırı"),
    spotify_not_connected: uiText("Spotify bağlı değil"),
    youtube_playlist_added: uiText("YouTube listesi eklendi"),
    spotify_album_added: uiText("Spotify albümü eklendi"),
    spotify_playlist_added: uiText("Spotify listesi eklendi"),
    youtube_disabled: uiText("YouTube kapalı"),
    spotify_disabled: uiText("Spotify kapalı"),
    invalid_link: uiText("Geçersiz YouTube bağlantısı"),
    playlist_disabled: uiText("Liste istekleri kapalı"),
    service_error: uiText("Servis / YouTube doğrulama / oynatma hatası"),
    queue_error: uiText("Kuyruğa ekleme hatası"),
    playlist_empty: uiText("Listeden şarkı eklenemedi"),
    empty_query: uiText("Eksik \u015Eark\u0131 Ad\u0131 / Parametre Uyar\u0131s\u0131"),
    empty: uiText("Kuyruk veya Liste Bo\u015F Uyar\u0131s\u0131"),
    already_queued: uiText("\u015Eark\u0131 Zaten Kuyrukta Uyar\u0131s\u0131"),
    max_reached: uiText("Kullan\u0131c\u0131 \u0130stek S\u0131n\u0131r\u0131 A\u015F\u0131ld\u0131"),
    banned: uiText("Kara Listeli \u015Eark\u0131 Uyar\u0131s\u0131"),
    not_found: uiText("\u015Eark\u0131 / \u0130stek Bulunamad\u0131"),
    not_your_song: uiText("Ba\u015Fkas\u0131n\u0131n \u015Eark\u0131s\u0131n\u0131 Atlama Uyar\u0131s\u0131"),
    nothing_playing: uiText("\u015Eu An \u00C7alan \u015Eark\u0131 Yok"),
    confirmed: uiText("Burada Olma Onay\u0131 (Presence)"),
    cooldown: uiText("Bekleme S\u00FCresi (Cooldown) Uyar\u0131s\u0131"),
    permission_denied: uiText("Yetki Yetersiz Uyar\u0131s\u0131"),
};
const TEMPLATE_VARIABLES = [
    { tag: "{user}", label: uiText("{user} (Kullan\u0131c\u0131)") },
    { tag: "{song}", label: uiText("{song} (\u015Eark\u0131 Ad\u0131)") },
    { tag: "{artist}", label: uiText("{artist} (Sanat\u00E7\u0131)") },
    { tag: "{position}", label: uiText("{position} (Kuyruk S\u0131ras\u0131)") },
    { tag: "{wait_time}", label: "{wait_time} (Tahmini Bekleme)" },
    { tag: "{queue_length}", label: uiText("{queue_length} (Kuyruk Uzunlu\u011Fu)") },
    { tag: "{requester}", label: uiText("{requester} (\u0130stek Sahibi)") },
];
const CUSTOM_VARIABLES = [
    { tag: "{kullanici}", label: uiText("{kullanici} (@\u0130zleyici)") },
    { tag: "{sarki}", label: uiText("{sarki} (\u00C7alan \u015Eark\u0131)") },
    { tag: "{sanatci}", label: uiText("{sanatci} (Sanat\u00E7\u0131)") },
    { tag: "{siradaki}", label: uiText("{siradaki} (S\u0131radaki \u015Eark\u0131)") },
    { tag: "{sayi}", label: "{sayi} (Kuyruk Adedi)" },
    { tag: "{kanal}", label: uiText("{kanal} (Kanal Ad\u0131)") },
];
const ROLE_OPTIONS = [
    { value: "everyone", label: uiText("Herkes (T\u00FCm \u0130zleyiciler)") },
    { value: "subscriber", label: uiText("Aboneler") },
    { value: "moderator", label: uiText("Moderat\u00F6rler") },
    { value: "broadcaster", label: uiText("Yaln\u0131zca Yay\u0131nc\u0131") },
];
export const CommandsPanel: React.FC = () => {
    const [commands, setCommands] = useState<Record<string, CommandItem>>({});
    const [customCommands, setCustomCommands] = useState<Record<string, CustomCommandItem>>({});
    const [roleProfiles, setRoleProfiles] = useState<Record<string, RoleProfileItem>>(DEFAULT_ROLE_PROFILES);
    const [activeTab, setActiveTab] = useState<"builtin" | "custom">("builtin");
    const [loading, setLoading] = useState(true);
    const [configLoaded, setConfigLoaded] = useState(false);
    const [saving, setSaving] = useState(false);
    const [search, setSearch] = useState("");
    const [filterRole, setFilterRole] = useState("all");
    const [expandedCmd, setExpandedCmd] = useState<string | null>(null);
    const [feedback, setFeedback] = useState<{
        text: string;
        err: boolean;
    } | null>(null);
    const [newTemplateKey, setNewTemplateKey] = useState("");
    const importInput = useRef<HTMLInputElement>(null);
    const [importing, setImporting] = useState(false);
    const isLoadedRef = useRef(false);
    const autoSave = useAutoSave({ commands, customCommands, roleProfiles }, api.updateCommandsConfig, !loading && configLoaded && !importing);
    const autoSaveState = autoSave.status;
    const loadConfig = async () => {
        setLoading(true);
        try {
            const data = await api.getCommandsConfig();
            const cfg = data as CommandsConfigResponse;
            const platforms = await api.getPlatforms();
            const kick = platforms.find(p => p.platform === "kick");
            const channelRoles = (kick?.config as {
                commandRoles?: string[];
            } | undefined)?.commandRoles || ["all"];
            if (cfg.commands?.play && !cfg.commands.play.allowedRoles) {
                const allowed = inheritedRoles(cfg.commands.play, true, cfg.roleProfiles || DEFAULT_ROLE_PROFILES);
                cfg.commands.play.allowedRoles = allowed.filter(role => role === "broadcaster" || channelRoles.includes("all") || channelRoles.includes("list")
                    || channelRoles.includes(role) || (role === "vip" && channelRoles.includes("og")));
            }
            if (cfg?.commands) {
                setCommands(cfg.commands);
            }
            if (cfg?.customCommands) {
                setCustomCommands(cfg.customCommands);
            }
            setConfigLoaded(true);
            if (cfg?.roleProfiles) {
                setRoleProfiles(prev => ({ ...prev, ...cfg.roleProfiles }));
            }
        }
        catch {
            showFeedback(uiText("Komut yap\u0131land\u0131rmas\u0131 y\u00FCklenemedi."), true);
        }
        finally {
            setLoading(false);
        }
    };
    useEffect(() => {
        loadConfig();
    }, []);
    const updateRoleProfile = (roleKey: string, field: keyof RoleProfileItem, value: unknown) => {
        setRoleProfiles(prev => {
            const cur = prev[roleKey] || DEFAULT_ROLE_PROFILES[roleKey];
            return {
                ...prev,
                [roleKey]: {
                    ...cur,
                    [field]: value,
                },
            };
        });
    };
    const showFeedback = (text: string, err = false) => {
        setFeedback({ text, err });
        setTimeout(() => setFeedback(null), 4000);
    };
    const exportCommands = () => {
        const document = { format: "musick.commands", version: 1, exportedAt: new Date().toISOString(), configuration: { commands, customCommands, roleProfiles } };
        const url = URL.createObjectURL(new Blob([JSON.stringify(document, null, 2)], { type: "application/json" }));
        const link = window.document.createElement("a");
        link.href = url; link.download = "Musick-Komutlar.json"; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    };
    const importCommands = async (event: React.ChangeEvent<HTMLInputElement>) => {
        const file = event.target.files?.[0]; event.target.value = "";
        if (!file) return;
        if (file.size > 2 * 1024 * 1024) { showFeedback(uiText("Komut dosyası en fazla 2 MB olabilir."), true); return; }
        setImporting(true);
        try {
            const document: unknown = JSON.parse(await file.text());
            if (!window.confirm(uiText("Bu dosyadaki komutlar, roller ve yanıt şablonları mevcut ayarların yerini alacak. Devam edilsin mi?"))) return;
            const config = await api.importCommandsConfig(document) as CommandsConfigResponse;
            setCommands(config.commands!); setCustomCommands(config.customCommands!); setRoleProfiles(config.roleProfiles!);
            showFeedback(uiText("Komutlar içe aktarıldı ve kaydedildi."));
        } catch (error) {
            showFeedback(error instanceof Error ? error.message : uiText("Komut dosyası okunamadı."), true);
        } finally { setImporting(false); }
    };
    const aliasConflicts = useMemo(() => {
        const seen = new Map<string, string>();
        const conflicts: {
            alias: string;
            cmd1: string;
            cmd2: string;
        }[] = [];
        const check = (alias: string, cmdName: string) => {
            const clean = alias.trim().toLowerCase().replace(/^!/, "");
            if (!clean)
                return;
            if (seen.has(clean)) {
                const existing = seen.get(clean)!;
                if (existing !== cmdName) {
                    conflicts.push({ alias: clean, cmd1: existing, cmd2: cmdName });
                }
            }
            else {
                seen.set(clean, cmdName);
            }
        };
        Object.entries(commands).forEach(([key, cmd]) => {
            check(cmd.name || key, cmd.name || key);
            (cmd.aliases || []).forEach(a => check(a, cmd.name || key));
        });
        Object.entries(customCommands).forEach(([key, cmd]) => {
            check(cmd.name || key, cmd.name || key);
            (cmd.aliases || []).forEach(a => check(a, cmd.name || key));
        });
        return conflicts;
    }, [commands, customCommands]);
    const handleSave = async () => {
        setSaving(true);
        try {
            await api.updateCommandsConfig({ commands, customCommands, roleProfiles });
            showFeedback(uiText("Komutlar ve rol profilleri ba\u015Far\u0131yla kaydedildi! (commands.json g\u00FCncellendi)"));
        }
        catch (e: unknown) {
            showFeedback(e instanceof Error ? e.message : uiText("Kaydedilirken hata olu\u015Ftu."), true);
        }
        finally {
            setSaving(false);
        }
    };
    const handleAddCustomCommand = () => {
        const id = `custom_${Date.now()}`;
        const nextNum = Object.keys(customCommands).length + 1;
        setCustomCommands(prev => ({
            ...prev,
            [id]: {
                name: `ozel${nextNum}`,
                aliases: [],
                responseText: uiText("@{kullanici}, bu \u00F6zel bir Kick sohbet komutudur!"),
                requiredRole: "everyone",
                cooldownSeconds: 10,
                roleCooldowns: { subscriber: 5, everyone: 15 },
                exemptRoles: ["broadcaster", "moderator"],
                enabled: true,
            }
        }));
        setActiveTab("custom");
    };
    const updateCustomCommand = <K extends keyof CustomCommandItem>(key: string, field: K, value: CustomCommandItem[K]) => {
        setCustomCommands(prev => ({
            ...prev,
            [key]: {
                ...prev[key],
                [field]: value
            }
        }));
    };
    const removeCustomCommand = (key: string) => {
        setCustomCommands(prev => {
            const next = { ...prev };
            delete next[key];
            return next;
        });
    };
    const renderPreview = (template: string) => {
        return template
            .replace(/{kullanici}|{user}/g, "@İzleyici")
            .replace(/{sarki}|{song}/g, "Daft Punk - One More Time")
            .replace(/{sanatci}|{artist}/g, "Daft Punk")
            .replace(/{siradaki}|{next}/g, "The Weeknd - Blinding Lights")
            .replace(/{sayi}|{count}/g, "4")
            .replace(/{kanal}|{channel}/g, "KickYayinci");
    };
    const handleResetToDefaults = async () => {
        if (!window.confirm(uiText("T\u00FCm komut ayarlar\u0131 varsay\u0131lana s\u0131f\u0131rlans\u0131n m\u0131?")))
            return;
        setSaving(true);
        try {
            await api.reloadCommandsConfig();
            await loadConfig();
            showFeedback(uiText("Varsay\u0131lan komut ayarlar\u0131 y\u00FCklendi."));
        }
        catch {
            showFeedback(uiText("S\u0131f\u0131rlama ba\u015Far\u0131s\u0131z oldu."), true);
        }
        finally {
            setSaving(false);
        }
    };
    const updateCommandField = <K extends keyof CommandItem>(cmdKey: string, field: K, value: CommandItem[K]) => {
        setCommands(prev => {
            const current = prev[cmdKey];
            if (!current)
                return prev;
            return {
                ...prev,
                [cmdKey]: {
                    ...current,
                    [field]: value,
                },
            };
        });
    };
    const updateTemplate = (cmdKey: string, templateKey: string, value: string) => {
        setCommands(prev => {
            const current = prev[cmdKey];
            if (!current)
                return prev;
            const templates = { ...(current.templates || {}), [templateKey]: value };
            return {
                ...prev,
                [cmdKey]: {
                    ...current,
                    templates,
                },
            };
        });
    };
    const removeTemplate = (cmdKey: string, templateKey: string) => {
        setCommands(prev => {
            const current = prev[cmdKey];
            if (!current || !current.templates)
                return prev;
            const templates = { ...current.templates };
            templates[templateKey] = "";
            return {
                ...prev,
                [cmdKey]: {
                    ...current,
                    templates,
                },
            };
        });
    };
    const addTemplate = (cmdKey: string, keyName: string) => {
        const k = keyName.trim().toLowerCase();
        if (!k)
            return;
        updateTemplate(cmdKey, k, uiText("@{user}, i\u015Flem ba\u015Far\u0131l\u0131."));
        setNewTemplateKey("");
    };
    const insertVariableIntoTemplate = (cmdKey: string, templateKey: string, variableTag: string) => {
        const currentText = commands[cmdKey]?.templates?.[templateKey] || "";
        updateTemplate(cmdKey, templateKey, `${currentText} ${variableTag}`.trim());
    };
    const toggleExemptRole = (cmdKey: string, role: string, isCustom = false) => {
        if (isCustom) {
            const cur = customCommands[cmdKey]?.exemptRoles || [];
            const updated = cur.includes(role) ? cur.filter(r => r !== role) : [...cur, role];
            updateCustomCommand(cmdKey, "exemptRoles", updated);
        }
        else {
            const cur = commands[cmdKey]?.exemptRoles || [];
            const updated = cur.includes(role) ? cur.filter(r => r !== role) : [...cur, role];
            updateCommandField(cmdKey, "exemptRoles", updated);
        }
    };
    const setRoleCooldown = (cmdKey: string, role: string, seconds: number, isCustom = false) => {
        if (isCustom) {
            const cur = { ...(customCommands[cmdKey]?.roleCooldowns || {}) };
            cur[role] = Math.max(0, seconds);
            updateCustomCommand(cmdKey, "roleCooldowns", cur);
        }
        else {
            const cur = { ...(commands[cmdKey]?.roleCooldowns || {}) };
            cur[role] = Math.max(0, seconds);
            updateCommandField(cmdKey, "roleCooldowns", cur);
        }
    };
    const filteredCommands = useMemo(() => {
        return Object.entries(commands).filter(([key, cmd]) => {
            const q = search.trim().toLowerCase();
            const matchSearch = !q ||
                key.toLowerCase().includes(q) ||
                cmd.name.toLowerCase().includes(q) ||
                (cmd.aliases && cmd.aliases.some(a => a.toLowerCase().includes(q)));
            const matchRole = filterRole === "all" ||
                (filterRole === "admin" && (cmd.requiredRole === "moderator" || cmd.requiredRole === "broadcaster")) ||
                (filterRole === "viewer" && (cmd.requiredRole === "everyone" || cmd.requiredRole === "subscriber"));
            return matchSearch && matchRole;
        });
    }, [commands, search, filterRole]);
    return (<div className="commands-panel" aria-busy={importing} style={{ display: "flex", flexDirection: "column", gap: 16 }}>

      <fieldset disabled={importing} style={{ border: 0, padding: 0, margin: 0, minWidth: 0, display: "contents" }}>
      {/* ── Top Header & Actions ── */}
      <div style={{
            display: "flex",
            flexWrap: "wrap",
            justifyContent: "space-between",
            alignItems: "center",
            gap: 12,
            paddingBottom: 14,
            borderBottom: uiText("1px solid var(--border)"),
        }}>
        <div>
          <h2 style={{ margin: "0 0 4px 0", fontSize: 18, fontWeight: 700 }}>{uiText("Komut Y\u00F6netimi & \u00D6zelle\u015Ftirme")}</h2>
          <p style={{ margin: 0, fontSize: 12, color: "var(--text-muted)" }}>{uiText("Kick sohbet komutlar\u0131n\u0131, takma adlar\u0131 (alias), rol bazl\u0131 yetkileri, bekleme s\u00FCrelerini (cooldown) ve T\u00FCrk\u00E7e yan\u0131t \u015Fablonlar\u0131n\u0131 buradan d\u00FCzenleyin.")}</p>
        </div>

        <div style={{ display: "flex", gap: 10, alignItems: "center" }}>
          {autoSaveState === "saving" && (<span style={{ fontSize: 12, color: "#facc15", display: "flex", alignItems: "center", gap: 6 }}>
              <span className="pulse-dot-green" style={{ background: "#facc15", width: 7, height: 7 }}/>{uiText("Kaydediliyor...")}</span>)}
          {autoSaveState === "saved" && (<span style={{ fontSize: 12, color: "#22c55e", display: "flex", alignItems: "center", gap: 6, fontWeight: 600 }}>
              <Check size={14}/>{uiText("\u2713 Otomatik Kaydedildi")}</span>)}
          {autoSaveState === "error" && (<span style={{ fontSize: 12, color: "#ef4444", display: "flex", alignItems: "center", gap: 6 }}>
              <AlertCircle size={14}/>{uiText("Kaydedilemedi")}<button className="btn btn-sm" onClick={autoSave.retry}>{uiText("Tekrar dene")}</button>
            </span>)}

          <input ref={importInput} type="file" accept=".json,application/json" hidden onChange={importCommands}/>
          <button type="button" className="btn btn-sm btn-ghost" onClick={exportCommands} disabled={loading || importing}><Download size={14}/>{uiText("Dışa aktar")}</button>
          <button type="button" className="btn btn-sm btn-ghost" onClick={() => importInput.current?.click()} disabled={loading || importing || autoSaveState === "saving"}><Upload size={14}/>{uiText("İçe aktar")}</button>
          <button type="button" className="btn btn-sm btn-ghost" onClick={handleResetToDefaults} disabled={loading} title={uiText("Dosyadan tekrar y\u00FCkle veya varsay\u0131lanlara s\u0131f\u0131rla")}>
            <RotateCcw size={14} style={{ marginRight: 4 }}/>{uiText("S\u0131f\u0131rla")}</button>
        </div>
      </div>

      {/* ── Feedback Notification ── */}
      {feedback && (<div style={{
                padding: uiText("10px 14px"),
                borderRadius: 6,
                fontSize: 13,
                display: "flex",
                alignItems: "center",
                gap: 8,
                background: feedback.err ? "rgba(239, 68, 68, 0.15)" : "rgba(34, 197, 94, 0.15)",
                color: feedback.err ? "#ef4444" : "#22c55e",
                border: `1px solid ${feedback.err ? "rgba(239,68,68,0.3)" : "rgba(34,197,94,0.3)"}`,
            }}>
          {feedback.err ? <AlertCircle size={16}/> : <Check size={16}/>}
          <span>{feedback.text}</span>
        </div>)}

      {/* ── Alias Conflict Alert Banner ── */}
      {aliasConflicts.length > 0 && (<div style={{
                padding: uiText("10px 14px"),
                borderRadius: 6,
                fontSize: 13,
                display: "flex",
                alignItems: "center",
                gap: 10,
                background: "rgba(245, 158, 11, 0.15)",
                color: "#f59e0b",
                border: uiText("1px solid rgba(245, 158, 11, 0.3)"),
            }}>
          <AlertCircle size={16}/>
          <div>
            <strong>{uiText("Dikkat - Takma Ad \u00C7ak\u0131\u015Fmas\u0131:")}</strong>{" "}
            {aliasConflicts.map(c => `!${c.alias} ('${c.cmd1}' ve '${c.cmd2}')`).join(", ")}
          </div>
        </div>)}

      {/* ── Tabs: Yerleşik vs Özel Komutlar ── */}
      <div className="studio-command-tabs" style={{ display: "flex", justifyContent: "space-between", alignItems: "center", borderBottom: uiText("1px solid var(--border)"), paddingBottom: 8 }}>
        <div style={{ display: "flex", gap: 8 }}>
          <button type="button" className={`btn btn-sm ${activeTab === "builtin" ? "btn-primary" : "btn-ghost"}`} onClick={() => setActiveTab("builtin")} style={{ fontWeight: 600 }}>{uiText("Yerle\u015Fik Bot Komutlar\u0131 (")}{Object.keys(commands).length})
          </button>
          <button type="button" className={`btn btn-sm ${activeTab === "custom" ? "btn-primary" : "btn-ghost"}`} onClick={() => setActiveTab("custom")} style={{ fontWeight: 600 }}>{uiText("\u00D6zel Sohbet Komutlar\u0131 (")}{Object.keys(customCommands).length})
          </button>
        </div>

        {activeTab === "custom" && (<button type="button" className="btn btn-sm btn-primary" onClick={handleAddCustomCommand} style={{ fontSize: 12 }}>
            <Plus size={14} style={{ marginRight: 4 }}/>{uiText("Yeni \u00D6zel Komut Ekle")}</button>)}
      </div>

      {/* ── TAB 1: Yerleşik Komutlar ── */}
      {activeTab === "builtin" && (<>
          {/* Search & Filter Bar */}
          <div style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
            <div style={{ position: "relative", flex: 1, minWidth: 220 }}>
              <Search size={14} style={{ position: "absolute", left: 10, top: 10, color: "var(--text-muted)" }}/>
              <input type="text" className="input" placeholder={uiText("Komut veya takma ad ara (\u00F6rn: istek, atla, ses, sira)...")} value={search} onChange={e => setSearch(e.target.value)} style={{ paddingLeft: 30, fontSize: 13 }}/>
            </div>

            <div style={{ display: "flex", gap: 4 }}>
              <button type="button" className={`btn btn-sm ${filterRole === "all" ? "btn-primary" : "btn-ghost"}`} onClick={() => setFilterRole("all")}>{uiText("T\u00FCm\u00FC (")}{Object.keys(commands).length})
              </button>
              <button type="button" className={`btn btn-sm ${filterRole === "viewer" ? "btn-primary" : "btn-ghost"}`} onClick={() => setFilterRole("viewer")}>{uiText("\u0130zleyici Komutlar\u0131")}</button>
              <button type="button" className={`btn btn-sm ${filterRole === "admin" ? "btn-primary" : "btn-ghost"}`} onClick={() => setFilterRole("admin")}>{uiText("Mod/Yay\u0131nc\u0131 Komutlar\u0131")}</button>
            </div>
          </div>

          {/* Cards List */}
          {loading ? (<LoadingState label={uiText("Komutlar yükleniyor…")}/>) : filteredCommands.length === 0 ? (<div style={{ textAlign: "center", padding: 30, color: "var(--text-muted)" }}>{uiText("Araman\u0131zla e\u015Fle\u015Fen komut bulunamad\u0131.")}</div>) : (<div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
              {filteredCommands.map(([key, cmd]) => {
                    const isExpanded = expandedCmd === key;
                    return (<div className="studio-command-card" key={key} style={{
                            background: isExpanded ? "rgba(255,255,255,0.03)" : "rgba(255,255,255,0.015)",
                            border: isExpanded ? uiText("1px solid var(--accent, #6366f1)") : uiText("1px solid var(--border)"),
                            borderRadius: 8,
                            overflow: "hidden",
                            transition: uiText("all 0.15s ease"),
                        }}>
                    {/* Header */}
                    <div style={{
                            display: "flex",
                            alignItems: "center",
                            justifyContent: "space-between",
                            padding: uiText("10px 14px"),
                            cursor: "pointer",
                            userSelect: "none",
                            background: isExpanded ? "rgba(255,255,255,0.02)" : "transparent",
                        }} onClick={() => setExpandedCmd(isExpanded ? null : key)}>
                      <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
                        <div role="switch" tabIndex={0} aria-checked={cmd.enabled} aria-label={`${cmd.name || key} komutu`} onKeyDown={e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); e.stopPropagation(); updateCommandField(key, "enabled", !cmd.enabled); } }} className={`settings-toggle${cmd.enabled ? " on" : ""}`} onClick={(e) => {
                            e.stopPropagation();
                            updateCommandField(key, "enabled", !cmd.enabled);
                        }} title={cmd.enabled ? uiText("Komut A\u00E7\u0131k (T\u0131klayarak kapat)") : uiText("Komut Kapal\u0131 (T\u0131klayarak a\u00E7)")} style={{ cursor: "pointer" }}>
                          <div className="settings-toggle-thumb"/>
                        </div>

                        <div style={{ display: "flex", alignItems: "baseline", gap: 8 }}>
                          <code style={{ fontSize: 14, fontWeight: 700, color: cmd.enabled ? "#53fc18" : "var(--text-muted)" }}>
                            !{cmd.name || key}
                          </code>

                          {cmd.aliases && cmd.aliases.length > 0 && (<span style={{ fontSize: 11, color: "var(--text-muted)" }}>
                              ({cmd.aliases.map(a => `!${a.replace(/^!/, "")}`).join(", ")})
                            </span>)}
                        </div>
                      </div>

                      <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                        <span className={`cmd-role-badge cmd-role-${cmd.requiredRole === "moderator" || cmd.requiredRole === "broadcaster" ? "admin" : "viewer"}`}>
                          {cmd.allowedRoles ? cmd.allowedRoles.map(r => ROLE_NAMES[r]).join(", ") || "Engelli" : ROLE_OPTIONS.find(r => r.value === cmd.requiredRole)?.label || cmd.requiredRole}
                        </span>

                        {cmd.cooldownSeconds > 0 && (<span style={{ fontSize: 11, color: "var(--text-muted)", padding: uiText("2px 6px"), background: "rgba(0,0,0,0.2)", borderRadius: 4 }}>
                            {cmd.cooldownSeconds}{uiText("s bekleme")}</span>)}

                        {cmd.maxPerUser > 0 && (<span style={{ fontSize: 11, color: "var(--text-muted)", padding: uiText("2px 6px"), background: "rgba(0,0,0,0.2)", borderRadius: 4 }}>{uiText("maks")}{cmd.maxPerUser}
                          </span>)}

                        <button type="button" className="btn btn-sm btn-ghost" aria-label={`${cmd.name || key} ayarlarını ${isExpanded ? "kapat" : "düzenle"}`} aria-expanded={isExpanded} style={{ padding: uiText("4px 8px") }} onClick={(e) => {
                            e.stopPropagation();
                            setExpandedCmd(isExpanded ? null : key);
                        }}>
                          {isExpanded ? <ChevronUp size={14}/> : <ChevronDown size={14}/>}
                        </button>
                      </div>
                    </div>

                    {/* Detail & Templates Editor */}
                    {isExpanded && (<div style={{
                                padding: 16,
                                borderTop: uiText("1px solid var(--border)"),
                                display: "flex",
                                flexDirection: "column",
                                gap: 14,
                                background: "rgba(0,0,0,0.15)",
                            }}>
                        {/* Row 1: Name, Aliases, Role, Cooldown, Limit */}
                        <div style={{ display: "grid", gridTemplateColumns: uiText("repeat(auto-fit, minmax(180px, 1fr))"), gap: 12 }}>
                          <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                            <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Komut Ad\u0131")}</span>
                            <input type="text" className="input" value={cmd.name || key} onChange={e => updateCommandField(key, "name", e.target.value.trim().replace(/^!/, ""))} style={{ fontSize: 13 }}/>
                          </label>

                          <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                            <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Takma Adlar (Virg\u00FClle ay\u0131r\u0131n)")}</span>
                            <AliasInput aliases={cmd.aliases || []} placeholder={uiText("sr, istek, sarki")} onChange={arr => updateCommandField(key, "aliases", arr)} />
                          </label>

                          <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                            <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Varsay\u0131lan Cooldown (sn)")}</span>
                            <input type="number" min={0} max={300} className="input" value={cmd.cooldownSeconds || 0} onChange={e => updateCommandField(key, "cooldownSeconds", Number(e.target.value))} style={{ fontSize: 13 }}/>
                          </label>
                        </div>

                        {/* Kick Rol Yetki & Cooldown Matrisi */}
                        <KickRoleMatrix cmdKey={key} isPlay={key === "play"} cmd={cmd} roleProfiles={roleProfiles} onUpdateRoleProfile={updateRoleProfile} onUpdateRoleCooldown={(roleKey, sec) => setRoleCooldown(key, roleKey, sec, false)} onSetAllowedRoles={roles => updateCommandField(key, "allowedRoles", roles)}/>

                        {/* Templates Section */}
                        <div style={{ marginTop: 6, display: "flex", flexDirection: "column", gap: 10 }}>
                          <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                            <span style={{ fontSize: 13, fontWeight: 700, color: uiText("var(--accent-light, #a5b4fc)") }}>{uiText("Bot Yan\u0131t \u015Eablonlar\u0131 (Chat Cevaplar\u0131)")}</span>
                            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("Kick sohbet s\u0131n\u0131r\u0131na (500 karaktere) g\u00F6re otomatik dengelenir.")}</span>
                          </div>

                          {cmd.templates && Object.keys(cmd.templates).length > 0 ? (Object.entries(cmd.templates).map(([tplKey, tplVal]) => (<div key={tplKey} style={{
                                    display: "flex",
                                    flexDirection: "column",
                                    gap: 6,
                                    padding: 10,
                                    background: "rgba(0,0,0,0.25)",
                                    borderRadius: 6,
                                    border: uiText("1px solid rgba(255,255,255,0.06)"),
                                }}>
                                <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                                  <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                                    <span style={{ fontSize: 12, fontWeight: 600, color: "var(--text)" }}>
                                      {TEMPLATE_KEY_LABELS[tplKey] || tplKey}
                                    </span>
                                    <code style={{ fontSize: 10, color: "var(--text-muted)" }}>[{tplKey}]</code>
                                  </div>

                                  <button type="button" className="btn btn-sm btn-ghost" style={{ padding: uiText("2px 6px"), color: uiText("var(--color-danger, #ef4444)") }} onClick={() => removeTemplate(key, tplKey)} title={uiText("Bu yanıtı sustur")}>
                                    <Trash2 size={12}/>
                                  </button>
                                </div>

                                <textarea rows={2} className="input" placeholder={uiText("Boş bırakılırsa bu yanıt gönderilmez.")} value={tplVal} onChange={e => updateTemplate(key, tplKey, e.target.value)} style={{ fontSize: 12, lineHeight: 1.4, resize: "vertical" }}/>

                                {/* Variable Insertion Pills */}
                                <div style={{ display: "flex", flexWrap: "wrap", gap: 6, alignItems: "center" }}>
                                  <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("De\u011Fi\u015Fken ekle:")}</span>
                                  {TEMPLATE_VARIABLES.map(v => (<button key={v.tag} type="button" className="btn btn-sm btn-ghost" style={{
                                        fontSize: 10,
                                        padding: uiText("1px 6px"),
                                        background: "rgba(255,255,255,0.05)",
                                        borderRadius: 3,
                                    }} onClick={() => insertVariableIntoTemplate(key, tplKey, v.tag)} title={uiText("\u015Eablona {{v0}} ekle", { v0: v.tag })}>
                                      + {v.tag}
                                    </button>))}
                                </div>
                              </div>))) : (<div style={{ fontSize: 12, color: "var(--text-muted)" }}>{uiText("Bu komut i\u00E7in \u00F6zel bir yan\u0131t \u015Fablonu tan\u0131mlanmam\u0131\u015F.")}</div>)}

                          {/* Add Custom Template */}
                          <div style={{ display: "flex", gap: 8, alignItems: "center", marginTop: 4 }}>
                            <input type="text" className="input" placeholder={uiText("Yeni \u015Fablon anahtar\u0131 (\u00F6rn: custom_msg)...")} value={newTemplateKey} onChange={e => setNewTemplateKey(e.target.value)} style={{ maxWidth: 260, fontSize: 12 }}/>
                            <button type="button" className="btn btn-sm btn-ghost" onClick={() => addTemplate(key, newTemplateKey)} disabled={!newTemplateKey.trim()}>
                              <Plus size={13} style={{ marginRight: 4 }}/>{uiText("\u015Eablon Ekle")}</button>
                          </div>
                        </div>
                      </div>)}
                  </div>);
                })}
            </div>)}
        </>)}

      {/* ── TAB 2: Özel Sohbet Komutları ── */}
      {activeTab === "custom" && (<div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
          {Object.keys(customCommands).length === 0 ? (<div style={{
                    textAlign: "center",
                    padding: uiText("40px 20px"),
                    background: "rgba(255,255,255,0.02)",
                    borderRadius: 8,
                    border: uiText("1px dashed var(--border)"),
                    display: "flex",
                    flexDirection: "column",
                    alignItems: "center",
                    gap: 12
                }}>
              <MessageSquare size={32} style={{ color: "var(--text-muted)", opacity: 0.6 }}/>
              <div>
                <h4 style={{ margin: "0 0 4px 0", fontSize: 15, fontWeight: 600 }}>{uiText("Hen\u00FCz \u00D6zel Komut Eklenmemi\u015F")}</h4>
                <p style={{ margin: 0, fontSize: 13, color: "var(--text-muted)", maxWidth: 440 }}>{uiText("Yay\u0131nc\u0131 ve moderat\u00F6rler i\u00E7in \u00F6zel duyurular, sosyal medya linkleri veya otomatik cevaplar ekleyebilirsiniz.")}</p>
              </div>
              <button type="button" className="btn btn-sm btn-primary" onClick={handleAddCustomCommand} style={{ marginTop: 4 }}>
                <Plus size={14} style={{ marginRight: 4 }}/>{uiText("\u0130lk \u00D6zel Komutunuzu Ekleyin")}</button>
            </div>) : (Object.entries(customCommands).map(([cKey, cmd]) => {
                const preview = renderPreview(cmd.responseText || "");
                return (<div className="studio-panel" key={cKey} style={{
                        background: "rgba(255,255,255,0.02)",
                        border: uiText("1px solid var(--border)"),
                        borderRadius: 8,
                        padding: 16,
                        display: "flex",
                        flexDirection: "column",
                        gap: 14,
                    }}>
                  {/* Top Bar of Custom Command */}
                  <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                    <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
                      <div role="switch" tabIndex={0} aria-checked={cmd.enabled} aria-label={`${cmd.name || cKey} komutu`} onKeyDown={e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); e.stopPropagation(); updateCustomCommand(cKey, "enabled", !cmd.enabled); } }} className={`settings-toggle${cmd.enabled ? " on" : ""}`} onClick={() => updateCustomCommand(cKey, "enabled", !cmd.enabled)} title={cmd.enabled ? uiText("Komut A\u00E7\u0131k") : uiText("Komut Kapal\u0131")} style={{ cursor: "pointer" }}>
                        <div className="settings-toggle-thumb"/>
                      </div>

                      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                        <code style={{ fontSize: 15, fontWeight: 700, color: cmd.enabled ? "#53fc18" : "var(--text-muted)" }}>
                          !{cmd.name || "komut"}
                        </code>
                        {cmd.aliases && cmd.aliases.length > 0 && (<span style={{ fontSize: 11, color: "var(--text-muted)" }}>
                            ({cmd.aliases.map(a => `!${a.replace(/^!/, "")}`).join(", ")})
                          </span>)}
                      </div>
                    </div>

                    <button type="button" className="btn btn-sm btn-ghost" style={{ color: "#ef4444", padding: uiText("4px 8px") }} onClick={() => removeCustomCommand(cKey)} title={uiText("Bu \u00F6zel komutu sil")}>
                      <Trash2 size={14} style={{ marginRight: 4 }}/>{uiText("Sil")}</button>
                  </div>

                  {/* Settings Grid */}
                  <div style={{ display: "grid", gridTemplateColumns: uiText("repeat(auto-fit, minmax(180px, 1fr))"), gap: 12 }}>
                    <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Komut Ad\u0131")}</span>
                      <input type="text" className="input" value={cmd.name} onChange={e => updateCustomCommand(cKey, "name", e.target.value.trim().replace(/^!/, ""))} placeholder="komut" style={{ fontSize: 13 }}/>
                    </label>

                    <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Takma Adlar (Virg\u00FClle ay\u0131r\u0131n)")}</span>
                      <AliasInput aliases={cmd.aliases || []} placeholder={uiText("dc, discord, sunucu")} onChange={arr => updateCustomCommand(cKey, "aliases", arr)} />
                    </label>

                    <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Gereken Rol Yetkisi")}</span>
                      <select className="input" value={cmd.requiredRole || "everyone"} onChange={e => updateCustomCommand(cKey, "requiredRole", e.target.value)} style={{ fontSize: 13 }}>
                        {ROLE_OPTIONS.map(opt => (<option key={opt.value} value={opt.value}>{opt.label}</option>))}
                      </select>
                    </label>

                    <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Varsay\u0131lan Cooldown (sn)")}</span>
                      <input type="number" min={0} max={300} className="input" value={cmd.cooldownSeconds || 0} onChange={e => updateCustomCommand(cKey, "cooldownSeconds", Number(e.target.value))} style={{ fontSize: 13 }}/>
                    </label>
                  </div>

                  {/* Kick Rol Yetki & Cooldown Matrisi */}
                  <KickRoleMatrix cmdKey={cKey} isPlay={false} cmd={cmd} roleProfiles={roleProfiles} onUpdateRoleProfile={updateRoleProfile} onUpdateRoleCooldown={(roleKey, sec) => setRoleCooldown(cKey, roleKey, sec, true)} onSetAllowedRoles={roles => updateCustomCommand(cKey, "allowedRoles", roles)}/>

                  {/* Response Text & Variables */}
                  <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                      <span style={{ fontSize: 12, fontWeight: 600 }}>{uiText("Bot Yan\u0131t Metni (Kick Sohbetine Yaz\u0131lacak Mesaj)")}</span>
                      <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{(cmd.responseText || "").length}{uiText("/ 500 karakter")}</span>
                    </div>

                    <textarea rows={2} className="input" maxLength={500} value={cmd.responseText || ""} onChange={e => updateCustomCommand(cKey, "responseText", e.target.value)} placeholder={uiText("\u00D6rnek: @{kullanici}, Discord sunucumuza kat\u0131lmak i\u00E7in: https://discord.gg/ornek")} style={{ fontSize: 13, lineHeight: 1.4, resize: "vertical" }}/>

                    {/* Variable Pills */}
                    <div style={{ display: "flex", flexWrap: "wrap", gap: 6, alignItems: "center" }}>
                      <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("De\u011Fi\u015Fken ekle:")}</span>
                      {CUSTOM_VARIABLES.map(v => (<button key={v.tag} type="button" className="btn btn-sm btn-ghost" style={{
                            fontSize: 11,
                            padding: uiText("2px 8px"),
                            background: "rgba(255,255,255,0.05)",
                            borderRadius: 4,
                        }} onClick={() => {
                            const cur = cmd.responseText || "";
                            updateCustomCommand(cKey, "responseText", `${cur} ${v.tag}`.trim());
                        }}>
                          + {v.tag}
                        </button>))}
                    </div>
                  </div>

                  {/* Live Simulated Preview */}
                  <div style={{
                        padding: uiText("10px 12px"),
                        background: "rgba(0,0,0,0.35)",
                        borderRadius: 6,
                        border: uiText("1px dashed rgba(255,255,255,0.1)"),
                        display: "flex",
                        alignItems: "flex-start",
                        gap: 10,
                    }}>
                    <Sparkles size={16} style={{ color: "#53fc18", marginTop: 2, flexShrink: 0 }}/>
                    <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
                      <div style={{ fontSize: 11, fontWeight: 700, color: "var(--text-muted)" }}>{uiText("Kick Sohbet \u00D6nizlemesi:")}</div>
                      <div style={{ fontSize: 13, color: "var(--text)", wordBreak: "break-word" }}>
                        <strong style={{ color: "#53fc18" }}>{uiText("Bot:")}</strong>
                        {preview || <span style={{ color: "var(--text-muted)", fontStyle: "italic" }}>{uiText("Yan\u0131t metni girilmedi...")}</span>}
                      </div>
                    </div>
                  </div>
                </div>);
            }))}
        </div>)}
    </fieldset></div>);
};
