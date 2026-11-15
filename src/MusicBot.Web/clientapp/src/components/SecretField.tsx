import { useState } from "react";
import { api } from "../services/api";
import { uiText } from "../i18n/ui";

export function SecretField({ platform, value, configured, onChange }: {
    platform: "kick" | "spotify"; value: string; configured: boolean; onChange: (value: string) => void;
}) {
    const [visible, setVisible] = useState(false);
    const [revealed, setRevealed] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const toggle = async () => {
        setError("");
        if (visible) { setVisible(false); setRevealed(null); return; }
        if (value || !configured) { setVisible(true); return; }
        setBusy(true);
        try { const result = await api.revealCredential(platform); setRevealed(result.secret); setVisible(true); }
        catch (e) { setError(e instanceof Error ? e.message : uiText("Anahtar gösterilemedi.")); }
        finally { setBusy(false); }
    };
    return <>
        <div style={{ display: "flex", gap: 8 }}>
            <input type={visible ? "text" : "password"} className="input" autoComplete="new-password"
                aria-label={platform === "kick" ? "Kick Client Secret" : "Spotify Client Secret"}
                placeholder={configured ? "••••••••••••••••" : "Client Secret"}
                value={value || (visible ? revealed ?? "" : "")} disabled={busy}
                onChange={e => { setRevealed(null); onChange(e.target.value); }}
                style={{ fontSize: 12, flex: 1 }}/>
            <button type="button" className="btn btn-sm btn-ghost" disabled={busy}
                onClick={() => void toggle()} aria-pressed={visible}>
                {busy ? uiText("Yükleniyor...") : visible ? uiText("Gizle") : uiText("Göster")}
            </button>
        </div>
        {error && <span role="alert" style={{ fontSize: 11, color: "var(--red)" }}>{error}</span>}
    </>;
}
