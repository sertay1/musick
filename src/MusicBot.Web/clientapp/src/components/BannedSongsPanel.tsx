import { useState } from "react";
import { useBannedSongs } from "../hooks/useBannedSongs";
import { uiText } from "../i18n/ui";
export function BannedSongsPanel() {
  const { entries, toggleBan } = useBannedSongs(); const [error, setError] = useState("");
  return <section className="studio-panel" style={{ padding: 16, display: "grid", gap: 12 }}>
    <strong>{uiText("Kara liste")} · {entries.length}</strong>
    <p style={{ margin: 0, fontSize: 12, color: "var(--text-muted)" }}>{uiText("Engellenen kayıtlar isteklerde ve Musick sırası ilerlerken atlanır. Aynı düğmeyle engeli kaldırabilirsin.")}</p>
    {!entries.length && <span>{uiText("Engellenen şarkı yok.")}</span>}
    {entries.map(entry => <div key={entry.uri} style={{ display: "flex", alignItems: "center", gap: 12, borderTop: "1px solid var(--border)", paddingTop: 10 }}>
      <div style={{ flex: 1, minWidth: 0 }}><strong>{entry.title || entry.uri}</strong><div style={{ fontSize: 12, color: "var(--text-muted)" }}>{entry.artist} · {entry.uri.startsWith("spotify:") ? "Spotify" : "YouTube"}</div></div>
      <button type="button" className="btn btn-sm" style={{ color: "#ef4444" }} onClick={() => { setError(""); void toggleBan({ spotifyUri: entry.uri, title: entry.title, artist: entry.artist }).catch(e => setError(String(e.message || e))); }}>{uiText("Engeli kaldır")}</button>
    </div>)}
    {error && <p role="alert">{error}</p>}
  </section>;
}
