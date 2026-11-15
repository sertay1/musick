import { LoadingState, ErrorState } from "./PanelState";
import { uiText } from "../i18n/ui";
import { Artwork } from "./Artwork";
import { SourceBadge } from "./SourceBadge";
import React, { useState, useEffect, useCallback, useRef } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../services/api";
import { PlaylistLibrary, PlaylistLibrarySong, Song } from "../types/models";
import { formatDuration } from "../utils";
import { useConfirm } from "../hooks/useConfirm";
interface Props {
    onPlaylistActivated?: () => void;
}
export const PlaylistLibraryPanel: React.FC<Props> = ({ onPlaylistActivated }) => {
    const { t } = useTranslation();
    const [confirmModal, confirm] = useConfirm();
    const [playlists, setPlaylists] = useState<PlaylistLibrary[]>([]);
    const [listError, setListError] = useState("");
    const [selectedId, setSelectedId] = useState<number | null>(null);
    const [songs, setSongs] = useState<PlaylistLibrarySong[]>([]);
    const [loadingList, setLoadingList] = useState(true);
    const [loadingSongs, setLoadingSongs] = useState(false);
    const [msg, setMsg] = useState("");
    const [msgErr, setMsgErr] = useState(false);
    // New playlist
    const [newName, setNewName] = useState("");
    const [creating, setCreating] = useState(false);
    // Rename
    const [renamingId, setRenamingId] = useState<number | null>(null);
    const [renameValue, setRenameValue] = useState("");
    // Search to add songs
    const [query, setQuery] = useState("");
    const [results, setResults] = useState<Song[]>([]);
    const searchRequest = useRef<AbortController | null>(null);
    const closeSearch = () => { searchRequest.current?.abort(); setResults([]); setSearching(false); setSearchMsg(""); };
    useEffect(() => () => searchRequest.current?.abort(), []);
    const [searching, setSearching] = useState(false);
    const [searchMsg, setSearchMsg] = useState("");
    // Import
    const [importUrl, setImportUrl] = useState("");
    const [importing, setImporting] = useState(false);
    // Playback
    const [activating, setActivating] = useState(false);
    // Spotify sync
    const [syncingSpotify, setSyncingSpotify] = useState(false);
    const [syncingYouTube, setSyncingYouTube] = useState(false);
    const handleSyncYouTube = async () => {
        setSyncingYouTube(true);
        try {
            const result = await api.syncYouTubePlaylists();
            showMsg(result.message);
            await loadPlaylists();
        }
        catch (e) {
            showMsg(e instanceof Error ? e.message : uiText("YouTube listeleri al\u0131namad\u0131"), true);
        }
        finally {
            setSyncingYouTube(false);
        }
    };
    // Multi-select batch actions
    const [selectedUris, setSelectedUris] = useState<Set<string>>(new Set());
    // Drag-and-drop reorder
    const [dragSongUri, setDragSongUri] = useState<string | null>(null);
    const [dropSongIdx, setDropSongIdx] = useState<number | null>(null);
    const dragSongIdxRef = useRef<number>(-1);
    const showMsg = (text: string, err = false) => {
        setMsg(text);
        setMsgErr(err);
        setTimeout(() => setMsg(""), 3500);
    };
    const handleSyncSpotify = async () => {
        setSyncingSpotify(true);
        try {
            const res = await api.syncSpotifyPlaylists();
            showMsg(res.message);
            await loadPlaylists();
        }
        catch (e: unknown) {
            showMsg(e instanceof Error ? e.message : uiText("Spotify listeleri e\u015Fitlenemedi"), true);
        }
        finally {
            setSyncingSpotify(false);
        }
    };
    const toggleSelectAll = () => {
        if (selectedUris.size === songs.length) {
            setSelectedUris(new Set());
        }
        else {
            setSelectedUris(new Set(songs.map(s => s.spotifyUri)));
        }
    };
    const toggleSelectOne = (uri: string) => {
        setSelectedUris(prev => {
            const next = new Set(prev);
            if (next.has(uri))
                next.delete(uri);
            else
                next.add(uri);
            return next;
        });
    };
    const handleBulkDelete = async () => {
        if (selectedUris.size === 0 || selectedId == null)
            return;
        const ok = await confirm({
            title: uiText("Se\u00E7ilenleri Sil"),
            message: uiText("{{v0}} \u015Fark\u0131y\u0131 bu \u00E7alma listesinden silmek istedi\u011Finize emin misiniz?", { v0: selectedUris.size }),
            confirmText: "Toplu Sil",
            danger: true,
        });
        if (ok) {
            await api.bulkDeletePlaylistSongs(selectedId, Array.from(selectedUris));
            const count = selectedUris.size;
            setSelectedUris(new Set());
            loadSongs(selectedId);
            loadPlaylists();
            showMsg(uiText("{{v0}} \u015Fark\u0131 listeden silindi.", { v0: count }));
        }
    };
    const handleBulkEnqueue = async () => {
        if (selectedUris.size === 0 || selectedId == null)
            return;
        await api.bulkEnqueuePlaylistSongs(selectedId, Array.from(selectedUris));
        showMsg(uiText("{{v0}} \u015Fark\u0131 oynatma kuyru\u011Funa eklendi.", { v0: selectedUris.size }));
    };
    const loadPlaylists = useCallback(async () => {
        setLoadingList(true);
        setListError("");
        try {
            const list = await api.getPlaylists();
            setPlaylists(list);
        }
        catch {
            setListError("Çalma listeleri alınamadı. Bağlantıyı kontrol edip tekrar deneyin.");
        }
        finally {
            setLoadingList(false);
        }
    }, []);
    useEffect(() => { loadPlaylists(); }, [loadPlaylists]);
    const loadSongs = useCallback(async (id: number) => {
        setLoadingSongs(true);
        setSongs([]);
        try {
            const list = await api.getPlaylistSongs(id);
            setSongs(list);
        }
        catch {
            showMsg(t("library.errLoadSongs"), true);
        }
        finally {
            setLoadingSongs(false);
        }
    }, []);
    const handleSelectPlaylist = (id: number) => {
        setSelectedId(id);
        setSelectedUris(new Set());
        setResults([]);
        setQuery("");
        setSearchMsg("");
        loadSongs(id);
    };
    // ── Create playlist ────────────────────────────────────────────────────────
    const handleCreate = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!newName.trim())
            return;
        setCreating(true);
        try {
            const p = await api.createPlaylist(newName.trim());
            setNewName("");
            await loadPlaylists();
            setSelectedId(p.id);
            setSongs([]);
        }
        catch (ex: unknown) {
            showMsg(ex instanceof Error ? ex.message : t("library.errCreate"), true);
        }
        finally {
            setCreating(false);
        }
    };
    // ── Delete playlist ────────────────────────────────────────────────────────
    const handleDelete = async (id: number, name: string) => {
        const ok = await confirm({ title: t("library.confirmDeleteTitle", { name }), message: t("library.confirmDeleteMsg"), confirmText: t("common.delete"), danger: true });
        if (!ok)
            return;
        try {
            await api.deletePlaylist(id);
            if (selectedId === id) {
                setSelectedId(null);
                setSongs([]);
            }
            await loadPlaylists();
        }
        catch {
            showMsg(t("library.errDeleteList"), true);
        }
    };
    // ── Rename playlist ────────────────────────────────────────────────────────
    const handleRenameSubmit = async (id: number) => {
        if (!renameValue.trim())
            return;
        try {
            await api.renamePlaylist(id, renameValue.trim());
            setRenamingId(null);
            await loadPlaylists();
        }
        catch (ex: unknown) {
            showMsg(ex instanceof Error ? ex.message : t("library.errRename"), true);
        }
    };
    // ── Search ─────────────────────────────────────────────────────────────────
    const handleSearch = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!query.trim())
            return;
        setSearching(true);
        setSearchMsg("");
        setResults([]);
        searchRequest.current?.abort();
        const controller = new AbortController();
        searchRequest.current = controller;
        try {
            await api.searchProgressive(query.trim(), 10, hits => {
                if (!controller.signal.aborted) {
                    setResults(hits);
                    setSearchMsg(hits.length ? "" : t("common.noResults"));
                }
            }, controller.signal);
        }
        catch {
            setSearchMsg(t("library.errSearch"));
        }
        finally {
            if (searchRequest.current === controller)
                setSearching(false);
        }
    };
    const handleAddSong = async (song: Song) => {
        if (selectedId == null)
            return;
        try {
            await api.addPlaylistSong(selectedId, song);
            closeSearch();
            setQuery("");
            showMsg(`✓ ${t("library.songAdded", { title: song.title })}`);
            await loadSongs(selectedId);
            await loadPlaylists();
        }
        catch (ex: unknown) {
            showMsg(ex instanceof Error ? ex.message : t("library.errAddSong"), true);
        }
    };
    // ── Reorder song ───────────────────────────────────────────────────────────
    const handleReorderSong = async (uri: string, toIndex: number) => {
        if (selectedId == null)
            return;
        // Optimistic update
        setSongs(prev => {
            const next = [...prev];
            const fromIndex = next.findIndex(s => s.spotifyUri === uri);
            if (fromIndex < 0)
                return prev;
            const [moved] = next.splice(fromIndex, 1);
            next.splice(toIndex, 0, moved);
            return next;
        });
        try {
            await api.reorderPlaylistSong(selectedId, uri, toIndex);
        }
        catch {
            showMsg(t("library.errReorder"), true);
            await loadSongs(selectedId);
        }
    };
    // ── Remove song ────────────────────────────────────────────────────────────
    const handleRemoveSong = async (uri: string) => {
        if (selectedId == null)
            return;
        try {
            await api.removePlaylistSong(selectedId, uri);
            setSongs(s => s.filter(x => x.spotifyUri !== uri));
            await loadPlaylists();
        }
        catch {
            showMsg(t("library.errRemoveSong"), true);
        }
    };
    // ── Import ─────────────────────────────────────────────────────────────────
    const handleImport = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!importUrl.trim() || selectedId == null)
            return;
        setImporting(true);
        try {
            const res = await api.importPlaylistSongs(selectedId, importUrl.trim());
            showMsg(`✓ ${t("library.songsImportedOf", { added: res.added, total: res.total })}`);
            setImportUrl("");
            await loadSongs(selectedId);
            await loadPlaylists();
        }
        catch (ex: unknown) {
            showMsg(ex instanceof Error ? ex.message : t("library.errImport"), true);
        }
        finally {
            setImporting(false);
        }
    };
    // ── Play ───────────────────────────────────────────────────────────────────
    const handlePlay = async (id: number) => {
        setActivating(true);
        try {
            const res = await api.activatePlaylist(id);
            showMsg(res.message);
            await loadPlaylists();
            onPlaylistActivated?.();
        }
        catch (ex: unknown) {
            showMsg(ex instanceof Error ? ex.message : t("library.errPlay"), true);
        }
        finally {
            setActivating(false);
        }
    };
    const handleDeactivate = async () => {
        try {
            await api.deactivatePlaylist();
            showMsg(t("library.playlistStopped"));
            await loadPlaylists();
        }
        catch {
            showMsg(t("library.errStop"), true);
        }
    };
    const selectedPlaylist = playlists.find(p => p.id === selectedId) ?? null;
    if (listError && !playlists.length) return <ErrorState message={listError} retry={() => void loadPlaylists()}/>;
    return (<div className="playlist-panel">
      {confirmModal}

      {/* ── Left: playlist list ───────────────────────────────────────── */}
      <div className="playlist-sidebar">
        <div className="playlist-sidebar-header" style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <span className="queue-section-label" style={{ marginBottom: 0 }}>{t("library.myPlaylists")}</span>
        </div>
        <div style={{ display: "flex", gap: 6, padding: "8px 0" }}>
          <button type="button" className="btn btn-xs" onClick={handleSyncSpotify} disabled={syncingSpotify} title={uiText("Eri\u015Febildi\u011Finiz t\u00FCm Spotify listelerini \u015Fark\u0131 s\u0131n\u0131r\u0131 olmadan al")} style={{ fontSize: 11, padding: uiText("3px 8px"), background: "rgba(30, 215, 96, 0.15)", color: "var(--green)", border: uiText("1px solid rgba(30, 215, 96, 0.3)") }}>
            {syncingSpotify ? uiText("Al\u0131n\u0131yor\u2026") : uiText("Spotify Listeleri")}
          </button>
          <button className="btn btn-xs" style={{ fontSize: 11, padding: uiText("3px 8px"), background: "rgba(239,68,68,.15)", color: "var(--red)", border: uiText("1px solid rgba(239,68,68,.3)") }} disabled={syncingYouTube} onClick={handleSyncYouTube}>{syncingYouTube ? uiText("Al\u0131n\u0131yor\u2026") : uiText("YouTube Listeleri")}</button>
        </div>
        {/* Create new */}
        <form className="playlist-create-form" onSubmit={handleCreate}>
          <input className="input input-sm" placeholder={t("library.newPlaylistPlaceholder")} value={newName} onChange={e => setNewName(e.target.value)} disabled={creating} autoComplete="off"/>
          <button type="submit" className="btn btn-primary btn-sm" disabled={creating || !newName.trim()}>
            +
          </button>
        </form>

        {loadingList ? (<LoadingState label={t("common.loading")}/>) : playlists.length === 0 ? (<div className="lib-empty" style={{ padding: uiText("24px 12px"), fontSize: 13 }}>
            {t("library.noPlaylists")}
          </div>) : (<div className="playlist-list">
            {playlists.map(p => (<div key={p.id} className={`playlist-list-item ${selectedId === p.id ? "active" : ""}`} role="button" tabIndex={0} aria-pressed={selectedId === p.id} onKeyDown={e => { if (e.target === e.currentTarget && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); handleSelectPlaylist(p.id); } }} onClick={() => handleSelectPlaylist(p.id)}>
                {renamingId === p.id ? (<form className="playlist-rename-form" onSubmit={e => { e.preventDefault(); handleRenameSubmit(p.id); }} onClick={e => e.stopPropagation()}>
                    <input className="input input-sm" value={renameValue} onChange={e => setRenameValue(e.target.value)} autoFocus onKeyDown={e => {
                        if (e.key === "Escape")
                            setRenamingId(null);
                    }}/>
                    <button type="submit" className="btn btn-sm btn-primary">✓</button>
                    <button type="button" className="btn btn-sm btn-secondary" onClick={() => setRenamingId(null)}>✕</button>
                  </form>) : (<>
                    <div className="playlist-list-item-info">
                      {p.isActive && <span className="playlist-active-dot" title={t("library.reproducing")}/>}
                      <span className="playlist-list-item-name">{p.name}</span>
                      {p.syncError && <span title={p.syncError} style={{ color: "#f59e0b" }}>⚠</span>}
                      <span className="playlist-list-item-count">{p.songCount}</span>
                    </div>
                    <div className="playlist-list-item-actions" onClick={e => e.stopPropagation()}>
                      <button className="btn-icon-muted" title={t("library.rename")} onClick={() => { setRenamingId(p.id); setRenameValue(p.name); }}>
                        ✏
                      </button>
                      <button className="btn-icon-danger" title={t("common.delete")} onClick={() => handleDelete(p.id, p.name)}>
                        ✕
                      </button>
                    </div>
                  </>)}
              </div>))}
          </div>)}
      </div>

      {/* ── Right: playlist detail ────────────────────────────────────── */}
      <div className="playlist-detail">
        {selectedPlaylist == null ? (<div className="lib-empty">{t("library.selectPlaylist")}</div>) : (<>
            {/* Header */}
            {selectedPlaylist.syncError && <div role="status" style={{ padding: 12, color: "#f59e0b", border: uiText("1px solid #f59e0b60"), borderRadius: 8, marginBottom: 12 }}>{selectedPlaylist.syncError} {selectedPlaylist.sourceUrl && <a href={selectedPlaylist.sourceUrl} target="_blank" rel="noreferrer">{uiText("Spotify'da a\u00E7")}</a>}</div>}
            <div className="playlist-detail-header">
              <div className="playlist-detail-title">
                <span className="tab-pane-title" style={{ fontSize: 16 }}>{selectedPlaylist.name}</span>
                <span className="count-chip">{selectedPlaylist.songCount} {t("library.songsLabel")}</span>
              </div>
              <div className="playlist-detail-actions">
                {selectedPlaylist.isActive ? (<button className="btn btn-sm btn-danger-outline" onClick={handleDeactivate}>
                    ⏹ {t("library.stop")}
                  </button>) : (<button className="btn btn-sm btn-primary" onClick={() => handlePlay(selectedPlaylist.id)} disabled={activating || selectedPlaylist.songCount === 0}>
                    {activating ? t("library.starting") : `▶ ${t("library.playPlaylist")}`}
                  </button>)}
              </div>
            </div>

            {msg && (<div className={`form-message${msgErr ? " form-message-error" : ""}`} style={{ margin: "0 0 8px" }}>
                {msg}
              </div>)}

            {/* Import from URL */}
            <div className="autoqueue-add-section">
              <div className="queue-section-label">{t("library.importFromYoutube")}</div>
              <form className="form-row" onSubmit={handleImport}>
                <input className="input" placeholder={t("library.importUrlPlaceholderList")} value={importUrl} onChange={e => setImportUrl(e.target.value)} disabled={importing} autoComplete="off"/>
                <button type="submit" className="btn btn-primary" style={{ whiteSpace: "nowrap" }} disabled={importing || !importUrl.trim()}>
                  {importing ? t("library.importing") : t("library.import")}
                </button>
              </form>
            </div>

            {/* Search to add */}
            <div className="autoqueue-add-section">
              <div className="queue-section-label">{t("library.searchAddSong")}</div>
              <form className="form-row" onSubmit={handleSearch}>
                <input className="input" placeholder={t("library.searchPlaceholderShort")} value={query} onChange={e => { closeSearch(); setQuery(e.target.value); }} onKeyDown={e => {
                if (e.key === "Escape")
                    closeSearch();
            }} autoComplete="off"/>
                <button type="submit" className="btn btn-primary" style={{ whiteSpace: "nowrap" }} disabled={searching || !query.trim()}>
                  {searching ? t("library.searching") : t("common.search")}
                </button>
              </form>

              {results.length > 0 && (<div className="search-results-list">
                  <button className="btn btn-sm btn-ghost" onClick={closeSearch}>{uiText("Sonu\u00E7lar\u0131 kapat")}</button>
                  {results.map(song => (<div key={song.spotifyUri} className="search-result-row">
                      {song.coverUrl && <img src={song.coverUrl} alt="" className="search-result-cover"/>}
                      <div className="search-result-info">
                        <SourceBadge uri={song.spotifyUri}/>
                        <span className="search-result-title">{song.title}</span>
                        <span className="search-result-artist">{song.artist} · {formatDuration(song.durationMs)}</span>
                      </div>
                      <button className="btn btn-sm btn-primary" onClick={() => handleAddSong(song)}>+</button>
                    </div>))}
                </div>)}
              {searchMsg && <div className="form-message" style={{ marginTop: 6 }}>{searchMsg}</div>}
            </div>

            {/* Song list with multi-select and batch actions */}
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 8 }}>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <span className="queue-section-label" style={{ marginBottom: 0 }}>{t("library.songsLabelCap")}</span>
                {songs.length > 0 && (<label style={{ display: "flex", alignItems: "center", gap: 6, fontSize: 12, cursor: "pointer", color: uiText("var(--text-dim, #71717a)") }}>
                    <input type="checkbox" checked={songs.length > 0 && selectedUris.size === songs.length} onChange={toggleSelectAll} style={{ cursor: "pointer" }}/>
                    <span>{uiText("T\u00FCm\u00FCn\u00FC Se\u00E7 (")}{selectedUris.size}/{songs.length})</span>
                  </label>)}
              </div>

              {selectedUris.size > 0 && (<div style={{ display: "flex", gap: 6 }}>
                  <button type="button" className="btn btn-xs btn-primary" onClick={handleBulkEnqueue} style={{ fontSize: 11 }}>{uiText("+ Kuyru\u011Fa Ekle (")}{selectedUris.size})
                  </button>
                  <button type="button" className="btn btn-xs btn-danger-outline" onClick={handleBulkDelete} style={{ fontSize: 11 }}>{uiText("\uD83D\uDDD1 Se\u00E7ilenleri Sil (")}{selectedUris.size})
                  </button>
                </div>)}
            </div>

            {loadingSongs ? (<LoadingState label={t("common.loading")}/>) : songs.length === 0 ? (<div className="lib-empty">{t("library.emptyList")}</div>) : (<div className="autoqueue-list">
                {songs.map((song, i) => {
                    const isDragging = dragSongUri === song.spotifyUri;
                    const isDropTarget = dropSongIdx === i && dragSongUri !== song.spotifyUri;
                    const isSelected = selectedUris.has(song.spotifyUri);
                    const isSpotify = song.spotifyUri?.startsWith("spotify:track:");
                    return (<div key={song.id} className={`autoqueue-row${isDragging ? " queue-row-dragging" : ""}${isDropTarget ? " queue-row-drop-target" : ""}${isSelected ? " selected-row" : ""}`} draggable onClick={e => {
                            if (!(e.target as HTMLElement).closest("button,input,a"))
                                toggleSelectOne(song.spotifyUri);
                        }} tabIndex={0} onKeyDown={e => {
                            if ((e.key === " " || e.key === "Enter") && e.target === e.currentTarget) {
                                e.preventDefault();
                                toggleSelectOne(song.spotifyUri);
                            }
                        }} onDragStart={() => { setDragSongUri(song.spotifyUri); dragSongIdxRef.current = i; }} onDragOver={e => { e.preventDefault(); e.dataTransfer.dropEffect = "move"; setDropSongIdx(i); }} onDrop={e => {
                            e.preventDefault();
                            if (dragSongUri && dragSongIdxRef.current !== i)
                                handleReorderSong(dragSongUri, i);
                            setDragSongUri(null);
                            setDropSongIdx(null);
                        }} onDragEnd={() => { setDragSongUri(null); setDropSongIdx(null); }} style={isSelected ? { background: "rgba(16, 185, 129, 0.08)", borderColor: uiText("var(--accent, #10b981)") } : undefined}>
                      <input type="checkbox" checked={isSelected} onChange={() => toggleSelectOne(song.spotifyUri)} style={{ cursor: "pointer", marginRight: 2 }} onClick={e => e.stopPropagation()}/>
                      <span className="queue-drag-handle" title={t("library.dragReorder")}>⠿</span>
                      <span style={{ fontSize: 11, color: "var(--text-muted)", minWidth: 20, textAlign: "right" }}>
                        {i + 1}
                      </span>
                      <Artwork trackId={song.spotifyUri} coverUrl={song.coverUrl} className="autoqueue-cover"/>
                      <div className="autoqueue-info">
                        <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                          <span className="autoqueue-title">{song.title}</span>
                          <span style={{
                            fontSize: 10,
                            padding: uiText("1px 5px"),
                            borderRadius: 4,
                            fontWeight: 600,
                            background: isSpotify ? "rgba(30, 215, 96, 0.15)" : "rgba(239, 68, 68, 0.15)",
                            color: isSpotify ? "var(--green)" : "var(--red)",
                            border: isSpotify ? uiText("1px solid rgba(30, 215, 96, 0.3)") : uiText("1px solid rgba(239, 68, 68, 0.3)")
                        }}>
                            {isSpotify ? uiText("Spotify") : uiText("YouTube")}
                          </span>
                        </div>
                        <span className="autoqueue-artist">{song.artist} · {formatDuration(song.durationMs)}</span>
                      </div>
                      <button className="btn-icon-danger" title={t("library.removeFromList")} onClick={() => handleRemoveSong(song.spotifyUri)}>
                        ✕
                      </button>
                    </div>);
                })}
              </div>)}
          </>)}
      </div>
    </div>);
};
