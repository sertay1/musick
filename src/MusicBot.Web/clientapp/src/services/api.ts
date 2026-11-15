import { localFetch as fetch } from "./localFetch";
import {
  CommandResult,
  NowPlayingState,
  MeResponse,
  SpotifyQueueState,
  PlatformState,
  HistoryItem,
  LibraryTrack,
  PlaylistLibrary,
  PlaylistLibrarySong,
  PlaylistMembership,
} from "../types/models";
import type { TickerMessage } from "../hooks/useSignalR";

const BASE = import.meta.env.VITE_API_URL || "";

const writeQueues = new Map<string, Promise<unknown>>();
function request<T>(url: string, options?: RequestInit): Promise<T> {
  if (options?.method !== "PUT") return sendRequest<T>(url, options);
  const previous = writeQueues.get(url) ?? Promise.resolve();
  const next = previous.catch(() => {}).then(() => sendRequest<T>(url, options));
  writeQueues.set(url, next);
  void next.finally(() => { if (writeQueues.get(url) === next) writeQueues.delete(url); }).catch(() => {});
  return next;
}
const controlRequests = new Map<string,Promise<unknown>>();
function controlRequest<T>(url:string,options:RequestInit):Promise<T> {
  const key=url+String(options.body??""); const existing=controlRequests.get(key);
  if(existing)return existing as Promise<T>;
  const pending=request<T>(url,options);controlRequests.set(key,pending);
  void pending.finally(()=>{if(controlRequests.get(key)===pending)controlRequests.delete(key);}).catch(()=>{});
  return pending;
}
async function sendRequest<T>(url: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${url}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...(options?.headers ?? {}) },
  });

  if (!res.ok) {
    const text = await res.text().catch(() => "");
    let body: any = {};
    try { body = JSON.parse(text); } catch {}
    throw new Error(body.error || body.message || (text && text.length < 200 ? text : `Request failed (${res.status})`));
  }

  if (res.status === 204) return undefined as T;
  const text = await res.text();
  if (!text || !text.trim()) return undefined as T;
  try {
    return JSON.parse(text) as T;
  } catch {
    return undefined as T;
  }
}

type AudioDeviceData = {
      activeDeviceId: string | null;
      activeSpotifyDeviceId: string | null;
      devices: { id: string; name: string; isDefault: boolean; isActive?: boolean; type?: "local" | "spotify" }[];
    };
let cachedAudioDevices: { at: number; data: AudioDeviceData } | null = null;
let audioDeviceRevision = 0;
let audioDevicesPending: { revision: number; promise: Promise<AudioDeviceData> } | null = null;
function loadAudioDevices(force: boolean): Promise<AudioDeviceData> {
  if (!force && cachedAudioDevices && Date.now() - cachedAudioDevices.at < 60000) return Promise.resolve(cachedAudioDevices.data);
  if (audioDevicesPending?.revision === audioDeviceRevision) return audioDevicesPending.promise;
  const revision = audioDeviceRevision;
  const promise = request<AudioDeviceData>("/api/player/devices").then(data => { if (revision === audioDeviceRevision) cachedAudioDevices = { at: Date.now(), data }; return data; }).finally(() => { if (audioDevicesPending?.promise === promise) audioDevicesPending = null; });
  audioDevicesPending = { revision, promise };
  return promise;
}

