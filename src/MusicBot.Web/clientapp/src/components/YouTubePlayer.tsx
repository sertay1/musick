import { localFetch as fetch } from "../services/localFetch";
import { Minimize2, Maximize2 } from "lucide-react";
import { uiText } from "../i18n/ui";
import { useEffect, useRef, useState } from "react";
type State = { version: number; uri: string | null; isPlaying: boolean; positionMs: number; durationMs: number; volume: number; requestedPlaying?: boolean };
type Command = { action: string; state: State };
declare global { interface Window { YT?: any; onYouTubeIframeAPIReady?: () => void; musickYouTubeReady?: Promise<void> } }
function ready() {
    if (window.YT?.Player) return Promise.resolve();
    if (window.musickYouTubeReady) return window.musickYouTubeReady;
    window.musickYouTubeReady = new Promise<void>((resolve, reject) => {
        const script = document.createElement("script");
        const failed = () => { clearTimeout(timeout); script.remove(); reject(new Error("YouTube player unavailable")); };
        const timeout = setTimeout(failed, 15000);
        window.onYouTubeIframeAPIReady = () => { clearTimeout(timeout); resolve(); };
        script.src = "https://www.youtube.com/iframe_api"; script.onerror = failed; document.head.appendChild(script);
    }).catch(error => { window.musickYouTubeReady = undefined; throw error; });
    return window.musickYouTubeReady;
}
export function YouTubePlayer() {
    const container = useRef<HTMLDivElement>(null), player = useRef<any>(null);
    const current = useRef<State | null>(null), pending = useRef<Command | null>(null);
    const confirmedVersion = useRef(-1);
    const [active, setActive] = useState(false), [error, setError] = useState("");
    const [needsGesture, setNeedsGesture] = useState(false);
    const [compact, setCompact] = useState(() => { try { return localStorage.getItem("musick:youtube-compact") === "1"; } catch { return false; } });
    const toggleSize = () => setCompact(value => { try { localStorage.setItem("musick:youtube-compact", value ? "0" : "1"); } catch { } return !value; });
    useEffect(() => {
        let alive = true, reporting = false, initializing = false, playerReady = false, routedVersion = -1;
        let watchdog: ReturnType<typeof setTimeout> | undefined;
        const clearWatchdog = () => { if (watchdog) clearTimeout(watchdog); watchdog = undefined; };
        const armWatchdog = (version: number) => {
            clearWatchdog();
            watchdog = setTimeout(() => { if (alive && current.current?.version === version && current.current?.uri && current.current.requestedPlaying !== false && player.current?.getPlayerState?.() !== 1) void report(1001).catch(() => {}); }, 15000);
        };
        const report = async (code?: number) => {
            const state = current.current, frame = player.current;
            if (!alive || !state?.uri || reporting) return;
            const frameId = frame?.getVideoData?.().video_id;
            // Rejected embeds often have no video metadata at all. Still report their error.
            if (!code && frameId !== state.uri.slice(8)) return;
            const playing = frame?.getPlayerState?.() === 1;
            if (playing) {
                confirmedVersion.current = state.version;
                if (routedVersion !== state.version) { routedVersion = state.version; void fetch("/api/player/refresh-output", { method: "POST" }).catch(() => {}); }
            }
            reporting = true;
            try {
                const response = await fetch("/api/youtube-player/status", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({
                    state: { ...state, positionMs: Math.round((frame?.getCurrentTime?.() || 0) * 1000), durationMs: Math.round((frame?.getDuration?.() || 0) * 1000), isPlaying: playing },
                    ended: frame?.getPlayerState?.() === 0 && confirmedVersion.current === state.version, error: code
                }) });
                if (response.status === 409 && !code && alive) window.dispatchEvent(new CustomEvent("youtube:command", { detail: await response.json() }));
            } finally { reporting = false; }
        };
        const apply = (command: Command) => {
            if (current.current && command.state.version < current.current.version) return;
            current.current = command.state; pending.current = command;
            setActive(!!command.state.uri);
            const { state, action } = command;
            if (!state.uri || action === "stop") { clearWatchdog(); pending.current = null; player.current?.stopVideo?.(); setNeedsGesture(false); return; }
            if (action === "pause" || state.requestedPlaying === false) clearWatchdog();
            else if (action === "load" || action === "resume" || action === "restore") armWatchdog(state.version);
            if (!playerReady) { initialize(); return; }
            const frame = player.current; pending.current = null;
            if (action === "load" || frame.getVideoData?.().video_id !== state.uri.slice(8)) {
                confirmedVersion.current = -1; setError(""); setNeedsGesture(false); frame.setVolume(Math.round(state.volume * 100));
                const video = { videoId: state.uri.slice(8), startSeconds: state.positionMs / 1000 };
                if (state.requestedPlaying === false) frame.cueVideoById(video); else frame.loadVideoById(video);
            } else if (action === "pause") frame.pauseVideo();
            else if (action === "resume") frame.playVideo();
            else if (action === "seek") frame.seekTo(state.positionMs / 1000, true);
            else if (action === "restore") { if (state.requestedPlaying === false) frame.pauseVideo(); else frame.playVideo(); }
            else if (action === "volume") frame.setVolume(Math.round(state.volume * 100));
        };
        const initialize = () => {
            if (initializing || player.current || !alive) return;
            initializing = true;
            void ready().then(() => {
                if (!alive || !container.current) return;
                const target = document.createElement("div"); container.current.appendChild(target);
                player.current = new window.YT.Player(target, { width: "100%", height: "200", host: "https://www.youtube.com",
                    playerVars: { origin: window.location.origin, playsinline: 1, autoplay: 1, widget_referrer: window.location.href }, events: {
                        onReady: (event: any) => { playerReady = true; event.target.getIframe?.()?.setAttribute("referrerpolicy", "strict-origin-when-cross-origin"); if (pending.current) apply(pending.current); },
                        onStateChange: (event: any) => { if (event.data === 1) { clearWatchdog(); setError(""); setNeedsGesture(false); } else if (event.data === 3 && current.current?.requestedPlaying !== false && current.current) armWatchdog(current.current.version); void report().catch(() => {}); },
                        onAutoplayBlocked: () => { clearWatchdog(); setNeedsGesture(true); },
                        onError: (event: any) => { setError(uiText("YouTube bu videoyu çevrimiçi oynatamadı. Yeniden deneyin veya sıradakine geçin.")); void report(event.data).catch(() => {}); }
                    }
                });
            }).catch(() => { initializing = false; if (alive) { setError(uiText("YouTube çevrimiçi oynatıcı yüklenemedi. Bağlantınızı kontrol edip yeniden deneyin.")); void report(1000).catch(() => {}); } });
        };
        const receive = (event: Event) => apply((event as CustomEvent<Command>).detail);
        const restore = () => { void fetch("/api/youtube-player").then(r => r.json()).then((state: State) => { if (alive) apply({ action: state.uri ? "restore" : "stop", state }); }).catch(() => {}); };
        window.addEventListener("youtube:command", receive); window.addEventListener("musick:reconnected", restore); restore();
        const timer = setInterval(() => { void report().catch(() => {}); }, 1000);
        return () => { alive = false; clearWatchdog(); clearInterval(timer); window.removeEventListener("youtube:command", receive); window.removeEventListener("musick:reconnected", restore); player.current?.destroy?.(); player.current = null; };
    }, []);
    return <aside style={{ display: active ? "block" : "none", position: "fixed", right: 18, bottom: 100, width: compact ? 200 : 320, boxSizing: "content-box", zIndex: 35, borderRadius: 10, overflow: "hidden", background: "var(--surface)", border: "1px solid var(--border)" }} aria-label={uiText("YouTube oynatıcı")}>
        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8, padding: "6px 8px", fontSize: 11 }}>
            <span>YouTube</span>
            <button type="button" className="btn btn-sm btn-ghost" aria-label={uiText(compact ? "YouTube oynatıcısını büyüt" : "YouTube oynatıcısını küçült")} title={uiText(compact ? "YouTube oynatıcısını büyüt" : "YouTube oynatıcısını küçült")} aria-pressed={compact} onClick={toggleSize} style={{ padding: 4 }}>{compact ? <Maximize2 size={14}/> : <Minimize2 size={14}/>}</button>
        </div>
        <div ref={container} style={{ minHeight: 200 }}/>
        {needsGesture && <button className="btn btn-sm" onClick={() => player.current?.playVideo?.()}>{uiText("YouTube oynatmayı başlat")}</button>}
        {error && <p role="status" style={{ padding: 10, color: "var(--text-muted)" }}>{error}</p>}
    </aside>;
}
