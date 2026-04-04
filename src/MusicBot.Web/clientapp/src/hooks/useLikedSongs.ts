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
      await api.toggleLiked(song);
    } catch {
      // Revert on error
      reload();
    }
  }, [likedUris, reload]);

  return { likedUris, toggleLike, reloadLiked: reload };
}
