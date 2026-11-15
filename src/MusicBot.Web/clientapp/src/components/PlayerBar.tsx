import { usePlayerAction } from "../hooks/usePlayerAction";
import { usePlayerProgress } from "../hooks/usePlayerProgress";
import { uiText } from "../i18n/ui";
import React, { useEffect, useState, useRef, useCallback } from "react";
import { useTranslation } from "react-i18next";
import { SkipBack, Play, Pause, SkipForward, List, Headphones, Volume2, Volume1, VolumeX, Heart, } from "lucide-react";
import { NowPlayingState } from "../types/models";
import { formatDuration, getPlatform } from "../utils";
import { DownloadState } from "../hooks/useSignalR";
import { SongRef } from "./ContextMenu";
import { api } from "../services/api";
interface Props {
    state: NowPlayingState | null;
    onSkip: () => void;
    onPause: () => void;
    onResume: () => void;
    downloadStates?: Record<string, DownloadState>;
    rightPanelMode: "queue" | "nowplaying" | "devices";
    onToggleQueue: () => void;
    onToggleDevices: () => void;
    likedUris?: Set<string>;
    onToggleLike?: (song: SongRef) => void;
}
export const PlayerBar: React.FC<Props> = ({ state, onSkip, onPause, onResume, downloadStates, rightPanelMode, onToggleQueue, onToggleDevices, likedUris, onToggleLike, }) => {
    const { t } = useTranslation();
    const song = state?.spotifyTrack ?? state?.item?.song ?? null;
    const reqBy = state?.item?.requestedBy ?? null;
    const platform = getPlatform(state?.item?.platform ?? undefined);
    const pendingDownload = song
        ? (downloadStates?.[song.spotifyUri] ?? null)
        : null;
    const downloadState = state?.isPlaying || (state?.progressMs ?? 0) > 0 ? null : pendingDownload;
    const {busy:controlBusy,error:playerError,run:runControl,reportError}=usePlayerAction();
    const {localProgress,isSeeking,seekValue,handleSeekChange,handleSeekCommit,handleSeekCancel,handleSeekKeyUp}=usePlayerProgress(state,song,runControl);
    const [volume, setVolume] = useState(1.0);
    const volumeDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);
    const volumeRevision = useRef(0);
    const volumeQuietUntil = useRef(0);
    const volumeDragging = useRef(false);
    const volumeSending = useRef(false);
    const pendingVolume = useRef<number | null>(null);
    const volumeRef = useRef<HTMLInputElement>(null);
    const [showDevicePicker, setShowDevicePicker] = useState(false);
    const [deviceData, setDeviceData] = useState<{
        activeDeviceId: string | null;
        activeSpotifyDeviceId: string | null;
        devices: {
            id: string;
            name: string;
            isDefault: boolean;
            isActive?: boolean;
            type?: "local" | "spotify";
        }[];
    } | null>(null);
    const [deviceLoading, setDeviceLoading] = useState(false);
    const [deviceError, setDeviceError] = useState("");
    const deviceDataRef = useRef(deviceData);
    const deviceRequest = useRef(0);
    deviceDataRef.current = deviceData;
    const loadDevices = useCallback(async (force = false) => {
        const requestId = ++deviceRequest.current;
        if (!deviceDataRef.current)
            setDeviceLoading(true);
        try {
            const data = await api.getAudioDevices(force);
            if (requestId !== deviceRequest.current)
                return;
            setDeviceData(data);
            setDeviceError("");
        }
        catch (e) {
            setDeviceError(e instanceof Error ? e.message : uiText("Ses \u00E7\u0131k\u0131\u015F\u0131 al\u0131namad\u0131"));
        }
        finally {
            if (requestId === deviceRequest.current)
                setDeviceLoading(false);
        }
    }, []);
    useEffect(() => { void loadDevices(); }, [loadDevices]);
    useEffect(() => {
        const refresh = () => { void loadDevices(true); };
        window.addEventListener("player:devices-changed", refresh);
        return () => window.removeEventListener("player:devices-changed", refresh);
    }, [loadDevices]);
    const handleToggleDevicePicker = useCallback(() => {
        if (!showDevicePicker) {
            if (!deviceData)
                loadDevices();
            setShowDevicePicker(true);
        }
        else {
            setShowDevicePicker(false);
        }
    }, [showDevicePicker, loadDevices, deviceData]);
    const handleSelectDevice = async (d: {
        id: string;
        type?: "local" | "spotify";
    }) => {
        const previous = deviceData;
        setDeviceData(old => old ? { ...old, activeDeviceId: d.type === "spotify" ? old.activeDeviceId : d.id, activeSpotifyDeviceId: d.type === "spotify" ? d.id : old.activeSpotifyDeviceId, devices: old.devices.map(item => item.type === d.type ? { ...item, isActive: item.id === d.id } : item) } : old);
        try {
            const result = await api.setAudioDevice(d.id, d.type);
            void loadDevices(true);
            if (result.warning)
                setDeviceError(result.warning);
        }
        catch (e) {
            setDeviceData(previous);
            setDeviceError(e instanceof Error ? e.message : uiText("Ses \u00E7\u0131k\u0131\u015F\u0131 de\u011Fi\u015Ftirilemedi"));
        }
    };
    // Close device picker when clicked outside
    const pickerRef = useRef<HTMLDivElement>(null);
    useEffect(() => {
        if (!showDevicePicker)
            return;
        const handleClickOutside = (e: MouseEvent) => {
            if (pickerRef.current && !pickerRef.current.contains(e.target as Node)) {
                setShowDevicePicker(false);
            }
        };
        document.addEventListener("mousedown", handleClickOutside);
        return () => document.removeEventListener("mousedown", handleClickOutside);
    }, [showDevicePicker]);
    // Persist latest volume so the wheel handler (attached once on mount) always
    // applies its delta on top of the current value, not the captured-at-mount one.
    const volumeStateRef = useRef(1.0);
    useEffect(() => { volumeStateRef.current = volume; }, [volume]);
    useEffect(() => {
        const release = () => { volumeDragging.current = false; };
        document.addEventListener("pointerup", release);
        return () => document.removeEventListener("pointerup", release);
    }, []);
    const flushVolume = useCallback(async () => {
        if (volumeSending.current)
            return;
        volumeSending.current = true;
        try {
            while (pendingVolume.current !== null) {
                const value = pendingVolume.current;
                pendingVolume.current = null;
                try { await api.setVolume(value); }
                catch(e) { pendingVolume.current=null;reportError(e);void api.getVolume().then(r=>setVolume(r.volume)).catch(()=>{});break; }
                volumeQuietUntil.current = Date.now() + 4500;
            }
        }
        finally {
            volumeSending.current = false;
        }
    }, [reportError]);
    const pushVolume = useCallback((next: number) => {
        const clamped = Math.max(0, Math.min(1, next));
        volumeRevision.current++;
        volumeQuietUntil.current = Date.now() + 4500;
        volumeStateRef.current = clamped;
        pendingVolume.current = clamped;
        setVolume(clamped);
        if (volumeDebounce.current)
            clearTimeout(volumeDebounce.current);
        volumeDebounce.current = setTimeout(() => { volumeDebounce.current = null; void flushVolume(); }, 180);
    }, [flushVolume]);
    useEffect(() => {
        let active = true;
        const load = () => {
            if (document.hidden || volumeDragging.current || volumeSending.current || Date.now() < volumeQuietUntil.current)
                return;
            const revision = volumeRevision.current;
            void api.getVolume().then(({ volume: v }) => {
                if (active && revision === volumeRevision.current && !volumeDragging.current && Date.now() >= volumeQuietUntil.current)
                    setVolume(v);
            }).catch(() => { });
        };
        load();
        const timer = setInterval(load, 1500);
        return () => { active = false; clearInterval(timer); };
    }, []);
    useEffect(() => {
        const handler = (e: Event) => {
            const customEvent = e as CustomEvent<number>;
            if (typeof customEvent.detail === "number" && !volumeDragging.current && Date.now() >= volumeQuietUntil.current) {
                setVolume(customEvent.detail);
            }
        };
        window.addEventListener("player:volume", handler);
        return () => window.removeEventListener("player:volume", handler);
    }, []);
    const duration = song?.durationMs ?? 0;
    const displayProg = Math.max(0, Math.min(isSeeking ? seekValue : localProgress, duration || Number.MAX_SAFE_INTEGER));
    const progressPct = duration > 0 ? Math.min(100, (displayProg / duration) * 100) : 0;
    const handleVolume = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
        pushVolume(Number(e.target.value));
    }, [pushVolume]);
    const handlePrev = useCallback(() => {
        void runControl(() => api.previous());
    }, [runControl]);
    const toggleMute = useCallback(() => {
        pushVolume(volumeStateRef.current === 0 ? 1 : 0);
    }, [pushVolume]);
    // Mouse-wheel scroll on the volume control (matches Spotify/VLC/YouTube Music UX).
    // React's synthetic onWheel is passive by default, so preventDefault() inside an
    // onWheel prop has no effect — we attach a native non-passive listener via ref.
    useEffect(() => {
        const el = volumeRef.current;
        if (!el)
            return;
        const onWheel = (e: WheelEvent) => {
            e.preventDefault();
            const stepBase = 0.05; // 5% per notch
            // deltaMode: 0 = pixels, 1 = lines, 2 = pages. Normalize finely for touchpads.
            const magnitude = e.deltaMode === 0 ? Math.min(1, Math.abs(e.deltaY) / 100) : 1;
            const dir = e.deltaY < 0 ? 1 : -1;
            pushVolume(volumeStateRef.current + dir * stepBase * magnitude);
        };
        el.addEventListener("wheel", onWheel, { passive: false });
        return () => el.removeEventListener("wheel", onWheel);
    }, [pushVolume]);
    const queueActive = rightPanelMode === "queue";
    const devicesActive = rightPanelMode === "devices";
    const VolumeIcon = volume === 0 ? VolumeX : volume < 0.5 ? Volume1 : Volume2;
    return (<div className="player-bar" aria-busy={controlBusy}>
      {playerError && <div role="alert" style={{position:"absolute",bottom:"100%",left:0,right:0,padding:"8px 20px",background:"var(--surface)",color:"var(--red)",borderTop:"1px solid var(--border)",fontSize:12}}>{playerError}</div>}

      {/* ── Seek bar (top border) ───────────────────────────── */}
      <div className="pb-seekbar-wrap">
        {downloadState ? (<div className="pb-seekbar-track">
            <div className={`pb-seekbar-fill${downloadState.pct === 0 ? " indeterminate" : ""}`} style={downloadState.pct > 0 ? { width: `${downloadState.pct}%` } : undefined}/>
          </div>) : (<>
            <div className="pb-seekbar-track">
              <div className="pb-seekbar-fill" style={{ width: `${progressPct}%` }}/>
            </div>
            <input className="pb-seekbar-input" aria-label="Çalma konumu" type="range" min={0} max={duration || 1} value={displayProg} onChange={handleSeekChange} onPointerUp={handleSeekCommit} onPointerCancel={handleSeekCancel} onKeyUp={handleSeekKeyUp} disabled={controlBusy || !duration}/>
          </>)}
      </div>

      {/* ── Main row ───────────────────────────────────────── */}
      <div className="pb-row">

        {/* Left: controls + time */}
        <div className="pb-left">
          <div className="pb-controls">
            <button className="pb-ctrl-btn" onClick={handlePrev} disabled={controlBusy} title={t('player.restart')}>
              <SkipBack size={18}/>
            </button>
            {state?.isPlaying ? (<button className="pb-ctrl-btn pb-ctrl-play" onClick={() => void runControl(onPause)} disabled={controlBusy} title={t('player.pause')}>
                <Pause size={20} fill="currentColor"/>
              </button>) : (<button className="pb-ctrl-btn pb-ctrl-play" onClick={() => void runControl(onResume)} disabled={controlBusy} title={t('player.resume')}>
                <Play size={20} fill="currentColor"/>
              </button>)}
            <button className="pb-ctrl-btn" onClick={() => void runControl(onSkip)} disabled={controlBusy} title={t('player.next')}>
              <SkipForward size={18}/>
            </button>
          </div>
          <span className="pb-time-display">
            {formatDuration(displayProg)} / {formatDuration(duration)}
          </span>
        </div>

        {/* Center: cover + info */}
        <div className="pb-center">
          {song ? (<>
              {song.coverUrl
                ? <img src={song.coverUrl} alt="" className="pb-cover"/>
                : <div className="pb-cover pb-cover-ph"><List size={18}/></div>}
              <div className="pb-info">
                <span className="pb-title">{song.title}</span>
                <span className="pb-artist">
                  {song.artist}
                  {reqBy && (<>
                      {platform && <span className={`platform-badge ${platform.className}`} style={{ marginLeft: 6 }}>{platform.label}</span>}
                      <span className="pb-requested"> · {reqBy}</span>
                    </>)}
                </span>
              </div>
              {onToggleLike && (<button className={`pb-like-btn${likedUris?.has(song.spotifyUri) ? " liked" : ""}`} onClick={() => onToggleLike({ spotifyUri: song.spotifyUri, title: song.title, artist: song.artist, coverUrl: song.coverUrl, durationMs: song.durationMs ?? 0 })} title={likedUris?.has(song.spotifyUri) ? t('player.unlike') : t('player.like')}>
                  <Heart size={15} fill={likedUris?.has(song.spotifyUri) ? "currentColor" : "none"}/>
                </button>)}
            </>) : downloadState ? (<>
              <div className="pb-cover pb-cover-dl">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                  <path d={uiText("M19 9h-4V3H9v6H5l7 7 7-7zm-8 2V5h2v6h1.17L12 13.17 9.83 11H11zm-6 7h14v2H5z")}/>
                </svg>
              </div>
              <div className="pb-info">
                <span className="pb-title">{downloadState.title || t('player.downloading')}</span>
                <span className="pb-artist">{downloadState.pct === 0 ? t('player.searchingYouTube') : `${downloadState.pct}%`}</span>
              </div>
            </>) : (<span className="pb-idle">{t('nowPlaying.empty')}</span>)}
        </div>

        {/* Right: extras + volume */}
        <div className="pb-right">
          <button className={`pb-side-btn${queueActive ? " pb-side-btn-active" : ""}`} title={queueActive ? t('player.hideQueue') : t('player.showQueue')} onClick={onToggleQueue}>
            <List size={16}/>
          </button>
          <div className="pb-devices-container" ref={pickerRef} style={{ position: "relative" }}>
            <button className={`pb-side-btn${showDevicePicker || devicesActive ? " pb-side-btn-active" : ""}`} title={t('player.audioDevices')} onClick={handleToggleDevicePicker}>
              <Headphones size={16}/>
            </button>

            {showDevicePicker && (<div className="pb-devices-popover" style={{
                position: "absolute",
                bottom: "100%",
                right: 0,
                marginBottom: 12,
                width: 290,
                maxHeight: 380,
                overflowY: "auto",
                background: uiText("var(--surface2, #18181b)"),
                border: uiText("1px solid var(--border, #27272a)"),
                borderRadius: 10,
                padding: "12px",
                boxShadow: uiText("0 10px 25px rgba(0,0,0,0.5)"),
                zIndex: 100,
                display: "flex",
                flexDirection: "column",
                gap: 8,
            }}>
                <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", borderBottom: uiText("1px solid var(--border, #27272a)"), paddingBottom: 6 }}>
                  <span style={{ fontWeight: 600, fontSize: 13, color: uiText("var(--text-main, #fff)") }}>{uiText("Ses \u00C7\u0131k\u0131\u015F Cihazlar\u0131")}</span>
                  <button className="btn btn-xs btn-ghost" onClick={() => loadDevices(true)} title={uiText("Yenile")} style={{ padding: uiText("2px 6px"), fontSize: 11 }}>{uiText("Yenile")}</button>
                </div>

                {deviceError && <p role="alert" style={{ fontSize: 12, color: uiText("var(--danger, #ef4444)") }}>{deviceError}</p>}
                {deviceLoading && !deviceData ? (<div style={{ padding: "12px 0", textAlign: "center", fontSize: 12, color: uiText("var(--text-dim, #71717a)") }}>{uiText("Cihazlar taran\u0131yor...")}</div>) : (<>
                    {/* Spotify Devices */}
                    {deviceData?.devices.some(d => d.type === "spotify") && (<div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                        <span style={{ fontSize: 11, fontWeight: 600, color: "#1db954", textTransform: "uppercase", letterSpacing: 0.5 }}>{uiText("Spotify Connect Cihazlar\u0131")}</span>
                        {deviceData.devices.filter(d => d.type === "spotify").map(d => {
                        const isCurActive = d.id === deviceData.activeSpotifyDeviceId || d.isActive;
                        return (<button key={d.id} onClick={() => handleSelectDevice(d)} style={{
                                display: "flex",
                                alignItems: "center",
                                justifyContent: "space-between",
                                padding: uiText("8px 10px"),
                                background: isCurActive ? "rgba(29, 185, 84, 0.15)" : "transparent",
                                border: isCurActive ? uiText("1px solid #1db954") : uiText("1px solid transparent"),
                                borderRadius: 6,
                                textAlign: "left",
                                cursor: "pointer",
                                color: isCurActive ? "#1db954" : uiText("var(--text-main, #fff)"),
                                fontSize: 12,
                                transition: uiText("all 0.15s ease"),
                            }}>
                              <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                                {d.name}
                              </span>
                              {isCurActive && <span style={{ fontSize: 13, fontWeight: 700 }}>✓</span>}
                            </button>);
                    })}
                      </div>)}

                    {/* Local Windows WASAPI Devices */}
                    <div style={{ display: "flex", flexDirection: "column", gap: 4, marginTop: 4 }}>
                      <span style={{ fontSize: 11, fontWeight: 600, color: uiText("var(--text-dim, #71717a)"), textTransform: "uppercase", letterSpacing: 0.5 }}>{uiText("Spotify + Musick \u00B7 Bu Bilgisayar")}</span>
                      {deviceData?.devices.filter(d => d.type === "local" || !d.type).map(d => {
                    const isCurActive = (deviceData.activeDeviceId == null && d.isDefault) || deviceData.activeDeviceId === d.id;
                    return (<button key={d.id} onClick={() => handleSelectDevice(d)} style={{
                            display: "flex",
                            alignItems: "center",
                            justifyContent: "space-between",
                            padding: uiText("8px 10px"),
                            background: isCurActive ? "rgba(16, 185, 129, 0.12)" : "transparent",
                            border: isCurActive ? uiText("1px solid var(--accent, #10b981)") : uiText("1px solid transparent"),
                            borderRadius: 6,
                            textAlign: "left",
                            cursor: "pointer",
                            color: isCurActive ? uiText("var(--accent, #10b981)") : uiText("var(--text-main, #fff)"),
                            fontSize: 12,
                            transition: uiText("all 0.15s ease"),
                        }}>
                            <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                              {d.name} {d.isDefault && <small style={{ opacity: 0.6 }}>{uiText("(Varsay\u0131lan)")}</small>}
                            </span>
                            {isCurActive && <span style={{ fontSize: 13, fontWeight: 700 }}>✓</span>}
                          </button>);
                })}
                    </div>
                  </>)}
              </div>)}
          </div>
          <button className="pb-side-btn" onClick={toggleMute} title={t('player.mute')}>
            <VolumeIcon size={16}/>
          </button>
          <input ref={volumeRef} onPointerDown={() => { volumeDragging.current = true; }} onPointerUp={() => { volumeDragging.current = false; }} onPointerCancel={() => { volumeDragging.current = false; }} onBlur={() => { volumeDragging.current = false; }} type="range" className="pb-vol" min={0} max={1} step={0.02} value={volume} onChange={handleVolume} title={t('player.volume')}/>
        </div>
      </div>
    </div>);
};
