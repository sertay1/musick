import { uiText as t } from "../i18n/ui";
import React, { useEffect, useRef, useState } from "react";
import { api } from "../services/api";
import { useAutoSave } from "../hooks/useAutoSave";
import type { NowPlayingState, QueueItem } from "../types/models";
import { Copy, ExternalLink, Check, RotateCcw } from "lucide-react";
import "./OverlayLinks.css";

const defaults = { design: "compact", theme: "glass", mode: "static", accent: "", text: "", background: "", opacity: 1, scale: 1, backgroundOpacity: 1, showShadow: true, shadowStrength: .5, neonEnabled: false, neonColor: "#76efb0", glowStrength: .65, neonSpeed: 1, radius: 16, titleSize: 0, showArtwork: true, showProgress: true, showRequester: true, backgroundArt: false, animate: false, keepLast: true, queueLimit: 8 };
const layouts = [
  { id: "now-playing/index.html", name: "Şimdi çalan · Yatay", width: 604, height: 254 },
  { id: "now-playing/vertical.html", name: "Şimdi çalan · Dikey", width: 404, height: 564 },
  { id: "queue/index.html", name: "Şarkı sırası · Yatay", width: 504, height: 614 },
  { id: "queue/vertical.html", name: "Şarkı sırası · Dikey", width: 404, height: 614 },
  { id: "player/index.html", name: "Çalan + sıra · Yatay", width: 964, height: 464 },
  { id: "player/vertical.html", name: "Çalan + sıra · Dikey", width: 444, height: 744 },
];
const designs = [
  { id: "compact", name: "Kompakt", detail: "Kapak, şarkı ve ince ilerleme çizgisi.", art: "square" },
  { id: "poster", name: "Albüm kartı", detail: "Geniş kapak ve ortalanmış şarkı bilgisi.", art: "poster" },
  { id: "ribbon", name: "İnce bant", detail: "Yayında az yer kaplayan tek satırlık görünüm.", art: "ribbon" },
  { id: "classic", name: "Klasik", detail: "Önceki Musick görünümü ve plak kapağı.", art: "disc" },
  { id: "studio", name: "Stüdyo", detail: "Büyük kapak ve çizgili editoryal düzen.", art: "editorial" },
  { id: "vinyl", name: "Plak", detail: "Plak olukları ve merkezde albüm etiketi.", art: "record" },
  { id: "broadcast", name: "Yayın bandı", detail: "Renk bloğu ve güçlü yayın tipografisi.", art: "broadcast" },
  { id: "cassette", name: "Kaset", detail: "Bant makaraları, basılı etiket ve sayaç.", art: "tape" },
];

