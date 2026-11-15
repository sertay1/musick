import { Tabs, TabsList, TabsTrigger } from "./ui/tabs";
import { Timeline, TimelineItem, TimelineContent } from "./reui/timeline";
import { Input } from "./ui/input";
import { uiText } from "../i18n/ui";
import React, { useEffect, useState } from "react";
import { api } from "../services/api";
import { Trash2, Music2, Terminal, SkipForward, Heart, AlertCircle, Radio, ListMusic, Volume2 } from "lucide-react";
type Entry = {
    id: number;
    timestamp: string;
    source: string;
    kind: string;
    user: string;
    message: string;
    success: boolean;
};
const labels: Record<string, string> = { play: uiText("\u015Eark\u0131 iste\u011Fi"), skip: uiText("Atla"), pause: uiText("Duraklat"), resume: "Devam et", queue: uiText("S\u0131ra"), like: uiText("Be\u011Fen"), history: uiText("Ge\u00E7mi\u015F"), remove: uiText("Kald\u0131r"), volume: "Ses" };
export function LiveActivityPanel() {
    const [tab, setTab] = useState<"requests" | "log">("requests");
    const [entries, setEntries] = useState<Entry[]>([]);
    const [query, setQuery] = useState("");
    const [after, setAfter] = useState(0);
    const [error, setError] = useState("");
    useEffect(() => {
        let active = true, pending = false;
        const load = async () => {
            if (pending || document.hidden)
                return;
            pending = true;
            try {
                const data = await api.getActivity();
                if (active) {
                    setEntries(prev => prev[0]?.id === data[0]?.id ? prev : data);
                    setError("");
                }
            }
            catch {
                if (active)
                    setError(uiText("Etkinlik ak\u0131\u015F\u0131 al\u0131namad\u0131; tekrar ba\u011Flan\u0131l\u0131yor."));
            }
            finally {
                pending = false;
            }
        };
        void load();
        const timer = setInterval(load, 2000);
        return () => { active = false; clearInterval(timer); };
    }, []);
    const shown = entries.filter(e => e.id > after && (tab === "log" || (e.source === "kick" && e.kind === "play")) && `${e.user} ${e.kind} ${e.message}`.toLocaleLowerCase("tr").includes(query.toLocaleLowerCase("tr")));
    return <section className="live-card studio-activity">
    <div className="live-card-header" style={{ gap: 8, flexWrap: "wrap" }}>
      <Tabs value={tab} onValueChange={value => setTab(value as "requests" | "log")}><TabsList aria-label="Etkinlik görünümü" variant="line">
        <TabsTrigger value="requests" className={`btn btn-sm ${tab === "requests" ? "btn-primary" : "btn-ghost"}`} title={uiText("Kick Canlı İstek Akışı")}><Radio size={14}/>{uiText("İstek akışı")}</TabsTrigger>
        <TabsTrigger value="log" className={`btn btn-sm ${tab === "log" ? "btn-primary" : "btn-ghost"}`}><Terminal size={14}/>{uiText("Günlük")}</TabsTrigger>
      </TabsList></Tabs>
      <button className="btn btn-sm btn-ghost" title={uiText("G\u00F6r\u00FCn\u00FCm\u00FC temizle")} aria-label={uiText("G\u00F6r\u00FCn\u00FCm\u00FC temizle")} onClick={() => setAfter(entries[0]?.id ?? after)}><Trash2 size={16}/></button>
    </div>
    <div className="studio-activity-search"><Input className="input" aria-label="Etkinlik ara" placeholder={uiText("Komut, kullan\u0131c\u0131 veya \u015Fark\u0131 ara\u2026")} value={query} onChange={e => setQuery(e.target.value)}/></div>
    {error && <p role="status">{error}</p>}
    <div className="live-queue-list" role="log" style={{ overflowY: "auto", maxHeight: 540 }}>
      {shown.length === 0 && <p style={{ padding: 20, color: "var(--text-muted)" }}>{uiText("Yeni etkinlikler burada g\u00F6r\u00FCnecek.")}</p>}
      <Timeline className="activity-timeline" aria-label="Etkinlikler">{shown.map((e, index) => {
            const color = !e.success ? "var(--red)" : e.kind === "skip" ? "var(--amber)" : "var(--accent)";
            const Icon = !e.success ? AlertCircle : e.kind === "\u00C7al\u0131yor" ? Music2 : e.kind === "skip" ? SkipForward : e.kind === "like" ? Heart : e.kind === "play" ? ListMusic : e.kind === "Ba\u011Flant\u0131" ? Radio : e.kind === "Ses \u00E7\u0131k\u0131\u015F\u0131" ? Volume2 : Terminal;
            return <TimelineItem step={index + 1} className="studio-event" key={e.id} style={{ padding: uiText("10px 14px"), borderBottom: uiText("1px solid var(--border)"), borderLeft: `3px solid ${color}`, fontSize: 12 }}>
        <div style={{ display: "flex", gap: 8, color: "var(--text-muted)", marginBottom: 5 }}>
          <Icon size={15} color={color}/><time>{new Date(e.timestamp).toLocaleTimeString("tr-TR")}</time><span>{e.source === "kick" ? uiText("Kick") : e.source === "spotify" ? uiText("Spotify") : "Musick"}</span><strong style={{ color }}>{labels[e.kind] ?? e.kind}</strong>{e.user && <span>@{e.user}</span>}
        </div>
        <TimelineContent style={{ color: e.success ? "var(--text)" : uiText("var(--danger, #ef4444)") }}>{e.message}</TimelineContent>
      </TimelineItem>;
        })}</Timeline>
    </div>
  </section>;
}
