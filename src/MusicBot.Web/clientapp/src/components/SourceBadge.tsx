import { uiText } from "../i18n/ui";
import { Music2 } from "lucide-react";
export function SourceBadge({ uri }: { uri: string }) {
    const spotify = /^spotify:|open\.spotify\.com\//i.test(uri);
    const youtube = /^youtube:|youtu(?:\.be|be\.com)/i.test(uri) || /youtube\.com\//i.test(uri);
    const source = spotify ? "spotify" : youtube ? "youtube" : "local";
    return <span className={`source-badge source-${source}`}>
        {spotify ? <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true"><circle cx="12" cy="12" r="11" fill="currentColor"/><path d="M5 9 Q12 6 19 10 M6 12 Q12 9.5 18 13 M7 15 Q12 13 17 16" fill="none" stroke="var(--surface)" strokeWidth="1.7" strokeLinecap="round"/></svg> : youtube ? <svg viewBox="0 0 24 24" width="13" height="13" aria-hidden="true"><rect x="1" y="4" width="22" height="16" rx="5" fill="currentColor"/><path d="M10 8 L16 12 L10 16 Z" fill="var(--surface)"/></svg> : <Music2 size={12} aria-hidden="true"/>}
        {spotify ? "Spotify" : youtube ? "YouTube" : uiText("Yerel")}
    </span>;
}

