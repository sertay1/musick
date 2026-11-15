import { useRef, useState } from "react";
import { Download, Upload } from "lucide-react";
import { api } from "../services/api";
import { uiText } from "../i18n/ui";

export function SettingsBackupPanel({ disabled, onBusy }: { disabled: boolean; onBusy: (busy: boolean) => void }) {
    const input = useRef<HTMLInputElement>(null);
    const [message, setMessage] = useState("");
    const exportSettings = async () => {
        onBusy(true); setMessage("");
        try {
            const document = await api.exportSettings();
            const blob = new Blob([JSON.stringify(document, null, 2)], { type: "application/json" });
            const url = URL.createObjectURL(blob), link = window.document.createElement("a");
            link.href = url; link.download = "Musick-Ayarlar.json"; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
            setMessage(uiText("Ayar yedeği hazırlandı. API bilgileri ve hesap oturumları dahil edilmedi."));
        } catch (error) { setMessage(error instanceof Error ? error.message : uiText("Ayarlar dışa aktarılamadı.")); }
        finally { onBusy(false); }
    };
    const importSettings = async (event: React.ChangeEvent<HTMLInputElement>) => {
        const file = event.target.files?.[0]; event.target.value = ""; if (!file) return;
        onBusy(true); setMessage(""); let applied = false;
        try {
            if (file.size > 4 * 1024 * 1024) throw new Error(uiText("Ayar dosyası en fazla 4 MB olabilir."));
            const document: unknown = JSON.parse(await file.text());
            if (!window.confirm(uiText("Dosyadaki ayarlar mevcut tercihlerin yerini alacak. API bilgileri ve hesap oturumları değişmeyecek. Devam edilsin mi?"))) return;
            const result = await api.importSettings(document);
            localStorage.setItem("musicbot-theme", result.appearance.theme);
            localStorage.setItem("musick-language", result.appearance.language);
            if (result.warnings.length) window.alert(result.warnings.join("\n"));
            applied = true; window.location.reload();
        } catch (error) { setMessage(error instanceof Error ? error.message : uiText("Ayarlar içe aktarılamadı.")); }
        finally { if (!applied) onBusy(false); }
    };
    return <section className="settings-card" style={{ padding: 20, border: "1px solid var(--border)", borderRadius: 8 }}>
        <h3 style={{ margin: "0 0 8px" }}>{uiText("Ayarları yedekle ve taşı")}</h3>
        <p style={{ color: "var(--text-muted)", fontSize: 12 }}>{uiText("Oynatma tercihleri, komutlar, roller, kanal, overlayler, otomatik mesajlar, yasaklı şarkılar, ses seçimi, dil ve tema taşınır. API anahtarları, hesap oturumları, çalma listeleri ve indirilen dosyalar dahil edilmez.")}</p>
        <div style={{ display: "flex", gap: 8 }}>
            <button type="button" className="btn btn-sm btn-ghost" disabled={disabled} onClick={exportSettings}><Download size={14}/>{uiText("Ayarları dışa aktar")}</button>
            <button type="button" className="btn btn-sm btn-ghost" disabled={disabled} onClick={() => input.current?.click()}><Upload size={14}/>{uiText("Ayarları içe aktar")}</button>
        </div>
        <input type="file" accept=".json,application/json" hidden ref={input} onChange={importSettings}/>
        {message && <p role="status" style={{ fontSize: 12, marginBottom: 0 }}>{message}</p>}
    </section>;
}
