import { useState, useCallback, useEffect } from "react";
import { api } from "../services/api";
import type { SongRef } from "../components/ContextMenu";

export function useLikedSongs(refreshKey?: number) {
  const [likedUris, setLikedUris] = useState<Set<string>>(new Set());

  const reload = useCallback(async () => {
    try {
      const uris = await api.getLikedUris();
      setLikedUris(new Set(uris));
    } catch { }
  }, []);

  useEffect(() => { reload(); }, [reload, refreshKey]);

  const toggleLike = useCallback(async (song: SongRef) => {
    // Optimistic update
    const wasLiked = likedUris.has(song.spotifyUri);
    setLikedUris(prev => {
      const next = new Set(prev);
      wasLiked ? next.delete(song.spotifyUri) : next.add(song.spotifyUri);
      return next;
    });
    try {
      const res = await api.toggleLiked(song);
      if (res?.spotifyWarning) {
        console.warn("Spotify Beğeni Senkronizasyonu:", res.spotifyWarning);
        window.dispatchEvent(new CustomEvent("musick:notice", { detail: res.spotifyWarning }));
      }
    } catch {
      // Revert on error
      reload();
    }
  }, [likedUris, reload]);

  return { likedUris, toggleLike, reloadLiked: reload };
}