const emptyQueue: QueueItem[] = [];
const paletteColors: Record<string, { accent: string; text: string; background: string }> = {
  glass: { accent:"#9cdaee",text:"#f1f5f7",background:"#161e26" }, minimal:{accent:"#ededed",text:"#f6f6f6",background:"#141414"},
  dark:{accent:"#dfc39a",text:"#f3f1eb",background:"#181b20"},light:{accent:"#526c75",text:"#272c31",background:"#f5f2eb"},
  midnight:{accent:"#82d2bc",text:"#e9f4f2",background:"#101c28"},warm:{accent:"#e8ae7d",text:"#f6eddf",background:"#28201c"},
};
export const OverlayLinks: React.FC<{ overlayToken: string; nowPlaying?: NowPlayingState | null; upcoming?: QueueItem[] }> = ({ overlayToken, nowPlaying = null, upcoming = emptyQueue }) => {
  const [preferences, setPreferences] = useState(defaults);
  const [loaded, setLoaded] = useState(false);
  const [loadError, setLoadError] = useState(false);
  const [reload, setReload] = useState(0);
  const [layoutId, setLayoutId] = useState(layouts[0].id);
  const [tab, setTab] = useState("Görünüm");
  const [copied, setCopied] = useState(false);
  const [copyError, setCopyError] = useState(false);
  const [previewBackground, setPreviewBackground] = useState("dark");
  const [previewWidth, setPreviewWidth] = useState(600);
  const [previewHeight, setPreviewHeight] = useState(360);
  const frame = useRef<HTMLIFrameElement>(null);
  const stage = useRef<HTMLDivElement>(null);
  const copyTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    let active = true;
    setLoadError(false);
    api.getOverlayPreferences().then(p => {
      if (!active) return;
      setPreferences({ ...defaults, ...p, scale: 1, opacity: 1,
        design: p.design === "type" ? "compact" : p.design ?? defaults.design,
        backgroundOpacity: p.backgroundOpacity ?? p.opacity ?? 1,
        theme: p.theme === "colorful" ? "glass" : p.theme ?? defaults.theme,
        neonEnabled: p.neonEnabled ?? p.theme === "colorful",
        neonColor: p.neonColor || (p.theme === "colorful" && p.accent) || defaults.neonColor,
      } as typeof defaults);
      setLoaded(true);
    }).catch(() => { if (active) setLoadError(true); });
    return () => { active = false; };
  }, [reload]);
  useEffect(() => {
    const element = stage.current;
    if (!element) return;
    const observer = new ResizeObserver(entries => {
      setPreviewWidth(entries[0].contentRect.width);
      setPreviewHeight(entries[0].contentRect.height);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  useEffect(() => () => { if (copyTimer.current) clearTimeout(copyTimer.current); }, []);
  const save = useAutoSave(preferences, api.setOverlayPreferences, loaded);
  const set = <K extends keyof typeof defaults>(key: K, value: typeof defaults[K]) => setPreferences(p => ({ ...p, [key]: value }));
  const layout = layouts.find(item => item.id === layoutId)!;
  const params = new URLSearchParams({ token: overlayToken, theme: preferences.theme, design: preferences.design });
  const url = `${window.location.origin}/overlays/${layout.id}?${params}`;
  const previewUrl = `/overlays/${layout.id}?preview=1&design=${preferences.design}`;
  const updatePreview = () => frame.current?.contentWindow?.postMessage({ type: "musick:overlay-preview", preferences }, window.location.origin);
  useEffect(updatePreview, [preferences, layoutId]);
  const hasLiveState = !!(nowPlaying?.spotifyTrack || nowPlaying?.item?.song || upcoming.length);
  const updatePreviewState = () => frame.current?.contentWindow?.postMessage({
    type: "musick:overlay-preview-state",
    state: hasLiveState ? { nowPlaying: nowPlaying ?? { item: null, spotifyTrack: null, progressMs: 0, isPlaying: false }, upcoming } : null,
  }, window.location.origin);
  useEffect(updatePreviewState, [nowPlaying, upcoming, layoutId, preferences.design]);
  const scale = Math.min(1, Math.max(0.1, previewWidth / layout.width), Math.max(0.1, previewHeight / layout.height));
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true); setCopyError(false);
      if (copyTimer.current) clearTimeout(copyTimer.current);
      copyTimer.current = setTimeout(() => setCopied(false), 2500);
    } catch { setCopyError(true); }
  };
  return <div className="overlay-editor">
    <header className="oe-heading"><div><h2>{t("Yayın katmanları")}</h2><p>{t("Görünümünü seç, önizle ve OBS’ye ekle.")}</p></div>
      <span role="status">{loadError ? t("Ayarlar yüklenemedi.") : !loaded ? t("Yükleniyor…") : save.status === "error" ? t("Ayarlar kaydedilemedi.") : save.status === "saving" ? t("Kaydediliyor…") : t("Değişiklikler otomatik kaydedilir.")}</span>
      {loadError && <button className="btn btn-secondary" onClick={() => setReload(n => n + 1)}>{t("Tekrar dene")}</button>}
      {save.status === "error" && <button className="btn btn-secondary" onClick={save.retry}>{t("Tekrar dene")}</button>}
    </header>
    <div className="oe-workspace">
      <fieldset className="oe-options" disabled={!loaded}>
        <section className="studio-panel oe-section"><h3><span>01</span>{t("İçerik ve yerleşim")}</h3>
          <label className="oe-label">{t("Ne gösterilsin?")}<select value={layoutId} onChange={e => { setLayoutId(e.target.value); setCopied(false); }}>{layouts.map(item => <option key={item.id} value={item.id}>{t(item.name)}</option>)}</select></label>
        </section>
        <section className="studio-panel oe-section"><h3><span>02</span>{t("Tasarımını seç")}</h3>
          <div className="oe-designs">{designs.map(design => <button key={design.id} className="oe-design" aria-pressed={preferences.design === design.id} onClick={() => set("design", design.id)}>
            <span className={`oe-thumbnail oe-thumbnail-${design.art}`} aria-hidden="true"><img src="/overlays/shared/demo-art.svg" alt=""/><span><i/><i/><i/></span></span>
            <strong>{t(design.name)}{preferences.design === design.id && <Check size={14}/>}</strong><small>{t(design.detail)}</small>
          </button>)}</div>
        </section>
        <section className="studio-panel oe-section"><h3><span>03</span>{t("Kişiselleştir")}</h3>
          <div className="oe-tabs" role="tablist" aria-label={t("Kişiselleştir")}>{["Görünüm", "İçerik", "Efektler"].map(name => <button role="tab" id={`oe-tab-${name}`} aria-selected={tab === name} aria-controls="oe-settings" key={name} onClick={() => setTab(name)}>{t(name)}</button>)}</div>
          <div id="oe-settings" role="tabpanel" aria-labelledby={`oe-tab-${tab}`} className="oe-settings">
            {tab === "Görünüm" && <>
              <label className="oe-label">{t("Renk paleti")}<select value={preferences.theme} onChange={e => set("theme", e.target.value)}>{[["glass", "Buzlu cam"], ["minimal", "Monokrom"], ["dark", "Kömür"], ["light", "Porselen"], ["midnight", "Gece mavisi"], ["warm", "Kehribar"]].map(([id, name]) => <option key={id} value={id}>{t(name)}</option>)}</select></label>
              <div className="oe-colors">{([['accent', 'Vurgu rengi', '#53fc18'], ['text', 'Yazı rengi', '#f8fafc'], ['background', 'Arka plan rengi', '#14141e']] as const).map(([key, label, fallback]) => <label key={key}>{t(label)}<span><input type="color" aria-label={t(label)} value={preferences[key] || paletteColors[preferences.theme]?.[key] || fallback} onChange={e => set(key, e.target.value)}/><button title={t("Tema rengini kullan")} aria-label={`${t(label)}: ${t("Tema rengini kullan")}`} onClick={() => set(key, "")}><RotateCcw size={13}/></button></span></label>)}</div>
              {([['backgroundOpacity', 'Arka plan opaklığı', 0, 1, .05], ['radius', 'Köşe yuvarlaklığı', 0, 60, 1], ['titleSize', 'Başlık boyutu (0: tema)', 0, 60, 1]] as const).map(([key, label, min, max, step]) => <label className="oe-slider" key={key}><span>{t(label)}<output>{preferences[key]}</output></span><input aria-label={t(label)} type="range" min={min} max={max} step={step} value={preferences[key]} onChange={e => set(key, Number(e.target.value))}/></label>)}
              <label className="oe-toggle"><input type="checkbox" checked={preferences.backgroundArt} onChange={e => set("backgroundArt", e.target.checked)}/>{t("Albümden renkli arka plan")}</label>
            </>}
            {tab === "İçerik" && <>
              {([['showArtwork', 'Kapak görseli'], ['showProgress', 'İlerleme çubuğu'], ['showRequester', 'İsteyen kullanıcı'], ['keepLast', 'Geçiş boşluğunda son şarkıyı koru']] as const).map(([key, label]) => <label className="oe-toggle" key={key}><input type="checkbox" checked={preferences[key]} onChange={e => set(key, e.target.checked)}/>{t(label)}</label>)}
              <label className="oe-slider"><span>{t("Gösterilecek sıra")}<output>{preferences.queueLimit}</output></span><input aria-label={t("Gösterilecek sıra")} type="range" min={1} max={30} value={preferences.queueLimit} onChange={e => set("queueLimit", Number(e.target.value))}/></label>
            </>}
            {tab === "Efektler" && <>
              <label className="oe-toggle"><input type="checkbox" checked={preferences.showShadow} onChange={e => set("showShadow", e.target.checked)}/>{t("Kutu gölgesi")}</label>
              {preferences.showShadow && <label className="oe-slider"><span>{t("Gölge yoğunluğu")}<output>{preferences.shadowStrength}</output></span><input aria-label={t("Gölge yoğunluğu")} type="range" min={0} max={1} step={.05} value={preferences.shadowStrength} onChange={e => set("shadowStrength", Number(e.target.value))}/></label>}
              <label className="oe-toggle"><input type="checkbox" checked={preferences.neonEnabled} onChange={e => set("neonEnabled", e.target.checked)}/>{t("Neon modu")}</label>
              {preferences.neonEnabled && <>
                <label className="oe-label">{t("Neon rengi")}<input aria-label={t("Neon rengi")} type="color" value={preferences.neonColor || "#76efb0"} onChange={e => set("neonColor", e.target.value)}/></label>
                <label className="oe-label">{t("Neon hareketi")}<select value={preferences.mode} onChange={e => set("mode", e.target.value)}>{[["static","Sabit"],["pulse","Kalp atışı"],["breathe","Nefes"],["chroma","Renk döngüsü"],["sweep","Çevre turu"],["flicker","Titreşim"]].map(([id,name])=><option key={id} value={id}>{t(name)}</option>)}</select></label>
                {([['glowStrength','Neon parlama yoğunluğu',0,1,.05],['neonSpeed','Hareket hızı',.25,3,.25]] as const).map(([key,label,min,max,step])=><label key={key} className="oe-slider"><span>{t(label)}<output>{preferences[key]}</output></span><input aria-label={t(label)} type="range" min={min} max={max} step={step} value={preferences[key]} onChange={e=>set(key,Number(e.target.value))}/></label>)}
              </>}
              <label className="oe-toggle"><input type="checkbox" checked={preferences.animate} onChange={e => set("animate", e.target.checked)}/>{t("Kapak ve geçiş animasyonları")}</label>
              <p className="oe-hint">{t("Neon yalnızca kenarlığı ve parlamayı etkiler. Albüm kapağının renkleri korunur.")}</p>
            </>}
          </div>
          <button className="oe-reset" onClick={() => setPreferences({ ...defaults, design: preferences.design })}><RotateCcw size={13}/>{t("Görünümü sıfırla")}</button>
        </section>
      </fieldset>
      <div className="oe-output"><section className="studio-panel oe-preview-panel"><header><strong>{t("Önizleme")}</strong><span>{t(hasLiveState ? "Canlı şarkı ve sıra" : "Örnek şarkılar")}</span><div className="oe-backdrops" aria-label={t("Önizleme zemini")}>{["dark", "light", "checker"].map(bg => <button key={bg} className={`oe-backdrop-${bg}`} aria-label={t(bg === "dark" ? "Koyu zemin" : bg === "light" ? "Açık zemin" : "Şeffaflık zemini")} aria-pressed={previewBackground === bg} onClick={() => setPreviewBackground(bg)}/>)}</div></header>
        <div ref={stage} className={`oe-stage oe-stage-${previewBackground}`}><div style={{ width: layout.width * scale, height: layout.height * scale }}><iframe key={layout.id + preferences.design} ref={frame} src={previewUrl} onLoad={() => { updatePreview(); updatePreviewState(); }} title={t("Overlay önizlemesi")} style={{ width: layout.width, height: layout.height, transform: `scale(${scale})` }} tabIndex={-1}/></div></div>
        <footer className="oe-obs">
          <div className="oe-obs-actions">
            <div>
              <h3>{t("OBS’ye ekle")}</h3>
              <p>{t(layout.name)} · <strong>{layout.width} × {layout.height} px</strong></p>
            </div>
            <button className="btn btn-primary" onClick={copy} disabled={!loaded}>{copied ? <Check size={16}/> : <Copy size={16}/>} {t(copied ? "Bağlantı kopyalandı" : "OBS bağlantısını kopyala")}</button>
          </div>
          <p>{t("OBS’de Tarayıcı kaynağı ekle. Bağlantıyı yapıştır ve gösterilen boyutları gir.")}</p>
          {copyError && <p role="alert">{t("Kopyalanamadı. Bağlantıyı aşağıdan seçip kopyalayabilirsin.")}</p>}
          <details open={copyError || undefined}>
            <summary>{t("Bağlantıyı göster")}</summary>
            <input aria-label={t("OBS bağlantısı")} readOnly value={url} onFocus={e => e.target.select()}/>
            <a href={url} target="_blank" rel="noopener noreferrer"><ExternalLink size={13}/>{t("Canlı katmanı aç")}</a>
            <p className="oe-hint">{t("Ayarlar açık OBS katmanlarına da uygulanır. Yeni bir kaynak eklemen gerekmez.")}</p>
            <p className="oe-hint">{t("Gölge ve neon için dört kenarda boşluk bırakılır.")}</p>
          </details>
        </footer>
      </section></div>
    </div>
  </div>;
};
