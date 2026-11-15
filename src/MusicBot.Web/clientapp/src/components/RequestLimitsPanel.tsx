import { uiText } from "../i18n/ui";
import React, { useEffect, useState } from "react";
import { api } from "../services/api";
import type { QueueSettings } from "../hooks/useSignalR";
import { useAutoSave } from "../hooks/useAutoSave";
export function RequestLimitsPanel() {
    const [settings, setSettings] = useState<QueueSettings | null>(null);
    const [kick, setKick] = useState<{
        channel: string;
        auto: boolean;
        roles: string[];
        users: string;
    } | null>(null);
    const [error, setError] = useState("");
    useEffect(() => {
        let active = true;
        Promise.all([api.getSettings(), api.getPlatforms()]).then(([s, ps]) => {
            if (!active)
                return;
            setSettings(s);
            const p = ps.find(p => p.platform === "kick");
            const cfg = p?.config as {
                channel?: string;
                commandRoles?: string[];
                allowedUsers?: string[];
            } | undefined;
            setKick({ channel: cfg?.channel || "", auto: p?.autoConnect ?? true, roles: cfg?.commandRoles || ["all"], users: (cfg?.allowedUsers || []).join(", ") });
        }).catch(e => setError(String(e)));
        return () => { active = false; };
    }, []);
    const queueSave = useAutoSave(settings, async (value) => {
        if (value)
            await api.updateSettings(value);
    }, settings !== null);
    const accessSave = useAutoSave(kick, async (value) => {
        if (value?.channel)
            await api.saveKick(value.channel, value.auto, value.roles, value.users.split(/[,\n]/).map(u => u.trim().replace(/^@/, "")).filter(Boolean));
    }, kick !== null);
    if (error)
        return <p role="alert">{uiText("\u0130stek ayarlar\u0131 y\u00FCklenemedi:")}{error}</p>;
    if (!settings || !kick)
        return null;
    const saveFailed = queueSave.status === "error" || accessSave.status === "error";
    return <section style={{ padding: 16, marginBottom: 20, border: uiText("1px solid var(--border)"), borderRadius: 8 }}>
    <h3 style={{ marginTop: 0 }}>{uiText("Kuyruk ve istek kurallar\u0131")}</h3>
    <p style={{ fontSize: 12, color: "var(--text-muted)" }}>{uiText("Rol izinleri ve rol ba\u015F\u0131na limitler a\u015Fa\u011F\u0131daki Ekle komutundan y\u00F6netilir. Bu b\u00F6l\u00FCm t\u00FCm kuyruk i\u00E7in ge\u00E7erlidir. De\u011Fi\u015Fiklikler otomatik kaydedilir.")}</p>
    <div style={{ display: "flex", gap: 24, flexWrap: "wrap" }}>
      <label><input type="checkbox" checked={settings.maxQueueSizeEnabled ?? true} onChange={e => setSettings({ ...settings, maxQueueSizeEnabled: e.target.checked })}/>{uiText("Toplam kuyruk s\u0131n\u0131r\u0131")}<input className="input" style={{ width: 95, marginLeft: 8 }} type="number" min={1} disabled={!settings.maxQueueSizeEnabled} value={settings.maxQueueSize} onChange={e => setSettings({ ...settings, maxQueueSize: Math.max(1, Number(e.target.value)) })}/>
      </label>
      <label><input type="checkbox" checked={settings.maxSongsPerUserEnabled ?? true} onChange={e => setSettings({ ...settings, maxSongsPerUserEnabled: e.target.checked })}/>{uiText("Rol ba\u015F\u0131na \u015Fark\u0131 limitlerini uygula")}</label>
    </div>
    <details style={{ marginTop: 16 }}><summary>{uiText("\u0130ste\u011Fe ba\u011Fl\u0131 kullan\u0131c\u0131 k\u0131s\u0131tlamas\u0131")}</summary>
      <p style={{ fontSize: 12 }}>{uiText("Yaln\u0131zca belirli ki\u015Filer botu kullanacaksa etkinle\u015Ftir. Normal yay\u0131n kullan\u0131m\u0131nda kapal\u0131 b\u0131rak; komutlar\u0131n rol izinleri yeterlidir.")}</p>
      <label><input type="checkbox" checked={kick.roles.includes("list")} onChange={e => setKick({ ...kick, roles: e.target.checked ? [...kick.roles.filter(r => r !== "list"), "list"] : kick.roles.filter(r => r !== "list") })}/>{uiText("Yaln\u0131zca listedeki kullan\u0131c\u0131lar (yay\u0131nc\u0131 ve bot hari\u00E7)")}</label>
      {kick.roles.includes("list") && <input aria-label={uiText("\u0130zinli kullan\u0131c\u0131 adlar\u0131")} className="input" value={kick.users} placeholder={uiText("kullanici1, kullanici2")} onChange={e => setKick({ ...kick, users: e.target.value })}/>}
    </details>
    <p role="status" style={{ fontSize: 12, color: saveFailed ? uiText("var(--danger, #ef4444)") : "var(--text-muted)" }}>
      {saveFailed ? uiText("Kaydedilemedi. De\u011Fi\u015Fiklikler hen\u00FCz uygulanmad\u0131.") : queueSave.status === "saving" || accessSave.status === "saving" ? uiText("Kaydediliyor\u2026") : uiText("Ayarlar g\u00FCncel.")}
      {saveFailed && <button className="btn btn-sm" onClick={() => { queueSave.retry(); accessSave.retry(); }}>{uiText("Tekrar dene")}</button>}
    </p>
  </section>;
}
