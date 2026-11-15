import { usePlayerAction } from "../hooks/usePlayerAction";
import { usePlayerProgress } from "../hooks/usePlayerProgress";
import React, { useEffect, useState, useRef, useCallback } from "react";
import { useTranslation } from "react-i18next";
import { NowPlayingState } from "../types/models";
import { formatDuration, getPlatform } from "../utils";
import { api } from "../services/api";

type AudioDevice = { id: string; name: string; isDefault: boolean };

interface Props {
  state: NowPlayingState | null;
  onSkip: () => void;
  onPause: () => void;
  onResume: () => void;
  downloadStates?: Record<string, import("../hooks/useSignalR").DownloadState>;
}

export const NowPlaying: React.FC<Props> = ({ state, onSkip, onPause, onResume, downloadStates }) => {
  const { t } = useTranslation();
  const song        = state?.spotifyTrack ?? state?.item?.song ?? null;
  const requestedBy = state?.item?.requestedBy ?? song?.requestedBy ?? null;
  const platform    = getPlatform(state?.item?.platform ?? song?.platform ?? undefined);

  // Resolve download state: match current song, or show first active download when idle
  const downloadState = song
    ? (downloadStates?.[song.spotifyUri] ?? null)
    : (downloadStates ? (Object.values(downloadStates)[0] ?? null) : null);

  // Local progress interpolation
  const {busy:controlBusy,error:playerError,run:runControl,reportError}=usePlayerAction();
  const {localProgress,isSeeking,seekValue,handleSeekChange,handleSeekCommit,handleSeekCancel,handleSeekKeyUp}=usePlayerProgress(state,song,runControl);
  const [volume,        setVolume]        = useState(1.0);
  const [devices,       setDevices]       = useState<AudioDevice[]>([]);
  const [activeDevice,  setActiveDevice]  = useState<string | null>(null);
  const [showDevices,   setShowDevices]   = useState(false);
  const [devicesLoaded, setDevicesLoaded] = useState(false);
  const volumeDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  const duration = song?.durationMs || state?.spotifyTrack?.durationMs || 0;
  const pct      = duration > 0 ? Math.min((localProgress / duration) * 100, 100) : 0;

  // Load persisted volume once on mount
  useEffect(() => {
    api.getVolume().then(({ volume: v }) => setVolume(v)).catch(() => {});
  }, []);

  // Load devices once on mount
  useEffect(() => {
    api.getAudioDevices()
      .then(({ activeDeviceId, devices: list }) => {
        setDevices(list);
        setDevicesLoaded(true);
        setActiveDevice(activeDeviceId);
      })
      .catch(err => console.warn("Could not load audio devices:", err));
  }, []);

  const toggleDevices = useCallback(() => setShowDevices(v => !v), []);

  const handleDeviceChange = useCallback((id: string | null) => {
    setActiveDevice(id);
    setShowDevices(false);
    api.setAudioDevice(id).catch(err => console.error("setAudioDevice failed:", err));
  }, []);

  // Volume handler (debounced API call)
  const handleVolume = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const v = Number(e.target.value);
    setVolume(v);
    if (volumeDebounce.current) clearTimeout(volumeDebounce.current);
    volumeDebounce.current = setTimeout(() => api.setVolume(v).catch(e => {reportError(e);void api.getVolume().then(r=>setVolume(r.volume)).catch(()=>{});}), 150);
  }, [reportError]);

  const deviceDropdown = showDevices && devicesLoaded ? (
    <div className="np-device-menu">
      <div className="np-device-menu-title">{t('nowPlaying.outputDevice')}</div>
      {devices.map(d => (
        <button
          key={d.id}
          className={`np-device-item${activeDevice === d.id ? " active" : ""}`}
          onClick={() => handleDeviceChange(d.id)}
        >
          {d.name}{d.isDefault ? ` (${t('nowPlaying.default')})` : ""}
        </button>
      ))}
      {activeDevice !== null && (
        <button className="np-device-item np-device-default" onClick={() => handleDeviceChange(null)}>
          ↩ {t('nowPlaying.useSystemDefault')}
        </button>
      )}
    </div>
  ) : null;

  const deviceBtn = (
    <div className="np-volume np-volume-device-row">
      <button className="np-device-btn" onClick={toggleDevices}>
        🔈 {activeDevice && devicesLoaded
          ? (devices.find(d => d.id === activeDevice)?.name ?? t('nowPlaying.audioDevice'))
          : t('nowPlaying.audioDevice')}
      </button>
    </div>
  );

  if (!song || !state) {
    return (
      <div className="now-playing-card empty">
        {downloadState ? (
          <div className="np-downloading-empty">
            <div className="np-downloading-empty-badge">⬇ {t('nowPlaying.downloading')}</div>
            <div className="np-downloading-empty-title">{downloadState.title}</div>
            <div className="np-downloading-empty-artist">{downloadState.artist}</div>
            <div className="np-downloading-empty-bar-track">
              <div
                className={`np-downloading-empty-bar-fill${downloadState.pct === 0 ? " indeterminate" : ""}`}
                style={downloadState.pct > 0 ? { width: `${downloadState.pct}%` } : undefined}
              />
            </div>
            <div className="np-downloading-empty-pct">{downloadState.pct}%</div>
            <div className="np-downloading-empty-sub">
              {downloadState.pct === 0 ? t('nowPlaying.searchingYouTube') : t('nowPlaying.downloadingAudio')}
            </div>
          </div>
        ) : (
          <div className="np-empty-text">{t('nowPlaying.empty')}</div>
        )}
        {deviceBtn}
        {deviceDropdown}
      </div>
    );
  }

  const displayProgress = isSeeking ? seekValue : localProgress;
  const displayPct      = duration > 0 ? Math.min((displayProgress / duration) * 100, 100) : 0;

  return (
    <div className="now-playing-card">
      <div className="np-bg" style={{ backgroundImage: `url(${song.coverUrl})` }} />
      <div className="np-bg-dim" />
      <div className="np-content">
        <img src={song.coverUrl} alt="" className="np-cover" />
        <div className="np-details">
          <div className="np-artist">{song.artist}</div>
          <div className="np-title">{song.title}</div>
          {requestedBy && (
            <div className="np-requested-by">
              {platform && (
                <span className={`platform-badge ${platform.className}`}>{platform.label}</span>
              )}
              {" "}{t('nowPlaying.requestedBy')} <strong>{requestedBy}</strong>
            </div>
          )}
          <div className="np-progress">
            {downloadState ? (
              <>
                <div className="np-download-bar-track">
                  <div className="np-download-bar-fill" style={{ width: `${downloadState.pct}%` }} />
                </div>
                <div className="np-time">
                  <span className="np-download-label">
                    ⬇ {downloadState.pct === 0 ? t('nowPlaying.searchingYouTube') : t('nowPlaying.downloadingShort')}
                  </span>
                  <span className="np-download-pct">{downloadState.pct}%</span>
                </div>
              </>
            ) : (
              <>
                <input
                  type="range"
                  className="np-seek-bar"
                  min={0}
                  max={duration}
                  value={isSeeking ? seekValue : localProgress}
                  onChange={handleSeekChange}
                  onPointerUp={handleSeekCommit}
                  onPointerCancel={handleSeekCancel}
                  onKeyUp={handleSeekKeyUp}
                  disabled={controlBusy || !duration}
                />
                <div className="np-time">
                  <span>{formatDuration(displayProgress)}</span>
                  <span>{formatDuration(duration)}</span>
                </div>
              </>
            )}
          </div>
          <div className="np-volume">
            <span className="np-volume-icon">{volume === 0 ? "🔇" : volume < 0.5 ? "🔉" : "🔊"}</span>
            <input
              type="range"
              className="np-volume-bar"
              min={0}
              max={1}
              step={0.02}
              value={volume}
              onChange={handleVolume}
            />
          </div>
          {deviceBtn}
          {deviceDropdown}
        </div>
        {playerError && <div role="alert" style={{color:"var(--red)",fontSize:12}}>{playerError}</div>}
        <div className="np-controls">
          {state.isPlaying ? (
            <button className="btn btn-control" onClick={() => void runControl(onPause)} disabled={controlBusy} title={t('nowPlaying.pause')}>⏸</button>
          ) : (
            <button className="btn btn-control" onClick={() => void runControl(onResume)} disabled={controlBusy} title={t('nowPlaying.resume')}>▶</button>
          )}
          <button className="btn btn-control" onClick={() => void runControl(onSkip)} disabled={controlBusy} title={t('nowPlaying.skip')}>⏭</button>
        </div>
      </div>
    </div>
  );
};
