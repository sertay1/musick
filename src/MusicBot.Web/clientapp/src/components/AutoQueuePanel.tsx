import { LoadingState } from "./PanelState";
import { uiText } from "../i18n/ui";
import { SourceBadge } from "./SourceBadge";
import React, { useCallback, useEffect, useMemo, useState, useRef } from "react";
import { Shuffle, Play, Plus, Search, Link, ListMusic, Trash2, Heart, RefreshCw } from "lucide-react";
import { api } from "../services/api";
import { NowPlayingState, Song, PlaylistLibrary } from "../types/models";
import { Artwork } from "./Artwork";
import { formatDuration } from "../utils";
import "./AutoQueuePanel.css";
type Track = Pick<Song, "spotifyUri" | "title" | "artist" | "coverUrl" | "durationMs">;
export function AutoQueuePanel({ nowPlaying }: {
    nowPlaying?: NowPlayingState | null;
}) {
    const [songs, setSongs] = useState<Track[]>([]);
    const [lists, setLists] = useState<PlaylistLibrary[]>([]);
    const [enabled, setEnabled] = useState<boolean | null>(null);
    const [loading, setLoading] = useState(true);
    const [busy, setBusy] = useState(false);
    const [message, setMessage] = useState("");
    const [error, setError] = useState("");
    const [mode, setMode] = useState<"search" | "url" | "library">("search");
    const [query, setQuery] = useState("");
    const [url, setUrl] = useState("");
    const [libraryId, setLibraryId] = useState("");
    const searchRequest = useRef<AbortController | null>(null);
    const [searching, setSearching] = useState(false);
    const closeSearch = () => { searchRequest.current?.abort(); setResults([]); setSearching(false); };
    useEffect(() => () => searchRequest.current?.abort(), []);
    const search = async () => {
        closeSearch();
        const controller = new AbortController();
        searchRequest.current = controller;
        setSearching(true);
        await api.searchProgressive(query.trim(), 10, setResults, controller.signal);
        if (!controller.signal.aborted)
            setSearching(false);
    };
    const [results, setResults] = useState<Song[]>([]);
    const [filter, setFilter] = useState("");
    const [limit, setLimit] = useState(100);
    const [confirmClear, setConfirmClear] = useState(false);
    const current = nowPlaying?.spotifyTrack ?? nowPlaying?.item?.song;
    const load = useCallback(async () => { const data = await api.getAutoQueue(); setSongs(data); }, []);
    useEffect(() => {
        let active = true, pending = false;
        const refresh = async () => {
            if (pending || document.hidden)
                return;
            pending = true;
            try {
                const [pool, settings] = await Promise.all([api.getAutoQueue(), api.getSettings()]);
                if (active) {
                    setSongs(pool);
                    setEnabled(settings.autoQueueEnabled);
                }
            }
            catch (e) {
                if (active)
                    setError(e instanceof Error ? e.message : uiText("Havuz y\u00FCklenemedi."));
            }
            finally {
                pending = false;
                if (active)
                    setLoading(false);
            }
        };
        void api.getPlaylists().then(data => {
            if (active)
                setLists(data);
        }).catch(() => { });
        void refresh();
        const timer = setInterval(refresh, 15000);
        return () => { active = false; clearInterval(timer); };
    }, []);
    const action = async (work: () => Promise<string | void>) => {
        setBusy(true);
        setError("");
        setMessage("");
        try {
            const result = await work();
            if (result)
                setMessage(result);
            await load();
        }
        catch (e) {
            setError(e instanceof Error ? e.message : uiText("\u0130\u015Flem tamamlanamad\u0131."));
        }
        finally {
            setBusy(false);
        }
    };
    const add = (track: Track) => action(async () => { await api.addAutoQueueSong(track); closeSearch(); setQuery(""); return track.title + " havuza eklendi."; });
    const toggle = () => action(async () => { const next = !enabled; await api.setAutoQueueEnabled(next); setEnabled(next); return next ? uiText("Otomatik \u00E7alma a\u00E7\u0131ld\u0131.") : uiText("Otomatik \u00E7alma kapat\u0131ld\u0131."); });
    const filtered = useMemo(() => songs.filter(s => (s.title + " " + s.artist).toLocaleLowerCase("tr").includes(filter.toLocaleLowerCase("tr"))), [songs, filter]);
    const spotifyCount = songs.filter(s => s.spotifyUri.startsWith("spotify:")).length;
    return <div className="aq-workspace">
    <section className="aq-hero">
      <div className="aq-hero-title"><Shuffle size={25}/><div><h2>{uiText("Otomatik Kuyruk")}</h2><p>{uiText("\u0130stekler bitince m\u00FCzik devam etsin.")}</p></div></div>
      <button className={"btn " + (enabled ? "btn-primary" : "btn-secondary")} disabled={busy || enabled === null} onClick={toggle} aria-pressed={enabled === true}>{enabled ? uiText("Otomatik \u00E7alma a\u00E7\u0131k") : uiText("Otomatik \u00E7alma kapal\u0131")}</button>
      <div className="aq-facts"><span><strong>{songs.length}</strong>{uiText("\u015Fark\u0131")}</span><span>{spotifyCount}{uiText("Spotify")}</span><span>{songs.length - spotifyCount}{uiText("YouTube / Yerel")}</span></div>
      <p className="aq-help">{uiText("\u0130zleyici istekleri \u00F6nceliklidir. Havuz kar\u0131\u015Ft\u0131r\u0131l\u0131r; t\u00FCm \u015Fark\u0131lar \u00E7al\u0131nmadan ayn\u0131 \u015Fark\u0131 tekrar se\u00E7ilmez. Havuzunuz otomatik kaydedilir.")}</p>
      <button className="btn btn-secondary" disabled={busy || !enabled || !songs.length} onClick={() => action(async () => { await api.startAutoQueue(); return uiText("Havuzdan oynatma ba\u015Flat\u0131ld\u0131."); })}><Play size={15}/>{uiText("Havuzdan ba\u015Flat")}</button>
    </section>
    {error && <div className="aq-notice aq-error" role="alert">{error}</div>}{message && <div className="aq-notice" role="status">{message}</div>}
    <section className="aq-card">
      <h3><Plus size={17}/>{uiText("Havuza \u015Fark\u0131 ekle")}</h3>
      {current && <div className="aq-current"><Artwork trackId={current.spotifyUri} coverUrl={current.coverUrl}/><div><small>{uiText("\u015EU AN \u00C7ALIYOR")}</small><strong>{current.title}</strong><span>{current.artist}</span></div><button className="btn btn-sm btn-secondary" disabled={busy || songs.some(s => s.spotifyUri === current.spotifyUri)} onClick={() => add(current)}><Heart size={14}/>{uiText("Havuza ekle")}</button></div>}
      <div className="aq-tabs" role="tablist">{([['search', Search, uiText("\u015Eark\u0131 ara")], ['url', Link, uiText("Liste ba\u011Flant\u0131s\u0131")], ['library', ListMusic, uiText("K\u00FCt\u00FCphaneden")]] as const).map(([id, Icon, label]) => <button key={id} role="tab" aria-selected={mode === id} onClick={() => { setMode(id); closeSearch(); }} className={mode === id ? "active" : ""}><Icon size={15}/>{label}</button>)}</div>
      {mode === "search" && <form className="aq-form" onSubmit={e => { e.preventDefault(); void search(); }}><input className="input" aria-label={uiText("\u015Eark\u0131 ara")} placeholder={uiText("\u015Eark\u0131 veya sanat\u00E7\u0131 ad\u0131")} value={query} onChange={e => { closeSearch(); setQuery(e.target.value); }} onKeyDown={e => {
                if (e.key === "Escape")
                    closeSearch();
            }}/><button className="btn btn-primary" disabled={searching || !query.trim()}>{searching ? uiText("Aran\u0131yor\u2026") : uiText("Ara")}</button></form>}
      {mode === "url" && <form className="aq-form" onSubmit={e => { e.preventDefault(); void action(async () => { const r = await api.importAutoQueue(url.trim()); setUrl(""); return r.added + uiText(" yeni \u015Fark\u0131 eklendi. Mevcut \u015Fark\u0131lar korunuyor."); }); }}><input className="input" aria-label={uiText("Liste ba\u011Flant\u0131s\u0131")} placeholder={uiText("Spotify veya YouTube Music liste ba\u011Flant\u0131s\u0131")} value={url} onChange={e => setUrl(e.target.value)}/><button className="btn btn-primary" disabled={busy || !url.trim()}>{uiText("Listeyi aktar")}</button></form>}
      {mode === "library" && <form className="aq-form" onSubmit={e => { e.preventDefault(); void action(async () => { const r = await api.importLibraryToAutoQueue(Number(libraryId)); return r.added + uiText(" yeni \u015Fark\u0131 k\u00FCt\u00FCphaneden eklendi."); }); }}><select className="input" aria-label={uiText("K\u00FCt\u00FCphane listesi")} value={libraryId} onChange={e => setLibraryId(e.target.value)}><option value="">{uiText("Bir \u00E7alma listesi se\u00E7")}</option>{lists.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}</select><button className="btn btn-primary" disabled={busy || !libraryId}>{uiText("Havuza aktar")}</button></form>}
      {results.length > 0 && <button className="btn btn-sm btn-ghost" onClick={closeSearch}>{uiText("Sonu\u00E7lar\u0131 kapat")}</button>}
      {results.map(s => <div className="aq-track" key={s.spotifyUri}><Artwork trackId={s.spotifyUri} coverUrl={s.coverUrl}/><div><strong>{s.title}</strong><span>{s.artist}</span></div><SourceBadge uri={s.spotifyUri}/><button className="btn btn-sm btn-secondary" disabled={busy || songs.some(t => t.spotifyUri === s.spotifyUri)} onClick={() => add(s)}><Plus size={15}/>{uiText("Ekle")}</button></div>)}
    </section>
    <section className="aq-card">
      <div className="aq-pool-heading"><h3><ListMusic size={17}/>{uiText("\u015Eark\u0131 havuzu")}<span>{songs.length}</span></h3><button className="btn btn-icon" aria-label="Havuzu yenile" disabled={busy} onClick={() => action(async () => { })}><RefreshCw size={16}/></button><button className="btn btn-icon" aria-label="Havuzu temizle" disabled={busy || !songs.length} onClick={() => setConfirmClear(v => !v)}><Trash2 size={16}/></button></div>
      {confirmClear && <div className="aq-notice">{uiText("Havuzdaki t\u00FCm \u015Fark\u0131lar kald\u0131r\u0131ls\u0131n m\u0131?")}<button className="btn btn-sm btn-danger" disabled={busy} onClick={() => action(async () => { await api.clearAutoQueue(); setConfirmClear(false); return uiText("Havuz temizlendi."); })}>{uiText("T\u00FCm\u00FCn\u00FC kald\u0131r")}</button><button className="btn btn-sm btn-ghost" onClick={() => setConfirmClear(false)}>{uiText("Vazge\u00E7")}</button></div>}
      <input className="input" aria-label="Havuzda ara" placeholder={uiText("Havuzda \u015Fark\u0131 veya sanat\u00E7\u0131 ara\u2026")} value={filter} onChange={e => { setFilter(e.target.value); setLimit(100); }}/>
      {loading ? <LoadingState label={uiText("Havuz yükleniyor…")}/> : !songs.length ? <div className="aq-empty"><Shuffle size={32}/><h3>{uiText("Havuzun hen\u00FCz bo\u015F")}</h3><p>{uiText("Bir liste aktar veya sevdi\u011Fin \u015Fark\u0131lar\u0131 ekle. \u0130zleyici istekleri bitti\u011Finde buradan \u00E7alaca\u011F\u0131z.")}</p></div> : !filtered.length ? <p className="aq-help">{uiText("Bu aramada \u015Fark\u0131 bulunamad\u0131.")}</p> : filtered.slice(0, limit).map(s => <div className="aq-track" key={s.spotifyUri}><Artwork trackId={s.spotifyUri} coverUrl={s.coverUrl}/><div><strong>{s.title}</strong><span>{s.artist} · {formatDuration(s.durationMs)}</span></div><small className={s.spotifyUri.startsWith("spotify:") ? "aq-spotify" : "aq-youtube"}>{s.spotifyUri.startsWith("spotify:") ? uiText("Spotify") : uiText("YouTube")}</small><button className="btn btn-icon" title={uiText("\u0130stek s\u0131ras\u0131na ekle")} disabled={busy} onClick={() => action(async () => { await api.enqueueTrack(s, "Admin"); return uiText("\u015Eark\u0131 istek s\u0131ras\u0131na eklendi."); })}><Plus size={16}/></button><button className="btn btn-icon" title={uiText("Havuzdan kald\u0131r")} disabled={busy} onClick={() => action(async () => { await api.removeAutoQueueSong(s.spotifyUri); })}><Trash2 size={15}/></button></div>)}
      {filtered.length > limit && <button className="btn btn-secondary" onClick={() => setLimit(n => n + 100)}>{uiText("Di\u011Fer \u015Fark\u0131lar\u0131 g\u00F6ster (")}{filtered.length - limit})</button>}
    </section>
  </div>;
}
