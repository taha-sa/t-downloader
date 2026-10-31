// T-Downloader sayfa içi video düğmesi ("T-Downloader ile İndir").
// Düğme ve kalite menüsü sayfanın DOM'una (Shadow DOM içinde, sayfa CSS'inden yalıtılmış) çizilir; böylece
// kaydırmada video ile birlikte akıcı hareket eder. Her çerçeve (iframe dahil) kendi videosu için kendi düğmesini çizer.
// Kaliteler App'ten gelir: eklenti -> App `qualities`, App -> eklenti `qualitiesResult`; seçim `qualitySelect` ile gider.
(() => { try {
  if (window.__tdButtonLoaded) return;
  window.__tdButtonLoaded = true;
  // Küçük çerçeveler (reklam/izleyici) yükleme anında 0 genişlikte olabilir; eleme qualifies() içinde, fare hareketinde yapılır.

  let enabled = true;
  const send = (msg, cb) => {
    try { chrome.runtime.sendMessage(msg, (r) => { void chrome.runtime.lastError; cb && cb(r); }); }
    catch { cb && cb(undefined); }                       // eklenti yenilendi: bağlam geçersiz
  };
  send({ kind: "cfg" }, (r) => { if (r) { enabled = r.showVideoButton !== false; syncTick(); } });
  try {
    chrome.runtime.onMessage.addListener((m) => {
      if (m && m.kind === "cfg") { enabled = m.showVideoButton !== false; if (!enabled) hideAll(); syncTick(); }
    });
  } catch { /* test ortamı */ }

  // ---------------------------------------------------------------- hangi video düğme alır
  const PREVIEW_SEL = "ytd-video-preview, ytd-miniplayer, #inline-preview-player, ytd-rich-grid-media, ytd-thumbnail, yt-lockup-view-model, ytm-inline-player-renderer";
  const MIN_W = 200, MIN_H = 120;
  function qualifies(v, win = window) {
    if (!v || v.tagName !== "VIDEO") return false;
    try { if (v.closest && v.closest(PREVIEW_SEL)) return false; } catch { /* eski tarayıcı */ }
    const r = v.getBoundingClientRect();
    if (r.width < MIN_W || r.height < MIN_H) return false;
    const ratio = r.width / r.height;
    if (ratio < 0.4 || ratio > 3.6) return false;
    const cs = win.getComputedStyle(v);
    return cs.display !== "none" && cs.visibility !== "hidden" && parseFloat(cs.opacity || "1") >= 0.05;
  }

  // Düğmenin görüntü alanındaki konumu: videonun İÇİNDE, sağ üst köşeye 8 px içeriden bağlı. Viewport'a kıstırma (clamp)
  // YOK: video kayınca düğme onunla birlikte kayar; düğme görüntü alanının dışına çıkarsa (video ekran dışında) visible=false.
  function buttonPos(r, bw, bh, vw, vh) {
    const left = r.left + r.width - bw - 8, top = r.top + 8;
    const visible = r.width > 0 && r.height > 0 && r.bottom > 0 && r.top < vh && r.right > 0 && r.left < vw &&
      top + bh > 0 && top < vh && left + bw > 0 && left < vw;
    return { left, top, visible };
  }

  // Kararlılık: video dikdörtgeni STABLE_MS boyunca değişmeden (konum sayfa koordinatında, kaydırma sayılmaz) ve yeterince
  // büyükse düğme görünebilir. Görünürken yalnızca BÜYÜK boyut değişimi (oynatıcı yeniden düzenlendi) beklemeyi yeniden başlatır.
  const STABLE_MS = 400, SHOW_MIN_W = 200, SHOW_MIN_H = 100, RESIZE_RATIO = 0.15;
  const newTracker = () => ({ sig: null, since: 0, shown: false, ref: null });
  function stepTracker(t, r, sx, sy, now) {
    const sig = { x: r.left + sx, y: r.top + sy, w: r.width, h: r.height };
    if (!t.sig) { t.sig = sig; t.since = now; return false; }
    if (t.shown) {
      const dw = Math.abs(sig.w - t.ref.w) / Math.max(1, t.ref.w), dh = Math.abs(sig.h - t.ref.h) / Math.max(1, t.ref.h);
      t.sig = sig;
      if (dw > RESIZE_RATIO || dh > RESIZE_RATIO || sig.w < SHOW_MIN_W || sig.h < SHOW_MIN_H) { t.shown = false; t.since = now; return false; }
      return true;                                   // konum değişimi (kaydırma): anında takip
    }
    const moved = Math.abs(sig.x - t.sig.x) > 1 || Math.abs(sig.y - t.sig.y) > 1 || Math.abs(sig.w - t.sig.w) > 1 || Math.abs(sig.h - t.sig.h) > 1;
    t.sig = sig;
    if (moved) { t.since = now; return false; }
    if (now - t.since >= STABLE_MS && sig.w >= SHOW_MIN_W && sig.h >= SHOW_MIN_H) { t.shown = true; t.ref = { w: sig.w, h: sig.h }; return true; }
    return false;
  }
  let clock = () => (typeof performance !== "undefined" && performance.now ? performance.now() : Date.now());
  let trackers = new WeakMap();
  const trackerOf = (v) => { let t = trackers.get(v); if (!t) { t = newTracker(); trackers.set(v, t); } return t; };
  const scrollXY = () => [typeof scrollX === "number" ? scrollX : 0, typeof scrollY === "number" ? scrollY : 0];

  // ---------------------------------------------------------------- DOM (Shadow DOM)
  let logo = "";
  try { logo = chrome.runtime.getURL("icons/icon32.png"); } catch { /* yok */ }
  const host = document.createElement("div");
  host.id = "tdownloader-overlay";
  host.style.cssText = "all:initial;position:fixed;z-index:2147483647;left:0;top:0;width:0;height:0;";
  // "closed": sayfa betiği host.shadowRoot ile düğmeye/menüye erişip tıklama simüle edemez (A2). Ek olarak tüm işleyiciler isTrusted ister.
  const sh = host.attachShadow({ mode: "closed" });
  sh.innerHTML = `<style>
    *{box-sizing:border-box;font:600 13px system-ui,"Segoe UI",Tahoma,sans-serif}
    #btn{position:fixed;display:none;align-items:center;gap:5px;padding:3px 8px 3px 5px;background:rgba(245,196,0,.75);color:#111;border:1px solid rgba(17,17,17,.7);
      border-radius:4px;cursor:pointer;user-select:none;opacity:0;transition:opacity .25s ease,background .12s;pointer-events:auto;white-space:nowrap;
      box-shadow:0 1px 5px #0006;font:600 12px/16px system-ui,"Segoe UI",Tahoma,sans-serif}
    #btn *{font:inherit}
    #btn img{width:14px;height:14px;display:block}
    #btn.vis{opacity:1}
    #btn.vis:hover,#btn.vis.on{background:#ffd21f;border-color:#111}
    #menu{position:fixed;display:none;width:290px;max-height:360px;overflow:auto;background:#141414;color:#f3f3f3;pointer-events:auto;
      border:1px solid #f5c400;border-radius:6px;box-shadow:0 8px 28px #000b;padding:0 0 4px}
    #menu .h{padding:8px 12px 7px;font-weight:700;color:#f5c400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;border-bottom:1px solid #333;background:#0c0c0c}
    #menu .g{padding:8px 12px 3px;font-size:11px;font-weight:700;letter-spacing:.04em;text-transform:uppercase;color:#9a8a3a}
    #menu .row{padding:7px 12px;cursor:pointer;display:flex;justify-content:space-between;gap:10px;font-weight:500}
    #menu .row:hover,#menu .row.def{background:#2a2400}
    #menu .row:hover{background:#f5c400;color:#111}
    #menu .row.oth{opacity:.6}
    #menu .row small{font-weight:400;opacity:.7}
    #menu .msg{padding:10px 12px;font-weight:400;font-size:12px;color:#c9c2a0}#menu .err{color:#ff9d8e}
    #menu .spin{display:inline-block;animation:r 1s linear infinite}@keyframes r{to{transform:rotate(360deg)}}
  </style><div id="btn">${logo ? `<img src="${logo}" alt="">` : ""}<span>T-Downloader ile İndir</span></div><div id="menu"></div>`;
  const btn = sh.getElementById("btn"), menu = sh.getElementById("menu");
  for (const ev of ["mousedown", "mouseup", "pointerdown", "pointerup", "click", "dblclick", "contextmenu", "wheel"])
    host.addEventListener(ev, (e) => e.stopPropagation());   // sayfanın oynatıcısı bizim tıklamalarımıza tepki vermesin

  let target = null, hideT = 0, menuOpen = false, token = 0, raf = 0;

  const mount = () => {
    const fs = document.fullscreenElement;
    const p = fs && fs.tagName !== "VIDEO" ? fs : (document.documentElement || document);
    if (host.parentNode !== p) p.appendChild(host);
  };
  // Tam ekran/sinema geçişinde yeniden kararlılık beklenir (düğme gizlenir, sonra doğru yerde belirir): zıplama yok.
  document.addEventListener("fullscreenchange", () => { trackers = new WeakMap(); if (host.isConnected) mount(); });

  let lastSig = "", lastCover = 0;      // değişmeyen kare: pahalı kontroller atlanır
  function setShown(on) {
    if (!on) { lastSig = ""; clipT = clipB = 0; }
    if (on) {
      if (btn.style.display !== "flex") { btn.style.display = "flex"; void btn.offsetWidth; }   // opaklık 0'dan başlasın (fade-in)
      btn.classList.add("vis");
    } else { btn.style.display = "none"; btn.classList.remove("vis"); }
  }
  // ---- sabit/yapışkan öğelerin (site üst/alt çubukları) örtmesi ----
  // Neden eski denetim çalışmıyordu: tek nokta (düğme merkezi) için elementFromPoint bakılıyordu; düğme görünürken o nokta
  // BİZİM düğmemizdir (host) ve denetim "örtü yok" diyordu. Ayrıca saydam/pointer-events:none çubuklar tek noktayla bulunamaz.
  // Şimdi: sabit/yapışkan aday listesi (seyrek yenilenir) + elementsFromPoint yığınındaki sabit atalar, dikdörtgen olarak kırpılır.
  const COVER_MS = 100, SCAN_MS = 250, ZONE_W = 220;
  let clipT = 0, clipB = 0;          // düğmenin üstten/alttan kırpılan piksel sayısı
  let cands = [], scanDirty = true, lastScan = -1e9, lastMeasure = -1e9, coverRects = [];
  const isFixedish = (n) => { try { const p = window.getComputedStyle(n).position; return p === "fixed" || p === "sticky"; } catch { return false; } };
  const isOwn = (n) => n === host || !!(host.contains && host.contains(n));
  const unrelated = (n) => !isOwn(n) && !(n.contains && n.contains(target)) && !(target.contains && target.contains(n));
  function scanCands(now) {
    lastScan = now; scanDirty = false;
    const out = [];
    try {
      const all = document.getElementsByTagName("*");
      const lim = Math.min(all.length, 20000);
      for (let i = 0; i < lim; i++) { const n = all[i]; if (n !== host && isFixedish(n)) out.push(n); }
    } catch { /* getElementsByTagName yok */ }
    cands = out;
  }
  const areaRect = (el) => { try { const q = el.getBoundingClientRect(); return q.width > 0 && q.height > 0 ? { l: q.left, t: q.top, r: q.right, b: q.bottom } : null; } catch { return null; } };
  function measure(r, now) {
    if (now - lastMeasure < COVER_MS) return;
    lastMeasure = now;
    if (scanDirty && now - lastScan >= SCAN_MS) scanCands(now);
    const out = [];
    for (const n of cands) { if (n.isConnected !== false && unrelated(n)) { const q = areaRect(n); if (q) out.push(q); } }
    try {
      if (typeof document.elementsFromPoint === "function") {
        const x = Math.max(1, Math.min(innerWidth - 1, r.right - 30));
        for (const y of [Math.max(r.top, 0) + 2, Math.min(r.bottom, innerHeight) - 2]) {
          for (const el of document.elementsFromPoint(x, y)) {
            if (el === target || (el.contains && el.contains(target))) break;      // videoya ulaştık
            if (isOwn(el) || (target.contains && target.contains(el))) continue;   // kendi host'umuz/menümüz atlanır
            for (let n = el, i = 0; n && n.nodeType !== 9 && i < 12; n = n.parentElement, i++) {
              if (isFixedish(n) && unrelated(n)) { const q1 = areaRect(el), q2 = areaRect(n); if (q1) out.push(q1); if (q2) out.push(q2); break; }
            }
          }
        }
      }
    } catch { /* elementsFromPoint yok */ }
    coverRects = out;
  }
  // Videonun üstünü/altını örten sabit öğelerin sınırları (viewport koordinatı). Her karede ucuz aritmetik.
  function clipFor(r) {
    let top = -Infinity, bot = Infinity;
    const vt = Math.max(r.top, 0), vb = Math.min(r.bottom, innerHeight);
    for (const q of coverRects) {
      if (q.r - q.l >= innerWidth * 0.9 && q.b - q.t >= innerHeight * 0.9) continue;       // tüm sayfayı kaplayan kap: çubuk değil
      if (!(q.l < r.right && q.r > r.right - ZONE_W && q.t < r.bottom && q.b > r.top)) continue;
      if (q.t <= vt + 1) top = Math.max(top, q.b);
      if (q.b >= vb - 1) bot = Math.min(bot, q.t);
    }
    return { top, bot };
  }
  function place() {
    if (!target || !target.isConnected || !qualifies(target)) return hide();
    const r = target.getBoundingClientRect();
    const [sx, sy] = scrollXY();
    const now = clock();
    // Düğme görünürken video/kaydırma/pencere değişmediyse (ve örtü ölçümü yakın zamanda yapıldıysa) kare boşa hesaplanmaz.
    const sig = r.left + "," + r.top + "," + r.width + "," + r.height + "," + sx + "," + sy + "," + innerWidth + "," + innerHeight;
    if (lastSig === sig && now - lastCover < COVER_MS) return;
    if (!stepTracker(trackerOf(target), r, sx, sy, now)) { setShown(false); if (menuOpen) closeMenu(); return; }
    setShown(true);
    measure(r, now);
    const bw = btn.offsetWidth, bh = btn.offsetHeight || 30;
    const cl = clipFor(r);
    const p = buttonPos(r, bw, bh, innerWidth, innerHeight);      // her zaman videonun GERÇEK sağ üst köşesi
    if (!p.visible) { setShown(false); if (menuOpen) closeMenu(); return; }
    // Örten sabit çubuk düğmeyle kesişiyorsa düğme çubuğun altına giriyormuş gibi kırpılır (konum değişmez).
    const cTop = Math.max(0, Math.min(bh, cl.top - p.top)), cBot = Math.max(0, Math.min(bh, p.top + bh - cl.bot));
    if (cTop + cBot >= bh) { setShown(false); if (menuOpen) closeMenu(); return; }   // tamamen örtülü
    if ((cTop > 0 || cBot > 0) && menuOpen) closeMenu();         // kırpma başladı: menü çubuğun üstünde asılı kalmasın
    clipT = cTop; clipB = cBot;
    btn.style.clipPath = cTop > 0 || cBot > 0 ? "inset(" + cTop + "px 0 " + cBot + "px 0)" : "none";
    btn.style.left = p.left + "px"; btn.style.top = p.top + "px";
    lastSig = sig; lastCover = now;
    if (menuOpen) placeMenu();
  }
  // Hedef varken her karede konumu güncelle: kaydırma/animasyonda video ile birebir hareket eder (konumda CSS geçişi yok).
  // rAF döngüsü yalnızca düğme/menü gerekliyken ve video görünür alandayken (IntersectionObserver) çalışır.
  function loop() {
    raf = 0;
    if (!target && !menuOpen) return;
    if (document.hidden) return;
    if (!menuOpen && target && ioSeen.has(target) && !inView.has(target)) { hide(); return; }   // video görünür alanda değil: döngü durur
    place();
    raf = requestAnimationFrame(loop);
  }
  function startLoop() { if (!raf) raf = requestAnimationFrame(loop); }

  function placeMenu() {
    const b = btn.getBoundingClientRect();
    menu.style.display = "block";
    menu.style.left = Math.max(4, Math.min(innerWidth - 294, b.right - 290)) + "px";
    const below = b.bottom + 6, h = Math.min(menu.scrollHeight, 360);
    menu.style.top = (below + h > innerHeight ? Math.max(4, b.top - h - 6) : below) + "px";
  }
  function hide() { if (menuOpen) return; setShown(false); target = null; }
  function hideAll() { closeMenu(); setShown(false); target = null; }
  // Kalite listesi okunurken menü kapanırsa (veya sekme/çerçeve gizlenirse) bekleyen istek iptal edilir (App yt-dlp'yi öldürür).
  let pendingRid = null;
  function closeMenu() {
    menuOpen = false; menu.style.display = "none"; btn.classList.remove("on"); token++;
    if (pendingRid) { send({ kind: "qualitiesCancel", rid: pendingRid }); pendingRid = null; }
  }

  // ---- video keşfi: IntersectionObserver ile görünür alandaki videolar tutulur; mousemove'da tüm <video>lar taranmaz ----
  let tickId = 0;
  const inView = new Set();             // görünür alandaki videolar
  const ioSeen = new WeakSet();         // en az bir IntersectionObserver bildirimi almış olanlar
  const known = new WeakSet();
  const io = typeof IntersectionObserver === "function" ? new IntersectionObserver((entries) => {
    for (const en of entries) { ioSeen.add(en.target); if (en.isIntersecting) inView.add(en.target); else inView.delete(en.target); }
    syncTick();
  }, { threshold: 0.05 }) : null;
  function track(v) {
    if (!v || v.tagName !== "VIDEO" || known.has(v)) return;
    known.add(v);
    if (io) io.observe(v); else { inView.add(v); syncTick(); }
  }
  function discover() { try { for (const v of document.querySelectorAll("video")) track(v); } catch { /* DOM hazır değil */ } }
  discover();
  document.addEventListener("DOMContentLoaded", discover);
  addEventListener("load", discover);
  for (const ev of ["loadedmetadata", "play", "playing"]) document.addEventListener(ev, (e) => track(e.target), true);

  function inRect(v, x, y) { const r = v.getBoundingClientRect(); return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom; }
  // İmlecin altındaki video: elementsFromPoint (yığın derinliği kadar iş); yoksa yalnızca görünür alandaki videolar denenir.
  function videoUnder(x, y) {
    try {
      if (typeof document.elementsFromPoint === "function") {
        for (const el of document.elementsFromPoint(x, y)) if (el.tagName === "VIDEO") { track(el); return el; }
      }
    } catch { /* eski tarayıcı */ }
    for (const v of inView) if (inRect(v, x, y)) return v;
    return null;
  }
  let lastMove = -1e9;
  addEventListener("mousemove", (e) => {
    if (!e.isTrusted || !enabled || menuOpen) return;   // sayfanın sentetik mousemove'u düğmeyi/menüyü tetikleyemez
    const t = clock();
    if (t - lastMove < 60) return;                       // fare olayı sağanağında en çok ~16 tarama/sn
    lastMove = t;
    const v = videoUnder(e.clientX, e.clientY);
    if (v && qualifies(v)) {
      mount(); target = v; place(); startLoop();
      clearTimeout(hideT); hideT = setTimeout(() => { if (!btn.matches(":hover")) hide(); }, 3500);
    }
  }, { passive: true });
  addEventListener("scroll", () => { scanDirty = true; if (target) place(); }, { passive: true, capture: true });
  // Görünür alandaki videoların kararlılığı hover'dan önce ölçülür (ilk hover'da 400 ms beklemeyi önler).
  // Zamanlayıcı yalnızca görünür alanda video varken çalışır; yoksa durur.
  function tickOnce() {
    if (document.hidden || !enabled) return;
    try {
      const [sx, sy] = scrollXY(), now = clock();
      let n = 0;
      for (const v of inView) {
        if (++n > 6) break;
        const r = v.getBoundingClientRect();
        if (r.width >= 100 && r.height >= 60) stepTracker(trackerOf(v), r, sx, sy, now);
      }
    } catch { /* DOM hazır değil */ }
  }
  function syncTick() {
    const want = inView.size > 0 && enabled && typeof setInterval === "function";
    if (want && !tickId) tickId = setInterval(tickOnce, 250);
    else if (!want && tickId && typeof clearInterval === "function") { clearInterval(tickId); tickId = 0; }
  }
  // Olay vermeden eklenen videolar için seyrek güvenlik ağı (4 sn).
  if (typeof setInterval === "function") setInterval(() => { if (!document.hidden && enabled) discover(); }, 4000);
  addEventListener("resize", () => { scanDirty = true; if (target) place(); }, { passive: true });
  // DOM değişimi (yeni çubuk/stil): aday listesi bir sonraki ölçümde (en çok 250 ms'de bir) yenilenir.
  try {
    if (typeof MutationObserver === "function" && document.documentElement)
      new MutationObserver(() => { scanDirty = true; }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["style", "class"] });
  } catch { /* gözlemci yok */ }
  addEventListener("mousedown", (e) => { if (e.isTrusted && menuOpen && !e.composedPath().includes(host)) { closeMenu(); hide(); } }, true);
  addEventListener("keydown", (e) => { if (e.isTrusted && e.key === "Escape" && menuOpen) { closeMenu(); hide(); } }, true);
  // Sekme/çerçeve gizlenince menü kapansın
  document.addEventListener("visibilitychange", () => { if (document.hidden) { closeMenu(); hide(); } });

  // ---------------------------------------------------------------- menü
  const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const mb = (n) => (n > 0 ? (n >= 1073741824 ? (n / 1073741824).toFixed(2) + " GB" : (n / 1048576).toFixed(n < 10485760 ? 1 : 0) + " MB") : "");

  function drawMessage(title, html, cls = "") {
    menu.innerHTML = `<div class="h" title="${esc(title)}">${esc(title)}</div><div class="msg ${cls}">${html}</div>`;
    placeMenu();
  }
  function groupOf(o) { return o.kind === "other" ? "Diğer dosyalar" : o.kind === "file" ? "Dosya" : o.audioOnly ? "Sadece ses" : "Video"; }
  function drawOptions(res) {
    let h = `<div class="h" title="${esc(res.title)}">${esc(res.title || "Video")}</div>`;
    let last = "";
    (res.options || []).forEach((o, i) => {
      const g = groupOf(o);
      if (g !== last) { h += `<div class="g">${esc(g)}</div>`; last = g; }
      h += `<div class="row${o.isDefault ? " def" : ""}${o.kind === "other" ? " oth" : ""}" data-i="${i}"><span>${esc(o.label)}</span><small>${esc(o.label.includes("MB") ? "" : mb(o.size))}</small></div>`;
    });
    menu.innerHTML = h;
    menu.onclick = (e) => {
      if (!e.isTrusted) return;                        // yalnızca gerçek kullanıcı tıklaması indirme başlatır (A2)
      const row = e.target.closest(".row"); if (!row) return;
      const i = +row.dataset.i, my = token;
      drawMessage(res.title || "Video", "Gönderiliyor…");
      send({ kind: "qualitySelect", selectionId: res.selectionId, index: i }, (r) => {
        if (my !== token) return;                      // menü bu arada kapandı
        if (r && r.ok === true) {
          drawMessage(res.title || "Video", "✓ T-Downloader'a gönderildi");
          setTimeout(() => { if (my === token) { closeMenu(); hide(); } }, 1100);
        } else {
          drawMessage(res.title || "Video", "T-Downloader'a ulaşılamadı; indirme başlatılamadı. Uygulama çalışıyor mu?", "err");
          setTimeout(() => { if (my === token) { closeMenu(); hide(); } }, 4000);
        }
      });
    };
    placeMenu();
  }

  function openMenu() {
    menuOpen = true; btn.classList.add("on"); startLoop();
    const my = ++token, v = target;
    const title = (document.title || location.hostname).slice(0, 120);
    drawMessage(title, `<span class="spin">⟳</span> Kaliteler okunuyor…`);
    const src = v && /^https?:/i.test(v.currentSrc || v.src || "") ? (v.currentSrc || v.src) : "";
    let done = false;
    const rid = "q" + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
    pendingRid = rid;
    const fin = (r) => {
      if (done || my !== token) return; done = true;
      if (pendingRid === rid) pendingRid = null;
      if (r && r.ok && r.options && r.options.length) return drawOptions(r);
      const code = r && r.errorCode;
      const msg = !r ? "T-Downloader yanıt vermedi. Uygulama çalışıyor mu?"
        : code === "no_ytdlp" ? "Bu video için yt-dlp gerekiyor (Seçenekler > Video)."
        : (r.error || "Bu video okunamadı.");
      drawMessage(title, `${esc(msg)}<div class="row" id="retry" style="margin:8px -12px -10px"><span>↻ Tekrar dene</span></div>`, "err");
      const rt = sh.getElementById("retry");
      if (rt) rt.onclick = (e) => { if (e.isTrusted) { closeMenu(); openMenu(); } };
    };
    setTimeout(() => fin(null), 100000);
    send({ kind: "qualities", src, rid }, fin);
  }
  btn.addEventListener("click", (e) => {
    e.stopPropagation(); e.preventDefault();
    if (!e.isTrusted) return;                          // sayfanın btn.click()/dispatchEvent'i menü açamaz, kalite isteği gönderemez (A2)
    if ((clipT > 0 || clipB > 0) && typeof e.clientY === "number") {   // kırpılmış (çubuğun altındaki) bölgeye tıklama yok sayılır
      const bb = btn.getBoundingClientRect();
      if (e.clientY < bb.top + clipT || e.clientY > bb.bottom - clipB) return;
    }
    if (menuOpen) { closeMenu(); return; }
    if (target) openMenu();
  }, true);

  if (typeof globalThis.__TD_TEST__ !== "undefined") {
    globalThis.__TD_TEST__.button = {
      qualifies, buttonPos, host, btn, menu, drawOptions, groupOf, newTracker, stepTracker, place, measure, inView, ioSeen, videoUnder, syncTick, loop,
      setTarget: (v) => { target = v; }, setClock: (f) => { clock = f; }, resetTrackers: () => { trackers = new WeakMap(); },
    };
  }
} catch (e) { try { document.documentElement.dataset.tdErr = String(e && e.stack || e); } catch {} } })();
