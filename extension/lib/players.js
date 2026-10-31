// Oynatıcı yapılandırmasından kalite kaynaklarını OKUR (sayfanın JS'i değiştirilmez; yalnızca okuma).
// `readPlayers` background tarafından düğme tıklanınca `chrome.scripting.executeScript({ world: "MAIN", func })` ile
// o çerçevede çalıştırılır; bu yüzden TAMAMEN kendi kendine yeterli olmalı (dış değişken/içe aktarma kullanamaz).
// Çıktı yalnızca düz veri: [{ url, label, height, via }]. Test için win/doc/expose parametreleri verilebilir.
export function readPlayers(win = window, doc = document, expose = null) {
  const out = [];
  const seen = new Set();
  const MEDIA = /\.(m3u8|mp4|webm|mkv|m4v|mov)(?:$|[?#])/i;
  const MAX = 40;

  function heightOfLabel(label) {
    const s = String(label == null ? "" : label).toLowerCase();
    let m = /(\d{3,4})\s*p/.exec(s);
    if (m) return +m[1];
    m = /(\d{3,4})\s*[x×]\s*(\d{3,4})/.exec(s);
    if (m) return +m[2];
    if (/\b4k\b/.test(s)) return 2160;
    if (/\b2k\b/.test(s)) return 1440;
    m = /^\s*(\d{3,4})\s*$/.exec(s);
    if (m && [144, 240, 360, 480, 540, 576, 720, 1080, 1440, 2160, 4320].includes(+m[1])) return +m[1];
    return 0;
  }

  function abs(u) {
    try {
      const s = String(u).trim().replace(/\\\//g, "/").replace(/\\u0026/g, "&").replace(/&amp;/g, "&");
      if (!s || /^(blob|data|javascript):/i.test(s)) return "";
      const base = (doc && doc.baseURI) || (win && win.location && win.location.href) || undefined;
      const href = new URL(s, base).href;
      return /^https?:/i.test(href) ? href : "";
    } catch { return ""; }
  }

  function add(url, label, height, via, force) {
    if (out.length >= MAX || !url) return;
    const href = abs(url);
    if (!href || (!force && !MEDIA.test(href))) return;
    if (seen.has(href)) return;
    seen.add(href);
    const lab = label == null ? "" : String(label).slice(0, 40);
    out.push({ url: href.slice(0, 2000), label: lab, height: Math.max(0, Math.round(+height || heightOfLabel(lab))), via });
  }

  // Playerjs: file:"[1080p]url,[720p]url" (url sonrası " or " yedekleri atlanır). Eklenen kaynak sayısını döndürür.
  function parseList(str, via) {
    const s = String(str || "").replace(/\\\//g, "/");
    const re = /\[([^\]]{1,24})\]\s*((?:https?:)?\/\/[^\s,\[\]"'\\]+)/g;
    let m, n = 0;
    while ((m = re.exec(s)) !== null) { add(m[2], m[1], 0, via, true); n++; }
    return n;
  }

  function fromObj(o, via) {
    if (!o || typeof o !== "object") return;
    const url = o.file || o.src || o.url || o.source;
    const label = o.label || o.res || o.size || o.quality || o.title || o.name || "";
    const h = +o.height || +o.size || 0;
    if (typeof url === "string") add(url, label, h > 100 ? h : 0, via, /m3u8|mpegurl|mp4|webm/i.test(String(o.type || "")));
  }

  // 1) <video>/<source> etiketleri (size/label/res öznitelikleri)
  try {
    for (const el of doc.querySelectorAll("video, audio, video source, audio source")) {
      const g = (n) => (el.getAttribute ? el.getAttribute(n) : null);
      const label = g("label") || g("res") || g("size") || g("data-res") || g("data-quality") || g("data-label") || g("title") || "";
      const src = g("src") || el.currentSrc;
      if (src) add(src, label, 0, "source-tag", /mpegurl|m3u8|mp4|webm/i.test(g("type") || ""));
    }
  } catch { /* DOM erişilemedi */ }

  // 2) jwplayer
  try {
    const jw = win.jwplayer;
    if (typeof jw === "function") {
      const p = jw();
      const list = (p && p.getPlaylist && p.getPlaylist()) || [];
      for (const item of list) {
        if (item && Array.isArray(item.sources)) item.sources.forEach((s) => fromObj(s, "jwplayer"));
        else fromObj(item, "jwplayer");
      }
      const cfg = p && p.getConfig && p.getConfig();
      if (cfg && Array.isArray(cfg.sources)) cfg.sources.forEach((s) => fromObj(s, "jwplayer"));
    }
  } catch { /* oynatıcı hazır değil */ }

  // 3) video.js
  try {
    const vjs = win.videojs;
    if (vjs && typeof vjs.getPlayers === "function") {
      const ps = vjs.getPlayers() || {};
      for (const k of Object.keys(ps)) {
        const p = ps[k];
        if (!p) continue;
        ((p.currentSources && p.currentSources()) || []).forEach((s) => fromObj(s, "videojs"));
        if (p.options_ && Array.isArray(p.options_.sources)) p.options_.sources.forEach((s) => fromObj(s, "videojs"));
      }
    }
  } catch { /* yok */ }

  // 4) HLS.js levels (bilinen genel adlar + levels/loadSource taşıyan pencere özellikleri; en fazla 400 anahtar)
  try {
    const cands = [win.hls, win.hlsPlayer, win.hls_player, win.player && win.player.hls];
    let n = 0;
    for (const k in win) {
      if (++n > 400) break;
      try { const v = win[k]; if (v && typeof v === "object" && Array.isArray(v.levels) && typeof v.loadSource === "function") cands.push(v); } catch { /* erişilemez */ }
    }
    for (const h of new Set(cands.filter(Boolean))) {
      if (!Array.isArray(h.levels)) continue;
      for (const lv of h.levels) {
        if (!lv) continue;
        const u = Array.isArray(lv.url) ? lv.url[0] : (lv.url || lv.uri);
        add(u, lv.name || (lv.height ? lv.height + "p" : ""), lv.height, "hlsjs", true);
      }
    }
  } catch { /* yok */ }

  // 5) satır içi script/JSON metni: sources:[{file,label}], Playerjs file:"[1080p]url,...", çıplak .m3u8 adresleri
  try {
    let n = 0;
    for (const sc of doc.querySelectorAll("script:not([src])")) {
      if (++n > 30 || out.length >= MAX) break;
      const text = String(sc.textContent || "").slice(0, 300000);
      if (!text) continue;
      let m;
      const reObj = /\{[^{}]{0,600}?["']?(?:file|src)["']?\s*:\s*["']([^"']+)["'][^{}]{0,600}?\}/g;   // etiketli kaynaklar önce
      while ((m = reObj.exec(text)) !== null) {
        const lab = /["']?(?:label|res|size|quality|title)["']?\s*:\s*["']?([^"',}]{1,30})/.exec(m[0]);
        if (!parseList(m[1], "script-sources")) add(m[1], lab ? lab[1] : "", 0, "script-sources");
      }
      const reFile = /["']?\bfile["']?\s*:\s*(["'`])((?:(?!\1)[^\\]|\\.)*)\1/g;
      while ((m = reFile.exec(text)) !== null) {
        if (!parseList(m[2], "script-file")) add(m[2], "", 0, "script-file");
      }
      const reM3 = /(?:https?:)?(?:\\?\/){2}[^\s"'<>\\,\[\]]+(?:\\\/[^\s"'<>\\,\[\]]+)*\.m3u8[^\s"'<>\\,\[\]]*/g;
      let c = 0;
      while ((m = reM3.exec(text)) !== null && c++ < 10) add(m[0], "", 0, "script-url");
    }
  } catch { /* yok */ }

  if (expose) { expose.parseList = (s) => { const before = out.length; parseList(s, "test"); return out.slice(before); }; expose.heightOfLabel = heightOfLabel; }
  return out;
}
