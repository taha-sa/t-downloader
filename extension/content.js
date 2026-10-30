// T-Downloader content script. Düğme SAYFAYA ÇİZİLMEZ; yalnızca video geometrisi bildirilir.
(() => {
  const THROTTLE_MS = 100;
  const MIN_W = 50, MIN_H = 30;
  let lastSent = 0, timer = null, lastJson = "", wasVisible = false;

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

  function buildPayload(doc, win) {
    const picked = pickMedia(doc, win);
    const base = {
      fullscreen: !!doc.fullscreenElement,
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
    lastSent = Date.now();
    const p = buildPayload(document, window);
    if (!p.visible && !wasVisible) return; // hiç görünmediyse sessiz kal
    const json = JSON.stringify(p);
    if (json === lastJson) return;
    lastJson = json;
    wasVisible = p.visible;
    post({ kind: "geometry", payload: p });
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
    globalThis.__TD_TEST__.exports = { buildPayload, pickMedia, collectLinks, flush, schedule };
  }
})();
