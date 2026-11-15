import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../services/api";
export function useBannedSongs() {
  const [entries, setEntries] = useState<Awaited<ReturnType<typeof api.getBanned>>>([]);
  const pending = useRef(new Set<string>());
  const reload = useCallback(async () => { const fresh = await api.getBanned(); setEntries(previous => [...fresh.filter(entry => !pending.current.has(entry.uri)), ...previous.filter(entry => pending.current.has(entry.uri))]); }, []);
  useEffect(() => { const refresh = () => { void reload().catch(() => {}); }; refresh(); window.addEventListener("musick:banned-changed", refresh); return () => window.removeEventListener("musick:banned-changed", refresh); }, [reload]);
  const bannedUris = new Set(entries.map(entry => entry.uri));
  const toggleBan = useCallback(async (song: { spotifyUri: string; title: string; artist: string }) => {
    if (pending.current.has(song.spotifyUri)) return;
    pending.current.add(song.spotifyUri);
    const previous = entries.find(entry => entry.uri === song.spotifyUri), blocked = !!previous;
    setEntries(current => blocked ? current.filter(entry => entry.uri !== song.spotifyUri) : [...current.filter(entry => entry.uri !== song.spotifyUri), { uri: song.spotifyUri, title: song.title, artist: song.artist, bannedAt: new Date().toISOString() }]);
    try { if (blocked) await api.unbanSong(song.spotifyUri); else await api.banSong(song.spotifyUri, song.title, song.artist); window.dispatchEvent(new Event("musick:banned-changed")); }
    catch (error) { setEntries(current => [...current.filter(entry => entry.uri !== song.spotifyUri), ...(previous ? [previous] : [])]); throw error; }
    finally { pending.current.delete(song.spotifyUri); }
  }, [entries]);
  return { entries, bannedUris, toggleBan, reload };
}
