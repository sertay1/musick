import { BroadcastHero } from "../components/BroadcastHero";
import { Button } from "../components/ui/button";
import { useBannedSongs } from "../hooks/useBannedSongs";
import { SourceBadge } from "../components/SourceBadge";
import { uiText } from "../i18n/ui";
import { LiveActivityPanel } from "../components/LiveActivityPanel";
import { YouTubePlayer } from "../components/YouTubePlayer";
import { Artwork } from "../components/Artwork";
import React, { useState, useCallback, useRef, useEffect, useMemo, useLayoutEffect } from "react";
import { Radio, Library, Zap, MessageSquare, Tv, Settings, Sun, Moon, Power, FileText, Music2, Search, ArrowUp, ArrowDown, Trash2, X, Check, Volume2, Plus, Shuffle, Download, Filter, Heart, SkipForward, Ban } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useTheme } from "../hooks/useTheme";
import { PlaylistLibraryPanel } from "../components/PlaylistLibraryPanel";
import { AutoQueuePanel } from "../components/AutoQueuePanel";
import { CommandsPanel } from "../components/CommandsPanel";
import { OverlayLinks } from "../components/OverlayLinks";
import { SettingsPanel } from "../components/SettingsPanel";
import { PlayerBar } from "../components/PlayerBar";
import { useSignalR } from "../hooks/useSignalR";
import { useLikedSongs } from "../hooks/useLikedSongs";
import { useConfirm } from "../hooks/useConfirm";
import { api } from "../services/api";
import { formatDuration } from "../utils";
import i18n from "../i18n";
const OVERLAY_TOKEN = "local";
function formatDownloadReason(raw: string): string {
    const prefixed = raw.match(/^yt-dlp failed for ".+?": (.+)$/s);
    const msg = prefixed ? prefixed[1] : raw;
    const t = i18n.t;
    if (/private video/i.test(msg))
        return t("dashboard.downloadReason.private");
    if (/video unavailable/i.test(msg))
        return t("dashboard.downloadReason.unavailable");
    if (/has been removed/i.test(msg))
        return t("dashboard.downloadReason.removed");
    if (/not available in your country/i.test(msg))
        return t("dashboard.downloadReason.region");
    if (/age.?restricted/i.test(msg))
        return t("dashboard.downloadReason.ageRestricted");
    if (/no youtube match/i.test(msg))
        return t("dashboard.downloadReason.noMatch");
    if (/output file not found/i.test(msg))
        return t("dashboard.downloadReason.processError");
    if (/unable to download/i.test(msg))
        return t("dashboard.downloadReason.downloadError");
    if (/copyright/i.test(msg))
        return t("dashboard.downloadReason.copyright");
    return msg.split("\n")[0].trim();
}
type NavPage = "live" | "library" | "autoqueue" | "commands" | "overlays" | "settings";
export const Dashboard: React.FC = () => {
    const { nowPlaying, appQueue, activePlaylistName, connected, kickStatus, integrationEvents, queueSettings, tickerMessages, queueUpdateCount, playlistUpdateCount, downloadStates, downloadErrors, authAlerts, authUpdatedAt, dismissDownloadError, dismissAuthAlert, } = useSignalR(OVERLAY_TOKEN);
    const { t } = useTranslation();
    const { theme, toggle: toggleTheme } = useTheme();
    const { likedUris, toggleLike } = useLikedSongs(playlistUpdateCount);
    const [confirmModal, confirm] = useConfirm();
    // Navigation state
    const [page, setPage] = useState<NavPage>("live");
    useLayoutEffect(() => {
        document.getElementById("main-content")?.scrollTo({ top: 0, left: 0 });
    }, [page]);
    useEffect(() => {
        const navigate = () => setPage("commands");
        const navigateAutoQueue = () => setPage("autoqueue");
        window.addEventListener("navigate-commands", navigate);
        window.addEventListener("navigate-autoqueue", navigateAutoQueue);
        return () => {
            window.removeEventListener("navigate-commands", navigate);
            window.removeEventListener("navigate-autoqueue", navigateAutoQueue);
        };
    }, []);
    // Keep libRefreshKey in sync with server-pushed playlist changes
    const [libRefreshKey, setLibRefreshKey] = useState(0);
    useEffect(() => {
        setLibRefreshKey(k => k + 1);
    }, [playlistUpdateCount]);
    const [appVersion, setAppVersion] = useState<string | null>(null);
    const [kickChannel, setKickChannel] = useState<string | null>(null);
    const [liveKickStatus, setLiveKickStatus] = useState<string | null>(null);
    const { bannedUris, toggleBan } = useBannedSongs();
    // Quick add input
    const [addInput, setAddInput] = useState("");
    const [adding, setAdding] = useState(false);
    const [addFeedback, setAddFeedback] = useState<{
        text: string;
        err: boolean;
    } | null>(null);
    // Inline Playlist / URL Import state
    const [showImportUrl, setShowImportUrl] = useState(false);
    const [importUrl, setImportUrl] = useState("");
    const [importing, setImporting] = useState(false);
    const [importFeedback, setImportFeedback] = useState<{
        text: string;
        err: boolean;
    } | null>(null);
    // Kick Stream filter & clear state
    const [streamFilter, setStreamFilter] = useState<"all" | "success" | "failed" | "cooldown" | "denied">("all");
    const [clearedEventIds, setClearedEventIds] = useState<Set<number>>(new Set());
    const [spotifyAuth, setSpotifyAuth] = useState(false);
    const [ytAuth, setYtAuth] = useState(false);
    const [ytAccount, setYtAccount] = useState<string | null>(null);
    useEffect(() => {
        api.getVersion().then(r => setAppVersion(r.version)).catch(() => { });
    }, []);
    // Fetch Kick platform channel, Spotify status & YouTube status
    useEffect(() => {
        const loadPlatforms = async () => {
            try {
                const platforms = await api.getPlatforms();
                const kick = platforms.find(p => p.platform === "kick");
                if (kick?.config?.channel) {
                    setKickChannel(kick.config.channel);
                }
                if (kick?.status) {
                    setLiveKickStatus(kick.status);
                }
            }
            catch {
                // ignore
            }
            try {
                const s = await api.getSpotifyStatus();
                setSpotifyAuth(s.authenticated);
            }
            catch {
                // ignore
            }
            try {
                const y = await api.getYouTubeAuthStatus();
                setYtAuth(y.authenticated);
                setYtAccount(y.account);
            }
            catch {
                // ignore
            }
        };
        loadPlatforms();
        const timer = setInterval(loadPlatforms, 5000);
        return () => clearInterval(timer);
    }, [authUpdatedAt]);
    const handleSkip = useCallback(() => api.skip("Admin").catch(e => setAddFeedback({ text: String(e.message || e), err: true })), []);
    const handlePause = useCallback(() => api.pause().catch(e => setAddFeedback({ text: String(e.message || e), err: true })), []);
    const handleResume = useCallback(() => api.resume().catch(e => setAddFeedback({ text: String(e.message || e), err: true })), []);
    const prewarmTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const triggerPrewarmDebounced = useCallback(() => {
        if (prewarmTimerRef.current)
            clearTimeout(prewarmTimerRef.current);
        prewarmTimerRef.current = setTimeout(() => api.prewarmNext(2).catch(() => { }), 3000);
    }, []);
    useEffect(() => () => {
        if (prewarmTimerRef.current)
            clearTimeout(prewarmTimerRef.current);
    }, []);
    const handleRemove = useCallback((uri: string) => {
        api.removeQueueItem(uri).catch(console.error);
    }, []);
    const handleBan = useCallback(async (song: { spotifyUri: string; title: string; artist: string }) => {
        const blocked = bannedUris.has(song.spotifyUri);
        try { await toggleBan(song); setAddFeedback({ text: uiText(blocked ? "Şarkının engeli kaldırıldı." : "Şarkı kara listeye alındı. Aynı düğmeden geri alabilirsin."), err: false }); }
        catch (error: any) { setAddFeedback({ text: String(error.message || error), err: true }); }
    }, [bannedUris, toggleBan]);
    const handleReorder = useCallback((uri: string, toIndex: number) => {
        api.reorderQueue(uri, toIndex).catch(console.error);
        triggerPrewarmDebounced();
    }, [triggerPrewarmDebounced]);
    const handleClearQueue = useCallback(async () => {
        const ok = await confirm({
            title: uiText("Kuyru\u011Fu Temizle"),
            message: uiText("Bekleyen tüm şarkılar ve etkin çalma sırası temizlenecek. Çalan şarkı ve kitaplığındaki listeler korunur. Onaylıyor musunuz?"),
            confirmText: uiText("Kuyru\u011Fu Temizle"),
            danger: true,
        });
        if (ok) {
            try { await api.clearPendingQueue(); } catch (error: any) { setAddFeedback({ text: String(error.message || error), err: true }); }
        }
    }, [confirm]);
    const handleToggleShuffle = useCallback(async () => {
        try { await api.shuffleQueue(); } catch (e: any) { setAddFeedback({ text: String(e.message || e), err: true }); return; }
    }, []);
    const handleQuickAdd = async (e?: React.FormEvent) => {
        if (e)
            e.preventDefault();
        const q = addInput.trim();
        if (!q || adding)
            return;
        setAdding(true);
        setAddFeedback(null);
        try {
            const res = await api.play(q, "Admin", "web");
            setAddInput("");
            setAddFeedback({ text: res.message || uiText("\u015Eark\u0131 s\u0131raya eklendi!"), err: !res.success });
            setTimeout(() => setAddFeedback(null), 3500);
        }
        catch (err: unknown) {
            setAddFeedback({ text: err instanceof Error ? err.message : uiText("\u015Eark\u0131 eklenemedi."), err: true });
            setTimeout(() => setAddFeedback(null), 4000);
        }
        finally {
            setAdding(false);
        }
    };
    const handleImportPlaylistUrl = async (e?: React.FormEvent) => {
        if (e)
            e.preventDefault();
        const url = importUrl.trim();
        if (!url || importing)
            return;
        setImporting(true);
        setImportFeedback(null);
        try {
            const res = await api.importPlaylist(url);
            setImportUrl("");
            setImportFeedback({ text: uiText("{{v0}} \u015Fark\u0131 ba\u015Far\u0131yla kuyru\u011Fa aktar\u0131ld\u0131! ({{v1}} atland\u0131)", { v0: res.added, v1: res.skipped }), err: false });
            setTimeout(() => setImportFeedback(null), 4000);
        }
        catch (err: unknown) {
            setImportFeedback({ text: err instanceof Error ? err.message : uiText("\u015Eark\u0131 listesi i\u00E7e aktar\u0131lamad\u0131."), err: true });
            setTimeout(() => setImportFeedback(null), 4000);
        }
        finally {
            setImporting(false);
        }
    };
    const kickEvents = integrationEvents.filter(e => e.source === "kick");
    const filteredKickEvents = useMemo(() => {
        return kickEvents.filter(ev => {
            if (clearedEventIds.has(ev.id))
                return false;
            if (streamFilter === "all")
                return true;
            if (streamFilter === "success")
                return ev.success;
            if (streamFilter === "failed")
                return !ev.success;
            const msg = (ev.message || "").toLowerCase();
            if (streamFilter === "cooldown")
                return msg.includes("bekle") || msg.includes("cooldown");
            if (streamFilter === "denied")
                return msg.includes("yetki") || msg.includes("izin") || msg.includes("denied");
            return true;
        });
    }, [kickEvents, clearedEventIds, streamFilter]);
    const handleClearStream = () => {
        setClearedEventIds(new Set(kickEvents.map(e => e.id)));
    };
    const nowSong = nowPlaying?.spotifyTrack ?? nowPlaying?.item?.song ?? null;
    const currentRequester = nowPlaying?.item?.requestedBy || (nowPlaying?.spotifyTrack ? uiText("Spotify") : "Admin");
    const totalSongsCount = (nowSong ? 1 : 0) + appQueue.length;
    const totalDurationMs = (nowSong?.durationMs || 0) + appQueue.reduce((acc, it) => acc + (it.song?.durationMs || 0), 0);
    const totalMinutes = Math.round(totalDurationMs / 60000);
    const isLiked = (uri?: string) => uri ? likedUris.has(uri) : false;
    return (<div className="app-shell spotify-shell musick-workspace" data-page={page}>
      <a className="skip-link" href="#main-content">İçeriğe geç</a>

      {/* ── Header ──────────────────────────────────────────── */}
      <header className="app-header">
        <div className="workspace-breadcrumb"><span>Çalışma alanı</span><span aria-hidden="true">/</span><strong>{uiText(({live:"Yayın masası",library:"Kütüphane",autoqueue:"Otomatik kuyruk",commands:"Komutlar ve yetkiler",overlays:"Yayın katmanları",settings:"Ayarlar"})[page])}</strong></div>

        {/* Sağ: Bağlantı Durumu Rozetleri + Hızlı Eylemler */}
        <div className="studio-header-tools" style={{ display: "flex", alignItems: "center", gap: 10 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <div className={`header-status-pill status-kick ${(liveKickStatus || kickStatus) === "connected" ? "" : "offline"}`} title={uiText("Kick Sohbet & Bot Ba\u011Flant\u0131s\u0131")}>
              <span className={(liveKickStatus || kickStatus) === "connected" ? "pulse-dot-green" : uiText("status-dot err")}/>
              <span>{uiText("Kick:")}<strong>{kickChannel ? `@${kickChannel}` : ((liveKickStatus || kickStatus) === "connected" ? uiText("Ba\u011Fl\u0131") : uiText("Ba\u011Fl\u0131 De\u011Fil"))}</strong></span>
            </div>

            <div className={`header-status-pill status-spotify ${spotifyAuth ? "" : "offline"}`} title={uiText("Spotify \u00C7alma & Hesap Ba\u011Flant\u0131s\u0131")}>
              <span className={spotifyAuth ? "pulse-dot-green" : uiText("status-dot err")}/>
              <span>{uiText("Spotify:")}<strong>{spotifyAuth ? uiText("Ba\u011Fl\u0131") : uiText("Ba\u011Fl\u0131 De\u011Fil")}</strong></span>
            </div>

            <div className={`header-status-pill status-youtube ${ytAuth ? "" : "offline"}`} title={uiText("YouTube \u00C7alma & Hesap Durumu")}>
              <span className={ytAuth ? "pulse-dot-green" : uiText("status-dot err")}/>
              <span>{uiText("YouTube:")}<strong>{ytAuth ? (ytAccount ? `@${ytAccount}` : uiText("Ba\u011Fl\u0131")) : uiText("Ba\u011Fl\u0131 De\u011Fil")}</strong></span>
            </div>
          </div>

          <button className="theme-toggle-btn" onClick={toggleTheme} title={t("header.toggleTheme")}>
            {theme === "dark" ? <Sun size={15}/> : <Moon size={15}/>}
          </button>
          <button className="header-icon-btn header-icon-btn-danger" onClick={async () => {
            const ok = await confirm({ title: t("dashboard.closeTitle"), message: t("dashboard.closeMessage"), confirmText: t("header.close"), danger: true });
            if (ok)
                api.shutdown();
        }} title={t("header.close")}>
            <Power size={15}/>
          </button>
        </div>
      </header>

      {/* ── Download error toasts ───────────────────────────── */}
      {downloadErrors.length > 0 && (<div className="download-error-stack">
          {downloadErrors.length > 1 && (<div className="download-error-toolbar">
              <span className="download-error-count">{t("dashboard.errors", { count: downloadErrors.length })}</span>
              <button className="download-error-dismiss-all" onClick={() => downloadErrors.forEach(e => dismissDownloadError(e.id))}>
                {t("dashboard.clearAll")}
              </button>
            </div>)}
          {downloadErrors.slice(0, 3).map(e => (<div key={e.id} className="download-error-toast">
              <span className="download-error-icon">⚠</span>
              <div className="download-error-body">
                <span className="download-error-text">
                  {t("dashboard.downloadFailed")} <strong>{e.title}</strong>
                  {e.artist ? ` · ${e.artist}` : ""}
                </span>
                {e.reason && (<span className="download-error-reason">{formatDownloadReason(e.reason)}</span>)}
              </div>
              <button className="download-error-close" onClick={() => dismissDownloadError(e.id)}>✕</button>
            </div>))}
        </div>)}

      {/* ── Auth expiration alerts ──────────────────────────── */}
      {authAlerts.length > 0 && (<div className="download-error-stack">
          {authAlerts.map(a => (<div key={a.id} className="download-error-toast auth-alert-toast">
              <span className="download-error-icon">🔑</span>
              <div className="download-error-body">
                <span className="download-error-text"><strong>{a.platform.charAt(0).toUpperCase() + a.platform.slice(1)}</strong></span>
                <span className="download-error-reason">{a.message}</span>
              </div>
              <button className="download-error-close" onClick={() => dismissAuthAlert(a.id)}>✕</button>
            </div>))}
        </div>)}

      {/* ── Orta Kısım: Sol Sidebar + Sağ Ana İçerik ──────────── */}
      <div className="app-layout-v2">

        {/* ── Sol Gezinme Menüsü (Sidebar) ── */}
        <aside className="sidebar-v2">
          <div className="workspace-brand"><img src="/musick-logo-horizontal.png" alt="Musick"/><span>by SertAy</span></div>
          <nav className="sidebar-v2-nav" aria-label={uiText("Ana Menü")}>
            <div className="sidebar-v2-section-title">{uiText("Ana Men\u00FC")}</div>

            <button title={uiText("Yayın masası")} aria-label={uiText("Yayın masası")} aria-current={page === "live" ? "page" : undefined} className={`sidebar-v2-btn ${page === "live" ? "active" : ""}`} onClick={() => setPage("live")}>
              <Radio size={16}/>
              <span>{uiText("Yayın masası")}</span>
            </button>

            <button title={uiText("Kütüphane & Listeler")} aria-label={uiText("Kütüphane & Listeler")} aria-current={page === "library" ? "page" : undefined} className={`sidebar-v2-btn ${page === "library" ? "active" : ""}`} onClick={() => setPage("library")}>
              <Library size={16}/>
              <span>{uiText("K\u00FCt\u00FCphane & Listeler")}</span>
            </button>

            <button title={uiText("Otomatik Kuyruk")} aria-label={uiText("Otomatik Kuyruk")} aria-current={page === "autoqueue" ? "page" : undefined} className={`sidebar-v2-btn ${page === "autoqueue" ? "active" : ""}`} onClick={() => setPage("autoqueue")}>
              <Zap size={16}/>
              <span>{uiText("Otomatik Kuyruk")}</span>
            </button>

            <div className="sidebar-v2-section-title">{uiText("Yap\u0131land\u0131rma")}</div>

            <button title={uiText("Komutlar & Yetkiler")} aria-label={uiText("Komutlar & Yetkiler")} aria-current={page === "commands" ? "page" : undefined} className={`sidebar-v2-btn ${page === "commands" ? "active" : ""}`} onClick={() => setPage("commands")}>
              <MessageSquare size={16}/>
              <span>{uiText("Komutlar & Yetkiler")}</span>
            </button>

            <button title={uiText("OBS Overlay'leri")} aria-label={uiText("OBS Overlay'leri")} aria-current={page === "overlays" ? "page" : undefined} className={`sidebar-v2-btn ${page === "overlays" ? "active" : ""}`} onClick={() => setPage("overlays")}>
              <Tv size={16}/>
              <span>{uiText("OBS Overlay'leri")}</span>
            </button>

            <button title={uiText("Ayarlar & Platformlar")} aria-label={uiText("Ayarlar & Platformlar")} aria-current={page === "settings" ? "page" : undefined} className={`sidebar-v2-btn ${page === "settings" ? "active" : ""}`} onClick={() => setPage("settings")}>
              <Settings size={16}/>
              <span>{uiText("Ayarlar & Platformlar")}</span>
            </button>
          </nav>
          <div className="studio-sidebar-foot">
            <span className="studio-build">MUSICK <span>{appVersion ? `v${appVersion}` : "DESKTOP"}</span></span>
          </div>
        </aside>

        {/* ── Sağ Ana İçerik Alanı ── */}
        <main id="main-content" className="main-content-v2" tabIndex={-1}>

          {/* SAYFA 1: CANLI YAYIN & KUYRUK */}
          {page === "live" && (<div className="live-desk">

              {/* Üst Başlık & Hızlı Şarkı Ekleme */}
              <div className="live-page-header">
                <div>
                  <span className="studio-eyebrow">{uiText("YAYIN KONTROLÜ")}</span>
                  <h1 style={{ fontSize: 20, fontWeight: 700, margin: 0, color: "var(--text)" }}>{uiText("Yayın masası")}</h1>
                  <p style={{ fontSize: 13, color: "var(--text-muted)", margin: "4px 0 0" }}>{uiText("Kick sohbetinden gelen istekler ve s\u0131radaki \u015Fark\u0131lar burada anl\u0131k listelenir.")}</p>
                </div>

                {/* Hızlı Ekleme Barı */}
                <form className="live-search-bar" onSubmit={handleQuickAdd}>
                  <Search size={18} className="studio-search-icon" aria-hidden="true"/>
                  <input type="text" className="live-search-input" aria-label="Şarkı ara veya bağlantı ekle" placeholder={uiText("\u015Eark\u0131 ad\u0131, sanat\u00E7\u0131 veya link yaz\u0131n...")} value={addInput} onChange={e => setAddInput(e.target.value)} disabled={adding}/>
                  <Button type="submit" className="live-search-btn" disabled={adding || !addInput.trim()}>
                    {adding ? "Ekleniyor..." : <><Plus size={16}/>{uiText("Kuyruğa ekle")}</>}
                  </Button>
                </form>
              </div>

              {addFeedback && (<div className={`browser-flash ${addFeedback.err ? "err" : ""}`} style={{ margin: "0 0 10px 0" }}>
                  {addFeedback.text}
                </div>)}

              <BroadcastHero song={nowSong} playing={nowPlaying?.isPlaying ?? false} requester={currentRequester} count={totalSongsCount} minutes={totalMinutes} connected={connected}/>

              {/* Queue and activity share the same existing state. */}
              <div className="live-page-grid">

                {/* Sol Kolon (7): Canlı Kuyruk Tablosu */}
                <div className="live-card">
                  <div className="live-card-header">
                    <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                      <span className="studio-section-title">{uiText("Çalma sırası")}</span>
                      <span className="studio-queue-count" style={{ fontSize: 11, background: "rgba(16,185,129,0.15)", color: uiText("var(--green, #10b981)"), padding: uiText("2px 8px"), borderRadius: 12, fontWeight: 700 }}>
                        {totalSongsCount} {uiText("\u015Eark\u0131")}</span>
                    </div>

                    <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                      <span style={{ fontSize: 12, color: "var(--text-muted)", fontFamily: "monospace", marginRight: 4 }}>
                        ~{totalMinutes} {uiText("dk")}</span>

                      <button type="button" onClick={() => setShowImportUrl(!showImportUrl)} className={`btn btn-sm ${showImportUrl ? "btn-primary" : "btn-ghost"}`} style={{ fontSize: 11, padding: uiText("3px 8px") }} title={uiText("Toplu URL veya \u00E7alma listesi aktar")}>
                        <Plus size={12} style={{ marginRight: 3 }}/>
                        {showImportUrl ? uiText("Kapat") : uiText("Liste \u0130\u00E7e Aktar")}
                      </button>

                      <button type="button" onClick={handleToggleShuffle} className="btn btn-sm btn-ghost" style={{ fontSize: 11, padding: uiText("3px 8px") }} title={uiText("Kuyru\u011Fu Kar\u0131\u015Ft\u0131r")} disabled={appQueue.length <= 1}>
                        <Shuffle size={12} style={{ marginRight: 3 }}/>{uiText("Kar\u0131\u015Ft\u0131r")}</button>

                      {appQueue.length > 0 && (<button type="button" onClick={handleClearQueue} style={{ fontSize: 11, color: uiText("var(--red, #ef4444)"), background: "transparent", border: "none", cursor: "pointer", fontWeight: 600, padding: uiText("3px 6px") }}>{uiText("Temizle")}</button>)}
                    </div>
                  </div>

                  {/* Inline Playlist / URL Import Panel */}
                  {showImportUrl && (<div style={{
                    padding: uiText("10px 14px"),
                    background: "rgba(0,0,0,0.25)",
                    borderBottom: uiText("1px solid var(--border)"),
                    display: "flex",
                    flexDirection: "column",
                    gap: 8,
                }}>
                      <div style={{ fontSize: 11, color: "var(--text-muted)" }}>{uiText("Spotify veya YouTube \u00E7alma listesi/alb\u00FCm/\u015Fark\u0131 ba\u011Flant\u0131s\u0131n\u0131 yap\u0131\u015Ft\u0131r\u0131n:")}</div>
                      <form onSubmit={handleImportPlaylistUrl} style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                        <input type="text" className="input" placeholder="https://open.spotify.com/playlist/... veya youtube.com/watch?v=..." value={importUrl} onChange={e => setImportUrl(e.target.value)} disabled={importing} style={{ flex: 1, minWidth: 220, fontSize: 12 }}/>
                        <button type="submit" className="btn btn-sm btn-primary" disabled={importing || !importUrl.trim()} style={{ fontSize: 12 }}>
                          {importing ? uiText("Aktar\u0131l\u0131yor...") : uiText("Kuyru\u011Fa Ekle")}
                        </button>
                      </form>
                      {importFeedback && (<div style={{ fontSize: 11, color: importFeedback.err ? "#ef4444" : "#10b981" }}>
                          {importFeedback.text}
                        </div>)}
                    </div>)}

                  <div className="studio-queue-labels" aria-hidden="true"><span>#</span><span>{uiText("PARÇA / SANATÇI")}</span><span>{uiText("İSTEK / SÜRE")}</span></div>
                  <div className="live-queue-list">
                    {!nowSong && appQueue.length === 0 ? (<div className="studio-queue-empty" style={{ padding: uiText("40px 20px"), textAlign: "center", color: "var(--text-muted)" }}>
                        <Music2 size={36} style={{ margin: uiText("0 auto 12px"), opacity: 0.3 }}/>
                        <div style={{ fontWeight: 600, color: "var(--text)", fontSize: 14 }}>{uiText("Kuyruk \u015Fu an bo\u015F")}</div>
                        <div style={{ fontSize: 12, marginTop: 4 }}>{uiText("Kick sohbetinde izleyiciler")}<code>{uiText("!sr [\u015Fark\u0131]")}</code>{uiText("yazarak istek g\u00F6nderebilir ya da yukar\u0131dan ekleyebilirsiniz.")}</div>
                      </div>) : (<>
                        {/* 1. Sıra: Şu An Çalmakta Olan Şarkı */}
                        {nowSong && (<div key="now-playing-row" className="live-queue-row active-now-playing-row" style={{
                        background: uiText("linear-gradient(90deg, rgba(16, 185, 129, 0.14) 0%, rgba(16, 185, 129, 0.03) 100%)"),
                        borderLeft: uiText("3px solid var(--green, #10b981)"),
                        boxShadow: uiText("inset 0 0 14px rgba(16, 185, 129, 0.06)"),
                        position: "relative"
                    }}>
                            <div style={{ display: "flex", alignItems: "center", gap: 12, minWidth: 0 }}>
                              <div style={{ width: 22, display: "flex", alignItems: "center", justifyContent: "center", flexShrink: 0 }}>
                                <span style={{
                        fontSize: 11,
                        fontWeight: 800,
                        color: uiText("var(--green, #10b981)"),
                        background: "rgba(16, 185, 129, 0.2)",
                        padding: uiText("2px 6px"),
                        borderRadius: 6,
                        fontFamily: "monospace"
                    }}>
                                  1
                                </span>
                              </div>

                              {/* Albüm kapağı */}
                              <div style={{ position: "relative", width: 42, height: 42, flexShrink: 0 }}>
                                {nowSong.coverUrl ? (<img src={nowSong.coverUrl} alt="" style={{ width: 42, height: 42, borderRadius: 8, objectFit: "cover", border: uiText("1px solid rgba(16, 185, 129, 0.4)") }}/>) : (<div style={{ width: 42, height: 42, borderRadius: 8, background: "var(--surface2)", display: "flex", alignItems: "center", justifyContent: "center", border: uiText("1px solid var(--border)") }}>
                                    <Music2 size={18} style={{ opacity: 0.4 }}/>
                                  </div>)}
                                <div style={{
                        position: "absolute",
                        bottom: -2,
                        right: -2,
                        background: uiText("var(--green, #10b981)"),
                        color: "#052e16",
                        borderRadius: "50%",
                        width: 15,
                        height: 15,
                        display: "flex",
                        alignItems: "center",
                        justifyContent: "center",
                        fontSize: 8,
                        fontWeight: 900,
                        boxShadow: uiText("0 0 6px rgba(16,185,129,0.8)")
                    }}>
                                  ▶
                                </div>
                              </div>

                              <div style={{ minWidth: 0, overflow: "hidden" }}>
                                <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                                  <span style={{
                        fontSize: 9,
                        fontWeight: 800,
                        textTransform: "uppercase",
                        letterSpacing: "0.06em",
                        color: uiText("var(--green, #10b981)"),
                        background: "rgba(16, 185, 129, 0.15)",
                        padding: uiText("1px 5px"),
                        borderRadius: 4
                    }}>{uiText("\u015Eimdi \u00C7al\u0131yor")}</span>
                                </div>
                                <div style={{ fontSize: 13, fontWeight: 700, color: "var(--text)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis", marginTop: 2 }}>
                                  {nowSong.title || uiText("Bilinmeyen \u015Eark\u0131")}
                                </div>
                                <div style={{ fontSize: 11, color: "var(--text-muted)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
                                  {nowSong.artist || uiText("Bilinmeyen Sanat\u00E7\u0131")}
                                </div>
                              </div>
                            </div>

                            <div style={{ display: "flex", alignItems: "center", gap: 10, flexShrink: 0 }}>
                              <SourceBadge uri={nowSong.spotifyUri}/>
                              <span className="studio-requester" style={{ fontSize: 11, background: "rgba(16, 185, 129, 0.15)", color: uiText("var(--green, #10b981)"), border: uiText("1px solid rgba(16, 185, 129, 0.3)"), padding: uiText("2px 8px"), borderRadius: 6, fontWeight: 600 }}>
                                @{currentRequester}
                              </span>

                              <span style={{ fontSize: 12, color: uiText("var(--green, #10b981)"), fontFamily: "monospace", fontWeight: 700 }}>
                                {formatDuration(nowPlaying?.progressMs || 0)} / {formatDuration(nowSong.durationMs || 0)}
                              </span>

                              <div style={{ display: "flex", alignItems: "center", gap: 4 }}>
                                <button className="live-action-btn" onClick={() => toggleLike(nowSong)} title={isLiked(nowSong.spotifyUri) ? uiText("Favorilerden kald\u0131r") : uiText("Favorilere ekle")} style={{ color: isLiked(nowSong.spotifyUri) ? uiText("var(--red, #ef4444)") : "var(--text-muted)" }}>
                                  <Heart size={14} fill={isLiked(nowSong.spotifyUri) ? "currentColor" : "none"}/>
                                </button>
                                <button type="button" className={`live-action-btn${bannedUris.has(nowSong.spotifyUri) ? " danger" : ""}`} aria-pressed={bannedUris.has(nowSong.spotifyUri)} style={{ color: bannedUris.has(nowSong.spotifyUri) ? "#ef4444" : undefined, background: bannedUris.has(nowSong.spotifyUri) ? "rgba(239,68,68,.18)" : undefined }} onClick={() => handleBan(nowSong)} title={uiText(bannedUris.has(nowSong.spotifyUri) ? "Engeli kaldır" : "Kara listeye al")}><Ban size={14}/></button>
                                <button className="live-action-btn" onClick={handleSkip} title={uiText("S\u0131radaki \u015Eark\u0131ya Ge\u00E7")} style={{ color: uiText("var(--green, #10b981)") }}>
                                  <SkipForward size={14}/>
                                </button>
                              </div>
                            </div>
                          </div>)}

                        {/* Sıradaki Şarkılar (2, 3, 4...) */}
                        {appQueue.map((item, idx) => (<div key={item.id || idx} className="live-queue-row">
                            <div style={{ display: "flex", alignItems: "center", gap: 12, minWidth: 0 }}>
                              <span style={{ fontSize: 12, fontWeight: 700, color: "var(--text-muted)", width: 22, textAlign: "center", fontFamily: "monospace" }}>
                                {(nowSong ? 2 : 1) + idx}
                              </span>

                              {/* Albüm kapağı */}
                              <Artwork trackId={item.song.spotifyUri} coverUrl={item.song.coverUrl} style={{ width: 40, height: 40, borderRadius: 8, objectFit: "cover", flexShrink: 0 }}/>

                              <div style={{ minWidth: 0, overflow: "hidden" }}>
                                <div style={{ fontSize: 13, fontWeight: 700, color: "var(--text)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
                                  {item.song?.title || uiText("Bilinmeyen \u015Eark\u0131")}
                                </div>
                                <div style={{ fontSize: 11, color: "var(--text-muted)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
                                  {item.song?.artist || uiText("Bilinmeyen Sanat\u00E7\u0131")}
                                </div>
                              </div>
                            </div>

                            <div style={{ display: "flex", alignItems: "center", gap: 10, flexShrink: 0 }}>
                              <SourceBadge uri={item.song.spotifyUri}/>
                              <span className="studio-requester" style={{ fontSize: 11, background: "rgba(16, 185, 129, 0.1)", color: uiText("var(--green, #10b981)"), border: uiText("1px solid rgba(16, 185, 129, 0.2)"), padding: uiText("2px 8px"), borderRadius: 6, fontWeight: 600 }}>
                                @{item.requestedBy || "Admin"}
                              </span>

                              <span style={{ fontSize: 12, color: "var(--text-muted)", fontFamily: "monospace" }}>
                                {formatDuration(item.song?.durationMs || 0)}
                              </span>

                              <div style={{ display: "flex", alignItems: "center", gap: 2 }}>
                                <button className="live-action-btn" onClick={() => handleReorder(item.song.spotifyUri, idx - 1)} disabled={idx === 0} title={uiText("Yukar\u0131 ta\u015F\u0131")}>
                                  <ArrowUp size={14}/>
                                </button>
                                <button className="live-action-btn" onClick={() => handleReorder(item.song.spotifyUri, idx + 1)} disabled={idx === appQueue.length - 1} title={uiText("A\u015Fa\u011F\u0131 ta\u015F\u0131")}>
                                  <ArrowDown size={14}/>
                                </button>
                                <button type="button" className={`live-action-btn${bannedUris.has(item.song.spotifyUri) ? " danger" : ""}`} aria-pressed={bannedUris.has(item.song.spotifyUri)} style={{ color: bannedUris.has(item.song.spotifyUri) ? "#ef4444" : undefined, background: bannedUris.has(item.song.spotifyUri) ? "rgba(239,68,68,.18)" : undefined }} onClick={() => handleBan(item.song)} title={uiText(bannedUris.has(item.song.spotifyUri) ? "Engeli kaldır" : "Kara listeye al")}><Ban size={14}/></button>
                                <button className="live-action-btn danger" onClick={() => handleRemove(item.song.spotifyUri)} title={uiText("Kuyruktan kald\u0131r")}>
                                  <X size={15}/>
                                </button>
                              </div>
                            </div>
                          </div>))}
                      </>)}
                  </div>
                </div>

                <LiveActivityPanel />

              </div>
            </div>)}

          {/* SAYFA 2: KÜTÜPHANE & LİSTELER */}
          {page === "library" && (<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div>
                <h1 style={{ fontSize: 20, fontWeight: 700, margin: 0, color: "var(--text)" }}>{uiText("K\u00FCt\u00FCphane & \u00C7alma Listeleri")}</h1>
                <p style={{ fontSize: 13, color: "var(--text-muted)", margin: "4px 0 0" }}>{uiText("Spotify ve yerel \u00E7alma listelerinizi y\u00F6netin, kaydedin veya yay\u0131n i\u00E7in aktif liste olarak belirleyin.")}</p>
              </div>
              <PlaylistLibraryPanel onPlaylistActivated={() => setLibRefreshKey(k => k + 1)}/>
            </div>)}

          {/* SAYFA 3: OTOMATİK KUYRUK */}
          {page === "autoqueue" && (<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div>
                <h1 style={{ fontSize: 20, fontWeight: 700, margin: 0, color: "var(--text)" }}>{uiText("Otomatik Kuyruk (AutoQueue)")}</h1>
                <p style={{ fontSize: 13, color: "var(--text-muted)", margin: "4px 0 0" }}>{uiText("\u0130zleyicilerden istek gelmedi\u011Finde yay\u0131n arka plan\u0131nda \u00E7alacak yedek \u015Fark\u0131 havuzu.")}</p>
              </div>
              <AutoQueuePanel nowPlaying={nowPlaying}/>
            </div>)}

          {/* SAYFA 4: KOMUTLAR & YETKİLER */}
          {page === "commands" && (<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div>
                <h1 style={{ fontSize: 20, fontWeight: 700, margin: 0, color: "var(--text)" }}>{uiText("Komutlar & Sohbet \u0130zinleri")}</h1>
                <p style={{ fontSize: 13, color: "var(--text-muted)", margin: "4px 0 0" }}>{uiText("Kick sohbet komutlar\u0131n\u0131 (!sr, !song, !skip, !queue vb.), bekleme s\u00FCrelerini ve yetkileri \u00F6zelle\u015Ftirin.")}</p>
              </div>
              <CommandsPanel />
            </div>)}

          {/* SAYFA 5: OBS OVERLAY'LERİ */}
          {page === "overlays" && (<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div>
                <h1 style={{ fontSize: 20, fontWeight: 700, margin: 0, color: "var(--text)" }}>{uiText("OBS Studio Canl\u0131 Yay\u0131n Katmanlar\u0131")}</h1>
                <p style={{ fontSize: 13, color: "var(--text-muted)", margin: "4px 0 0" }}>{uiText("OBS veya Streamlabs'e \"Taray\u0131c\u0131 Kayna\u011F\u0131\" olarak ekleyece\u011Finiz \u015F\u0131k \"\u015Eu An \u00C7al\u0131yor\" ve \"Kuyruk\" widget ba\u011Flant\u0131lar\u0131.")}</p>
              </div>
              <OverlayLinks overlayToken={OVERLAY_TOKEN} nowPlaying={nowPlaying} upcoming={appQueue}/>
            </div>)}

          {/* SAYFA 6: AYARLAR & PLATFORMLAR */}
          {page === "settings" && (<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div>
                <h1 style={{ fontSize: 20, fontWeight: 700, margin: 0, color: "var(--text)" }}>{uiText("Ayarlar & Platformlar")}</h1>
                <p style={{ fontSize: 13, color: "var(--text-muted)", margin: "4px 0 0" }}>{uiText("Hesap bağlantılarını, oynatıcıyı ve sistem ayarlarını yönetin.")}</p>
              </div>

              <SettingsPanel settings={queueSettings} onKickUpdated={() => { }}/>
            </div>)}

        </main>
      </div>

      {/* ── Evrensel Alt Oynatıcı Çubuğu (Universal Sticky Bottom Player) ── */}
      <YouTubePlayer />
      <PlayerBar state={nowPlaying} onSkip={handleSkip} onPause={handlePause} onResume={handleResume} downloadStates={downloadStates} rightPanelMode="queue" onToggleQueue={() => setPage("live")} onToggleDevices={() => setPage("settings")} likedUris={likedUris} onToggleLike={toggleLike}/>

      {/* ── Onay Modalı ────────────────────────────────────────── */}
      {confirmModal}
    </div>);
};
