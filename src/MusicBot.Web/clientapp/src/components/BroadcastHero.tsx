import { Headphones, Radio, ListMusic } from "lucide-react";
import { useReducedMotion } from "motion/react";
import { NumberTicker } from "./ui/number-ticker";
import { Badge } from "./ui/badge";
import { Artwork } from "./Artwork";
import { SourceBadge } from "./SourceBadge";
import type { Song } from "../types/models";
import { uiText } from "../i18n/ui";

interface Props { song: Song | null; playing: boolean; requester: string; count: number; minutes: number; connected: boolean }

/** Presentation only: transport and time interpolation remain in PlayerBar. */
export function BroadcastHero({ song, playing, requester, count, minutes, connected }: Props) {
  const reduced = useReducedMotion();
  return <section className={`broadcast-hero${song ? "" : " is-empty"}`} aria-label={uiText("Şimdi çalan")}>
    <div className="broadcast-art">{song ? <Artwork trackId={song.spotifyUri} coverUrl={song.coverUrl} style={{width:"100%",height:"100%",objectFit:"cover"}}/> : <Headphones size={54} strokeWidth={1}/>}</div>
    <div className="broadcast-copy">
      <div className="broadcast-kicker"><span className={`broadcast-indicator ${playing ? "on" : ""}`}/>{song ? (playing ? uiText("ŞİMDİ ÇALIYOR") : uiText("DURAKLATILDI")) : uiText("YAYINA HAZIR")}{song && <SourceBadge uri={song.spotifyUri}/>}</div>
      <h2>{song?.title || uiText("Sahne senin.")}</h2>
      <p>{song?.artist || uiText("Bir şarkı ekle. Gerisini Musick yönetsin.")}</p>
      <div className="broadcast-requester"><Radio size={14}/>{song ? <><span>{uiText("İsteyen")}</span><strong>{requester.startsWith("@") ? requester : `@${requester}`}</strong></> : <span>{uiText("Kick istekleri ve müziğin tek bir yerde.")}</span>}</div>
    </div>
    <div className="broadcast-summary">
      <Badge variant="outline" className={`session-badge ${connected ? "online" : "offline"}`}>{connected ? uiText("Canlı bağlantı") : uiText("Bağlanıyor…")}</Badge>
      <div className="broadcast-stat"><ListMusic size={18}/><strong>{reduced ? count : <NumberTicker value={count} className="queue-ticker"/>}</strong><span>{uiText("şarkı")}</span></div>
      <span className="broadcast-total">~{minutes} {uiText("dakikalık müzik")}</span>
    </div>
  </section>;
}