export const api = {
  getOverlayPreferences: () => request<Record<string, unknown>>("/api/overlay-preferences"),
  setOverlayPreferences: (value: unknown) => request<void>("/api/overlay-preferences", { method: "PUT", body: JSON.stringify(value) }),
  setAutoQueueEnabled: (enabled: boolean) => request<void>("/api/settings/autoqueue", { method: "PUT", body: JSON.stringify({ enabled }) }),
  importLibraryToAutoQueue: (id: number) => request<{added: number; total: number}>(`/api/autoqueue/library/${id}`, { method: "POST" }),
  openVolumeMixer: () => request<void>("/api/app/volume-mixer", { method: "POST" }),
  getActivity: () => request<{ id: number; timestamp: string; source: string; kind: string; user: string; message: string; success: boolean }[]>("/api/activity"),
  setDesktopTheme: (theme: string) => request<void>("/api/app/theme", { method: "POST", body: JSON.stringify({ theme, language: localStorage.getItem("musick-language") || "tr" }) }),
  // Info
  me: () => request<MeResponse>("/api/auth/me"),

  getSpotifyAuthUrl:   () => request<{ url: string }>("/api/auth/spotify"),
  getSpotifyStatus:    () => request<{ authenticated: boolean; libraryAccess?: boolean | null }>("/api/auth/spotify/status"),
  disconnectSpotify:   () => request<void>("/api/auth/spotify", { method: "DELETE" }),

  // Queue
  getNowPlaying: () => request<NowPlayingState>("/api/queue/now-playing"),

  // Commands
  searchProgressive: async (q: string, limit: number, onResults: (songs: import("../types/models").Song[]) => void, signal?: AbortSignal) => {
    const groups: import("../types/models").Song[][] = [[], []];
    await Promise.allSettled(["spotify", "youtube"].map(async (source, index) => {
      const data = await request<import("../types/models").Song[]>(`/api/search?q=${encodeURIComponent(q)}&limit=${Math.min(limit, 10)}&source=${source}`, { signal });
      if (signal?.aborted) return;
      groups[index] = data;
      const combined: import("../types/models").Song[] = [];
      for (let i = 0; i < Math.max(groups[0].length, groups[1].length); i++) for (const group of groups) if (group[i]) combined.push(group[i]);
      onResults(combined.slice(0, limit));
    }));
  },
  search: (q: string, limit = 5) =>
    request<import("../types/models").Song[]>(`/api/search?q=${encodeURIComponent(q)}&limit=${limit}`),

  getPlaylistTracks: (url: string, limit = 2147483647) =>
    request<import("../types/models").Song[]>(`/api/search/playlist-tracks?url=${encodeURIComponent(url)}&limit=${limit}`),

  play: (query: string, requestedBy: string, platform?: string) =>
    request<CommandResult>("/api/play", {
      method: "POST",
      body: JSON.stringify({ query, requestedBy, platform }),
    }),

  skip: (requestedBy: string) =>
    controlRequest<CommandResult>("/api/skip", {
      method: "POST",
      body: JSON.stringify({ requestedBy }),
    }),

  pause:  () => controlRequest<void>("/api/pause",  { method: "POST" }),
  resume: () => controlRequest<void>("/api/resume", { method: "POST" }),
  previous: () => controlRequest<void>("/api/player/previous", { method: "POST" }),

  getVolume: () =>
    request<{ volume: number }>("/api/player/volume"),

  setVolume: (volume: number) =>
    request<void>("/api/player/volume", { method: "POST", body: JSON.stringify({ volume }) }),

  seek: (positionMs: number, expectedTrackUri?: string) =>
    request<void>("/api/player/seek", { method: "POST", body: JSON.stringify({ positionMs, expectedTrackUri }) }),

  getAudioDevices: (force = false) => loadAudioDevices(force),
  setAudioDevice: async (deviceId: string | null, type?: "local" | "spotify") => {
    const result = await request<{ warning?: string }>("/api/player/device", { method: "POST", body: JSON.stringify({ deviceId, type }) });
    cachedAudioDevices = null;
    audioDeviceRevision++;
    window.dispatchEvent(new Event("player:devices-changed"));
    return result;
  },

  getHistory: (limit = 50) => request<HistoryItem[]>(`/api/history?limit=${limit}`),
  clearHistory: () => request<void>("/api/history", { method: "DELETE" }),

  // Library
  getLibrary:        ()           => request<LibraryTrack[]>("/api/library"),
  deleteTrack:       (id: number) => request<void>(`/api/library/${id}`, { method: "DELETE" }),
  clearLibrary:      ()           => request<void>("/api/library", { method: "DELETE" }),
  openLibraryFolder: ()           => request<{ path: string }>("/api/library/open-folder", { method: "POST" }),

  startAutoQueue: () =>
    request<CommandResult>("/api/queue/start-auto", { method: "POST" }),

  shuffleQueue: () =>
    request<{ message: string }>("/api/queue/shuffle", { method: "POST" }),

  shuffleBackgroundPlaylist: () =>
    request<void>("/api/queue/shuffle-background", { method: "POST" }),

  promoteFromBackground: (uri: string, toIndex?: number) =>
    request<void>("/api/queue/promote-from-background", {
      method: "POST",
      body: JSON.stringify({ uri, toIndex }),
    }),

  prewarmNext: (count = 2) =>
    request<void>(`/api/queue/prewarm-next?count=${count}`, { method: "POST" }),

  // Queue management
  clearPendingQueue: () => request<void>("/api/queue/pending", { method: "DELETE" }),
  clearUserQueue: () =>
    request<void>("/api/queue/user", { method: "DELETE" }),

  removeQueueItem: (uri: string) =>
    request<void>("/api/queue/item", {
      method: "DELETE",
      body: JSON.stringify({ uri }),
    }),

  moveQueueItem: (uri: string, direction: "up" | "down") =>
    request<void>("/api/queue/move", {
      method: "POST",
      body: JSON.stringify({ uri, direction }),
    }),

  playNow: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }, requestedBy?: string) =>
    request<CommandResult>("/api/queue/play-now", {
      method: "POST",
      body: JSON.stringify({ ...song, requestedBy: requestedBy ?? "Admin", platform: "web" }),
    }),

  enqueueTrack: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }, requestedBy?: string) =>
    request<CommandResult>("/api/queue/enqueue", {
      method: "POST",
      body: JSON.stringify({ ...song, requestedBy: requestedBy ?? "Admin", platform: "web" }),
    }),

  reorderQueue: (uri: string, toIndex: number) =>
    request<void>("/api/queue/reorder", {
      method: "POST",
      body: JSON.stringify({ uri, toIndex }),
    }),

  importPlaylist: (url: string, requestedBy?: string) =>
    request<{ added: number; skipped: number; total: number }>("/api/queue/import-playlist", {
      method: "POST",
      body: JSON.stringify({ url, requestedBy }),
    }),

  // Liked Songs
  getLikedUris: () => request<string[]>("/api/liked/uris"),

  toggleLiked: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) =>
    request<{ isLiked: boolean; spotifyWarning?: string }>("/api/liked/toggle", {
      method: "POST",
      body: JSON.stringify({ spotifyUri: song.spotifyUri, title: song.title, artist: song.artist, coverUrl: song.coverUrl, durationMs: song.durationMs }),
    }),

  getSongMemberships: (uri: string) =>
    request<PlaylistMembership[]>(`/api/liked/memberships?uri=${encodeURIComponent(uri)}`),

  toggleMembership: (playlistId: number, song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) =>
    request<{ isInPlaylist: boolean }>(`/api/liked/memberships/${playlistId}`, {
      method: "POST",
      body: JSON.stringify({ spotifyUri: song.spotifyUri, title: song.title, artist: song.artist, coverUrl: song.coverUrl, durationMs: song.durationMs }),
    }),

  openSpotify: () => request<void>("/api/app/spotify/open", { method: "POST" }),

  // Banned songs
  getBanned: () =>
    request<{ uri: string; title: string; artist: string; bannedAt: string }[]>("/api/banned"),

  banSong: (uri: string, title: string, artist: string) =>
    request<void>("/api/banned", {
      method: "POST",
      body: JSON.stringify({ uri, title, artist }),
    }),

  unbanSong: (uri: string) =>
    request<void>(`/api/banned/${encodeURIComponent(uri)}`, { method: "DELETE" }),

  // Vote
  vote: (username: string, skip: boolean, platform?: string) =>
    request<CommandResult>("/api/vote", {
      method: "POST",
      body: JSON.stringify({ username, skip, platform }),
    }),

  // Settings
  getSettings: () => request<{ chatPlaylistRequestsEnabled?: boolean; spotifyEnabled?: boolean; youTubeEnabled?: boolean; youTubeVolumePercent?: number; maxQueueSize: number; maxSongsPerUser: number; votingEnabled: boolean; presenceCheckEnabled: boolean; presenceCheckWarningSeconds: number; presenceCheckConfirmSeconds: number; saveDownloads: boolean; autoQueueEnabled: boolean; loudnessNormalizationEnabled: boolean; directSpotifyMode: boolean; notificationsEnabled?: boolean; openLogOnStart: boolean }>("/api/settings"),
  updateSettings: (s: { chatPlaylistRequestsEnabled?: boolean; spotifyEnabled?: boolean; youTubeEnabled?: boolean; youTubeVolumePercent?: number; maxQueueSize: number; maxSongsPerUser: number; votingEnabled: boolean; presenceCheckEnabled: boolean; presenceCheckWarningSeconds: number; presenceCheckConfirmSeconds: number; saveDownloads: boolean; autoQueueEnabled: boolean; loudnessNormalizationEnabled: boolean; directSpotifyMode: boolean; notificationsEnabled?: boolean; openLogOnStart: boolean }) =>
    request<void>("/api/settings", { method: "PUT", body: JSON.stringify(s) }),

  // Ticker messages
  getTicker: () => request<TickerMessage[]>("/api/ticker"),
  addTicker: (msg: Omit<TickerMessage, "id" | "order">) =>
    request<TickerMessage>("/api/ticker", { method: "POST", body: JSON.stringify(msg) }),
  updateTicker: (id: string, msg: Omit<TickerMessage, "id" | "order">) =>
    request<void>(`/api/ticker/${id}`, { method: "PUT", body: JSON.stringify(msg) }),
  deleteTicker: (id: string) =>
    request<void>(`/api/ticker/${id}`, { method: "DELETE" }),

  // AutoQueue
  getAutoQueue: () => request<{ id: number; spotifyUri: string; title: string; artist: string; coverUrl: string; durationMs: number }[]>("/api/autoqueue"),
  addAutoQueueSong: (song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) =>
    request<void>("/api/autoqueue", { method: "POST", body: JSON.stringify(song) }),
  removeAutoQueueSong: (uri: string) =>
    request<void>(`/api/autoqueue/${encodeURIComponent(uri)}`, { method: "DELETE" }),
  clearAutoQueue: () => request<void>("/api/autoqueue", { method: "DELETE" }),
  importAutoQueue: (url: string) =>
    request<{ added: number; total: number }>("/api/autoqueue/import", { method: "POST", body: JSON.stringify({ url }) }),

  // Platforms
  getPlatforms: () => request<PlatformState[]>("/api/platforms"),

  // YouTube in-app login (cookies for yt-dlp bot detection bypass)
  startYouTubeLogin:    () => request<{ message: string }>("/api/auth/youtube/start", { method: "POST" }),
  getYouTubeAuthStatus: () => request<{ configured: boolean; enabled: boolean; authenticated: boolean; account: string | null; savedAt: string | null; cancelled: boolean; error?: string }>("/api/auth/youtube/status"),
  enableYouTubeAuth:    () => request<void>("/api/auth/youtube/enable",  { method: "POST" }),
  disableYouTubeAuth:   () => request<void>("/api/auth/youtube/disable", { method: "POST" }),
  disconnectYouTubeAuth: () => request<void>("/api/auth/youtube", { method: "DELETE" }),

  // Open a URL in the user's default system browser (for OAuth flows)
  openInBrowser: (url: string) =>
    request<void>(`/api/auth/open-in-browser?url=${encodeURIComponent(url)}`, { method: "POST" }),

  // Kick OAuth
  getKickAuthUrl:   () => request<{ url: string }>("/api/auth/kick"),
  getKickStatus:    () => request<{ authenticated: boolean; channel: string | null }>("/api/auth/kick/status"),
  disconnectKick:   () => request<void>("/api/auth/kick", { method: "DELETE" }),

  saveKick: (channel: string, autoConnect: boolean, commandRoles: string[] = ["all"], allowedUsers: string[] = []) =>
    request<void>("/api/platforms/kick", {
      method: "PUT",
      body: JSON.stringify({ channel, autoConnect, commandRoles, allowedUsers }),
    }),

  connectPlatform:    (platform: string) =>
    request<void>(`/api/platforms/${platform}/connect`,    { method: "POST" }),

  disconnectPlatform: (platform: string) =>
    request<void>(`/api/platforms/${platform}/disconnect`, { method: "POST" }),

  forgetPlatform: (platform: string) =>
    request<void>(`/api/platforms/${platform}/forget`, { method: "POST" }),

  // Playlist Library
  getPlaylists: () =>
    request<PlaylistLibrary[]>("/api/playlists"),

  createPlaylist: (name: string) =>
    request<PlaylistLibrary>("/api/playlists", { method: "POST", body: JSON.stringify({ name }) }),

  deletePlaylist: (id: number) =>
    request<void>(`/api/playlists/${id}`, { method: "DELETE" }),

  renamePlaylist: (id: number, name: string) =>
    request<void>(`/api/playlists/${id}/rename`, { method: "PUT", body: JSON.stringify({ name }) }),

  getPlaylistSongs: (id: number) =>
    request<PlaylistLibrarySong[]>(`/api/playlists/${id}/songs`),

  addPlaylistSong: (id: number, song: { spotifyUri: string; title: string; artist: string; coverUrl?: string; durationMs: number }) =>
    request<void>(`/api/playlists/${id}/songs`, { method: "POST", body: JSON.stringify(song) }),

  removePlaylistSong: (id: number, uri: string) =>
    request<void>(`/api/playlists/${id}/songs/${encodeURIComponent(uri)}`, { method: "DELETE" }),

  importPlaylistSongs: (id: number, url: string, userProvidedName?: string) =>
    request<{ added: number; skipped: number; total: number; name?: string }>(`/api/playlists/${id}/import`, { method: "POST", body: JSON.stringify({ url, userProvidedName }) }),

  activatePlaylist: (id: number, shuffle = false) =>
    request<{ message: string }>(`/api/playlists/${id}/play`, {
      method: "POST",
      body: JSON.stringify({ shuffle }),
    }),

  playSongFromPlaylist: (playlistId: number, uri: string, shuffle = false) =>
    request<{ message: string }>(`/api/playlists/${playlistId}/songs/${encodeURIComponent(uri)}/play`, {
      method: "POST",
      body: JSON.stringify({ shuffle }),
    }),

  deactivatePlaylist: () =>
    request<void>("/api/playlists/active", { method: "DELETE" }),

  togglePlaylistPin: (id: number) =>
    request<{ isPinned: boolean }>(`/api/playlists/${id}/pin`, { method: "POST" }),

  reorderPins: (ids: number[]) =>
    request<void>("/api/playlists/pins/reorder", { method: "PUT", body: JSON.stringify({ ids }) }),

  reorderPlaylists: (ids: number[]) =>
    request<void>("/api/playlists/reorder", { method: "PUT", body: JSON.stringify({ ids }) }),

  reorderPlaylistSong: (playlistId: number, spotifyUri: string, toIndex: number) =>
    request<void>(`/api/playlists/${playlistId}/songs/reorder`, {
      method: "PUT",
      body: JSON.stringify({ spotifyUri, toIndex }),
    }),

  syncYouTubePlaylists: (channelUrl?: string) => request<{ count: number; message: string }>("/api/playlists/sync-youtube", { method: "POST", body: JSON.stringify({ channelUrl }) }),
  syncSpotifyPlaylists: () =>
    request<{ message: string; count: number }>("/api/playlists/sync-spotify", { method: "POST" }),

  bulkDeletePlaylistSongs: (playlistId: number, uris: string[]) =>
    request<{ removed: number }>(`/api/playlists/${playlistId}/songs/bulk-delete`, {
      method: "POST",
      body: JSON.stringify({ uris }),
    }),

  bulkEnqueuePlaylistSongs: (playlistId: number, uris: string[]) =>
    request<{ enqueued: number }>(`/api/playlists/${playlistId}/songs/bulk-enqueue`, {
      method: "POST",
      body: JSON.stringify({ uris }),
    }),

  // Commands Config
  getCommandsConfig: () => request<Record<string, unknown>>("/api/commands/config"),
  updateCommandsConfig: (config: unknown) => request<unknown>("/api/commands/config", { method: "PUT", body: JSON.stringify(config) }),
  importCommandsConfig: async (document: unknown) => {
    await (writeQueues.get("/api/commands/config") ?? Promise.resolve()).catch(() => {});
    return request<unknown>("/api/commands/config/import", { method: "POST", body: JSON.stringify(document) });
  },
  reloadCommandsConfig: () => request<unknown>("/api/commands/config/reload", { method: "POST" }),

  exportSettings: async () => {
    await Promise.allSettled([...writeQueues.values()]);
    return request<unknown>("/api/settings/backup");
  },
  importSettings: async (document: unknown) => {
    await Promise.allSettled([...writeQueues.values()]);
    return request<{appearance: {theme: string; language: string}; warnings: string[]}>("/api/settings/backup/import", { method: "POST", body: JSON.stringify(document) });
  },
  // Credentials
  revealCredential: (platform: "kick" | "spotify") => request<{ secret: string }>(`/api/settings/credentials/${platform}/reveal`, { method: "POST" }),
  getCredentials: () => request<{
    kickClientId: string;
    kickClientSecret: string;
    kickClientSecretConfigured: boolean;
    kickRedirectUri: string;
    spotifyClientId: string;
    spotifyClientSecret: string;
    spotifyClientSecretConfigured: boolean;
    spotifyRedirectUri: string;
  }>("/api/settings/credentials"),
  updateCredentials: (data: {
    kickClientId?: string;
    kickClientSecret?: string;
    spotifyClientId?: string;
    spotifyClientSecret?: string;
  }) => request<void>("/api/settings/credentials", { method: "PUT", body: JSON.stringify(data) }),

  // App
  shutdown:      () => request<void>("/api/app/shutdown", { method: "POST" }),
  openLog:       () => request<void>("/api/app/open-log",     { method: "POST" }),
  openLogDir:    () => request<void>("/api/app/open-log-dir", { method: "POST" }),
  getVersion:    () => request<{ version: string }>("/api/app/version"),
  updateYtDlp:   () => request<{ version: string; message: string }>("/api/app/yt-dlp/update", { method: "POST" }),
};
