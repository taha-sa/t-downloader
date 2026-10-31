// T-Downloader content script. Düğme SAYFAYA ÇİZİLMEZ; yalnızca video geometrisi bildirilir.
(() => {
  const THROTTLE_MS = 100;
  const MIN_W = 50, MIN_H = 30;
  let lastSent = 0, timer = null, lastJson = "", wasVisible = false;
  // Geometri yalnızca eski yerel düğme penceresi açıkken gönderilir (sayfa içi düğme geometriye ihtiyaç duymaz).
  let nativeGeo = false;
  const applyCfg = (r) => { const v = !!(r && r.nativeButton); if (v !== nativeGeo) { nativeGeo = v; lastJson = ""; if (v) schedule(); } };
  try {
    chrome.runtime.sendMessage({ kind: "cfg" }, (r) => { void chrome.runtime.lastError; applyCfg(r); });
    chrome.runtime.onMessage.addListener((m) => { if (m && m.kind === "cfg") applyCfg(m); });
  } catch { /* test ortamı */ }

  function isShown(el, win) {
    const cs = win.getComputedStyle(el);
    return cs.display !== "none" && cs.visibility !== "hidden" && parseFloat(cs.opacity || "1") > 0;
  }

  // Görünür alanı en büyük video/audio öğesi.
  function pickMedia(doc, win) {
    const vw = win.innerWidth, vh = win.innerHeight;
    let best = null, bestArea = 0;
    for (const el of doc.querySelectorAll("video,audio")) {
      const r = el.getBoundingClientRect();
      if (r.width < MIN_W || r.height < MIN_H || !isShown(el, win)) continue;
      const w = Math.min(r.right, vw) - Math.max(r.left, 0);
      const h = Math.min(r.bottom, vh) - Math.max(r.top, 0);
      if (w <= 0 || h <= 0) continue;
      if (w * h > bestArea) { best = el; bestArea = w * h; }
    }
    return best ? { el: best, area: bestArea } : null;
  }

  // ---- iframe oynatıcılar ----
  // Film siteleri videoyu başka alan adındaki iframe'de oynatır. Alt çerçevedeki içerik script'i konumunu üst
  // çerçeveye postMessage ile bildirir; üst çerçeve kendi görünümüne çevirip (iframe'in konumu + kenarlıkları) tek
  // bir geometri olarak background'a iletir. event.source ile iframe elemanı eşleşir: hangi iframe olduğu kesindir.
  const isTop = (() => { try { return window.top === window; } catch { return false; } })();
  const children = new Map(); // WindowProxy -> alt çerçevenin son payload'ı (kendi görünüm koordinatlarında)

  function frameElementFor(doc, src) {
    for (const f of doc.querySelectorAll("iframe,frame")) {
      try { if (f.contentWindow === src) return f; } catch { /* erişilemez */ }
    }
    return null;
  }

  // Alt çerçeve rect'ini üst çerçevenin görünümüne çevir ve görünür kısma kırp. Görünmüyorsa null.
  function translateChild(frameEl, cp, win) {
    const fr = frameEl.getBoundingClientRect();
    const cs = win.getComputedStyle(frameEl);
    const n = (v) => parseFloat(v) || 0;
    const ox = fr.left + n(cs.borderLeftWidth) + n(cs.paddingLeft);
    const oy = fr.top + n(cs.borderTopWidth) + n(cs.paddingTop);
    const cw = fr.width - n(cs.borderLeftWidth) - n(cs.borderRightWidth) - n(cs.paddingLeft) - n(cs.paddingRight);
    const ch = fr.height - n(cs.borderTopWidth) - n(cs.borderBottomWidth) - n(cs.paddingTop) - n(cs.paddingBottom);
    if (cw <= 0 || ch <= 0 || !isShown(frameEl, win)) return null;
    const sx = cp.viewport?.width > 0 ? cw / cp.viewport.width : 1;
    const sy = cp.viewport?.height > 0 ? ch / cp.viewport.height : 1;
    const r = cp.rect;
    const rect = {
      left: ox + r.left * sx, top: oy + r.top * sy, right: ox + r.right * sx, bottom: oy + r.bottom * sy,
    };
    const l = Math.max(rect.left, ox, 0), t = Math.max(rect.top, oy, 0);
    const rr = Math.min(rect.right, ox + cw, win.innerWidth), bb = Math.min(rect.bottom, oy + ch, win.innerHeight);
    if (rr - l < MIN_W || bb - t < MIN_H) return null;
    return { rect, area: (rr - l) * (bb - t), fullscreen: !!cp.fullscreen };
  }

  function onChildMessage(e, doc = document, win = window) {
    const d = e && e.data;
    if (!d || d.__tdGeo !== 1 || !d.payload || typeof d.payload !== "object" || !d.payload.rect || !e.source) return false;
    if (children.size >= 20 && !children.has(e.source)) return false;
    children.set(e.source, d.payload);
    return true;
  }

  function bestChild(doc, win) {
    let best = null;
    for (const [src, cp] of [...children]) {
      const el = frameElementFor(doc, src);
      if (!el) { children.delete(src); continue; }
      if (!cp.visible) continue;
      const t = translateChild(el, cp, win);
      if (t && (!best || t.area > best.area)) best = t;
    }
    return best;
  }

  function buildPayload(doc, win) {
    let picked = pickMedia(doc, win);
    const child = bestChild(doc, win);
    if (child && (!picked || child.area > picked.area)) picked = { el: { getBoundingClientRect: () => child.rect }, area: child.area, fullscreen: child.fullscreen };
    const base = {
      fullscreen: !!doc.fullscreenElement || !!picked?.fullscreen,
      devicePixelRatio: win.devicePixelRatio || 1,
      viewport: { width: win.innerWidth, height: win.innerHeight },
      pageUrl: win.location.href,
      pageTitle: doc.title || "",
    };
    if (!picked) return { ...base, visible: false, area: 0, rect: { left: 0, top: 0, right: 0, bottom: 0 } };
    const r = picked.el.getBoundingClientRect();
    return {
      ...base,
      visible: true,
      area: Math.round(picked.area),
      rect: {
        left: Math.round(r.left), top: Math.round(r.top),
        right: Math.round(r.right), bottom: Math.round(r.bottom),
      },
    };
  }

  function post(msg) {
    try { chrome.runtime.sendMessage(msg).catch?.(() => {}); } catch { /* bağlam geçersiz */ }
  }

  function flush() {
    timer = null;
    if (!nativeGeo) return;
    lastSent = Date.now();
    const p = buildPayload(document, window);
    if (!p.visible && !wasVisible) return; // hiç görünmediyse sessiz kal
    const json = JSON.stringify(p);
    if (json === lastJson) return;
    lastJson = json;
    wasVisible = p.visible;
    if (isTop) post({ kind: "geometry", payload: p });
    else {
      // Alt çerçeve: konumu üst çerçeve çevirir; background'a doğrudan gönderme (koordinatlar iframe'e göreli).
      try { window.parent.postMessage({ __tdGeo: 1, payload: p }, "*"); } catch { /* erişilemez */ }
    }
  }

  function schedule() {
    if (timer) return;
    timer = setTimeout(flush, Math.max(0, THROTTLE_MS - (Date.now() - lastSent)));
  }

  // ---- Alt tuşu: tarayıcının kendi indirmesine izin ver ----
  const alt = (ms) => post({ kind: "alt", ms });
  window.addEventListener("keydown", (e) => { if (e.key === "Alt") alt(10000); }, true);
  window.addEventListener("keyup", (e) => { if (e.key === "Alt") alt(0); }, true);
  window.addEventListener("blur", () => alt(0));
  const altClick = (e) => { if (e.altKey) alt(3000); };
  window.addEventListener("mousedown", altClick, true);
  window.addEventListener("click", altClick, true);

  window.addEventListener("message", (e) => { if (onChildMessage(e)) schedule(); });

  // ---- geometri tetikleyicileri ----
  window.addEventListener("scroll", schedule, { capture: true, passive: true });
  window.addEventListener("resize", schedule, { passive: true });
  window.addEventListener("mousemove", schedule, { passive: true });
  document.addEventListener("fullscreenchange", schedule);
  for (const ev of ["loadedmetadata", "play", "pause", "emptied"]) {
    document.addEventListener(ev, schedule, true);
  }
  const startObs = () => {
    if (!document.documentElement) return;
    new MutationObserver(schedule).observe(document.documentElement, {
      childList: true, subtree: true, attributes: true, attributeFilter: ["style", "class", "hidden"],
    });
  };
  startObs();
  setInterval(() => { if (!document.hidden) schedule(); }, 1500);

  // ---- link toplama (sağ tık menüsü) ----
  function collectLinks(selectionOnly) {
    const sel = selectionOnly ? window.getSelection() : null;
    const seen = new Set();
    for (const a of document.querySelectorAll("a[href]")) {
      if (sel && (sel.isCollapsed || !sel.containsNode(a, true))) continue;
      if (/^(https?|ftp):/i.test(a.href)) seen.add(a.href);
      if (seen.size >= 500) break;
    }
    return [...seen];
  }

  try {
    chrome.runtime.onMessage.addListener((msg, _s, sendResponse) => {
      if (msg && msg.kind === "collectLinks") {
        sendResponse({ urls: collectLinks(!!msg.selectionOnly) });
      }
      return false;
    });
  } catch { /* test ortamı */ }

  // Test için yalnızca __TD_TEST__ tanımlıysa dışa aç.
  if (typeof globalThis.__TD_TEST__ !== "undefined") {
    globalThis.__TD_TEST__.exports = { buildPayload, pickMedia, collectLinks, flush, schedule, onChildMessage, translateChild };
  }
})();
