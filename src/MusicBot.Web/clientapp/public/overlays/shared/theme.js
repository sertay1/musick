/* Shared appearance controller for all six overlay documents. */
(function () {
  const query = new URLSearchParams(location.search), preview = query.get('preview') === '1';
  const themes = ['glass','minimal','dark','light','midnight','warm','colorful','neon'];
  const designs = ['studio','vinyl','broadcast','cassette','compact','poster','ribbon','classic'];
  const modes = ['static','pulse','chroma','breathe','sweep','flicker'];
  let storedTheme;
  try { storedTheme = localStorage.getItem('musick-overlay-theme'); } catch {}
  const theme = query.get('theme') || storedTheme || 'glass';
  window.MusickOverlayPreferences = {
    keepLast:true, theme:themes.includes(theme)?theme:'glass',
    design:query.get('design')==='type'?'compact':designs.includes(query.get('design'))?query.get('design'):'studio',
    mode:query.get('neonMode') || 'static', neonColor:query.get('neonColor') || '',
  };
  const style = document.createElement('style'); document.head.appendChild(style);
  let revision = 0;
  const roots = 'html[data-theme][data-design][data-artwork] body :is(.now-playing,.queue-container,.npv-card,.qv-container,.player-shell,.pv-shell)';
  window.applyMusickOverlayPreferences = function (preferences) {
    revision++;
    const p = window.MusickOverlayPreferences = { ...window.MusickOverlayPreferences, ...preferences };
    const root = document.documentElement;
    const color = value => /^#[0-9a-f]{6}$/i.test(value || '') ? value : '';
    const num = (value,min,max,fallback) => value != null && Number.isFinite(Number(value)) ? Math.max(min,Math.min(max,Number(value))) : fallback;
    const legacyNeon = p.theme === 'colorful' || p.theme === 'neon';
    root.dataset.theme = legacyNeon ? 'glass' : themes.includes(p.theme) ? p.theme : 'glass';
    root.dataset.design = p.design === 'type' ? 'compact' : designs.includes(p.design) ? p.design : 'studio';
    root.dataset.artwork = p.showArtwork === false ? 'off' : 'on';
    root.dataset.albumBackground = p.backgroundArt ? 'on' : 'off';
    root.dataset.shadow = p.showShadow === false ? 'off' : 'on';
    root.dataset.neonEnabled = String(p.neonEnabled ?? legacyNeon);
    root.dataset.neonMode = modes.includes(p.mode) ? p.mode : 'static';
    root.dataset.motion = p.animate ? 'on' : 'off';
    for (const [key, variable] of [['accent','--ov-accent'],['text','--ov-text'],['background','--ov-bg']]) {
      const value = color(p[key]);
      if(value) root.style.setProperty(variable,value); else root.style.removeProperty(variable);
    }
    root.style.setProperty('--ov-background-alpha',num(p.backgroundOpacity ?? p.opacity,0,1,1));
    root.style.setProperty('--ov-shadow-strength',num(p.shadowStrength,0,1,.5));
    root.style.setProperty('--ov-glow-strength',num(p.glowStrength,0,1,.65));
    root.style.setProperty('--ov-neon-speed',num(p.neonSpeed,.25,3,1));
    root.style.setProperty('--ov-neon-tone',color(p.neonColor) || color(p.accent) || '#76efb0');
    root.style.setProperty('--ov-panel-bg','var(--ov-bg)');
    const limit = Math.round(num(p.queueLimit,1,30,8));
    style.textContent = roots+'{border-radius:'+num(p.radius,0,60,16)+'px!important;zoom:1;opacity:1;}';
    if(color(p.text)) style.textContent += 'html[data-theme][data-design][data-artwork] body :is(.np-title,.npv-title,.pl-title,.pv-title,.queue-item-title-line,.qv-title,.qp-title,.pv-item-title,.np-artist,.npv-artist,.pl-artist,.pv-artist,.qv-artist,.qp-artist,.pv-item-artist,.queue-item-title-line .artist){color:'+p.text+'!important;}';
    if(p.titleSize>0) style.textContent += 'html[data-theme][data-design][data-artwork] body :is(.np-title,.npv-title,.pl-title,.pv-title,.qv-title,.qp-title,.queue-item-title-line,.pv-item-title){font-size:'+num(p.titleSize,8,60,20)+'px!important;}';
    if(p.showArtwork===false) style.textContent += 'html[data-theme][data-design][data-artwork] body :is(.vinyl-wrap,.np-cover-container,.npv-cover-wrap,.pl-cover-wrap,.pv-cover-wrap,.queue-item-cover,.qp-cover,.qv-cover,.pv-item-cover){display:none!important;}';
    if(p.showProgress===false) style.textContent += 'html[data-theme][data-design][data-artwork] body :is(.np-progress,.npv-progress,.pl-progress,.pv-progress,.pv-bar-track,.pv-time-row,.pv-mini-bar,.qv-bar,.queue-item-mini-progress,.np-queue-bar){display:none!important;}';
    if(p.showRequester===false) style.textContent += 'html[data-theme][data-design][data-artwork] body :is(.np-requested,.npv-requested,.pl-requested,.pv-requested,.queue-item-requested,.queue-item-requested-by,.ov-requester){display:none!important;}';
    if(!p.animate) style.textContent += 'body *,body *::before,body *::after{animation-duration:.001ms!important;animation-iteration-count:1!important;transition-duration:.001ms!important;}html[data-theme][data-design][data-artwork] .vinyl-disc.playing{animation:none!important;}';
    style.textContent += 'html[data-theme][data-design][data-artwork] body :nth-child(n+'+(limit+1)+' of :is(.queue-item,.qp-row,.qv-item,.qv-row,.pv-item,.pv-row)){display:none!important;}';
  };
  window.applyMusickOverlayPreferences({});
  const initialRevision = revision;
  window.addEventListener('message',event=>{if(preview && event.origin===location.origin && event.source===parent && event.data?.type==='musick:overlay-preview') window.applyMusickOverlayPreferences(event.data.preferences || {});});
  if (!preview) fetch('/api/overlay-preferences').then(r=>r.ok?r.json():{}).then(p=>{if(revision===initialRevision)window.applyMusickOverlayPreferences(p);}).catch(()=>{});
})();
