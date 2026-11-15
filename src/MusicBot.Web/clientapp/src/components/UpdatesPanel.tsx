import { localFetch as fetch } from "../services/localFetch";
import { uiText } from "../i18n/ui";
import { useState } from "react";
type Update = {
    currentVersion: string;
    version?: string;
    available: boolean;
    canInstall?: boolean;
    message: string;
    releaseUrl: string;
};
export function UpdatesPanel() {
    const [data, setData] = useState<Update | null>(null);
    const [busy, setBusy] = useState(false);
    const [message, setMessage] = useState("");
    const perform = async (install: boolean) => {
        setBusy(true);
        setMessage("");
        try {
            const response = await fetch("/api/updates" + (install ? "/install" : ""), { method: install ? "POST" : "GET" });
            const result = await response.json();
            if (!response.ok)
                throw new Error(result.error || uiText("G\u00FCncelleme i\u015Flemi ba\u015Far\u0131s\u0131z."));
            if (!install)
                setData(result);
            setMessage(result.message);
        }
        catch (e) {
            setMessage(e instanceof Error ? e.message : uiText("G\u00FCncelleme kontrol\u00FC yap\u0131lamad\u0131."));
        }
        finally {
            setBusy(false);
        }
    };
    return <section style={{ padding: 18, border: uiText("1px solid var(--border)"), borderRadius: 10 }}>
    <h3>{uiText("Uygulama g\u00FCncellemeleri")}</h3><p style={{ color: "var(--text-dim)", fontSize: 12 }}>{uiText("sertay1/musick deposunun son kararl\u0131 s\u00FCr\u00FCm\u00FC kontrol edilir. Kurulumdan \u00F6nce dosya \u00F6zeti do\u011Frulan\u0131r.")}</p>
    {data && <p>{uiText("Y\u00FCkl\u00FC s\u00FCr\u00FCm:")}{data.currentVersion}{data.version && <>{uiText("\u00B7 Son s\u00FCr\u00FCm:")}{data.version}</>}</p>}
    <button className="btn btn-secondary" disabled={busy} onClick={() => perform(false)}>{busy ? "Bekleniyor…" : uiText("G\u00FCncellemeleri kontrol et")}</button>
    {data?.available && data.canInstall && <button className="btn btn-primary" disabled={busy} onClick={() => perform(true)}>{uiText("G\u00FCncellemeyi indir ve kur")}</button>}
    {data?.available && !data.canInstall && <a href={data.releaseUrl} target="_blank" rel="noreferrer">{uiText("S\u00FCr\u00FCm sayfas\u0131n\u0131 a\u00E7")}</a>}
    {message && <p role="status">{message}</p>}
  </section>;
}
