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
  send({ kind: "cfg" }, (r) => { if (r) enabled = r.showVideoButton !== false; });
  try {
    chrome.runtime.onMessage.addListener((m) => {
      if (m && m.kind === "cfg") { enabled = m.showVideoButton !== false; if (!enabled) hideAll(); }
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

  // Düğmenin görüntü alanındaki konumu: videonun sağ üst köşesi (ekrana sığacak biçimde kırpılır).
  function buttonPos(r, bw, vw, vh) {
    return {
      left: Math.max(4, Math.min(vw - bw - 4, r.left + r.width - bw - 10)),
      top: Math.max(4, Math.min(vh - 30, r.top + 10)),
    };
  }

  // ---------------------------------------------------------------- DOM (Shadow DOM)
  let logo = "";
  try { logo = chrome.runtime.getURL("icons/icon32.png"); } catch { /* yok */ }
  const host = document.createElement("div");
  host.id = "tdownloader-overlay";
  host.style.cssText = "all:initial;position:fixed;z-index:2147483647;left:0;top:0;width:0;height:0;";
  const sh = host.attachShadow({ mode: "open" });
  sh.innerHTML = `<style>
    *{box-sizing:border-box;font:600 13px system-ui,"Segoe UI",Tahoma,sans-serif}
    #btn{position:fixed;display:none;align-items:center;gap:7px;padding:5px 11px 5px 7px;background:#f5c400;color:#111;border:1px solid #111;
      border-radius:5px;cursor:pointer;user-select:none;opacity:.78;transition:opacity .12s,background .12s;pointer-events:auto;white-space:nowrap;
      box-shadow:0 2px 8px #0007}
    #btn img{width:18px;height:18px;display:block}
    #btn:hover,#btn.on{opacity:1;background:#ffd21f}
    #menu{position:fixed;display:none;width:290px;max-height:360px;overflow:auto;background:#141414;color:#f3f3f3;pointer-events:auto;
      border:1px solid #f5c400;border-radius:6px;box-shadow:0 8px 28px #000b;padding:0 0 4px}
    #menu .h{padding:8px 12px 7px;font-weight:700;color:#f5c400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;border-bottom:1px solid #333;background:#0c0c0c}
    #menu .g{padding:8px 12px 3px;font-size:11px;font-weight:700;letter-spacing:.04em;text-transform:uppercase;color:#9a8a3a}
    #menu .row{padding:7px 12px;cursor:pointer;display:flex;justify-content:space-between;gap:10px;font-weight:500}
    #menu .row:hover,#menu .row.def{background:#2a2400}
    #menu .row:hover{background:#f5c400;color:#111}
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
  document.addEventListener("fullscreenchange", () => { if (host.isConnected) mount(); });

  function place() {
    if (!target || !target.isConnected || !qualifies(target)) return hide();
    const r = target.getBoundingClientRect();
    if (r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) { btn.style.display = "none"; if (menuOpen) closeMenu(); return; }
    btn.style.display = "flex";
    const p = buttonPos(r, btn.offsetWidth, innerWidth, innerHeight);
    btn.style.left = p.left + "px"; btn.style.top = p.top + "px";
    if (menuOpen) placeMenu();
  }
  // Görünürken her karede konumu güncelle: kaydırma/animasyonda video ile birebir hareket eder.
  function loop() { raf = 0; if (btn.style.display === "none" && !menuOpen) return; place(); raf = requestAnimationFrame(loop); }
  function startLoop() { if (!raf) raf = requestAnimationFrame(loop); }

  function placeMenu() {
    const b = btn.getBoundingClientRect();
    menu.style.display = "block";
    menu.style.left = Math.max(4, Math.min(innerWidth - 294, b.right - 290)) + "px";
    const below = b.bottom + 6, h = Math.min(menu.scrollHeight, 360);
    menu.style.top = (below + h > innerHeight ? Math.max(4, b.top - h - 6) : below) + "px";
  }
  function hide() { if (menuOpen) return; btn.style.display = "none"; target = null; }
  function hideAll() { closeMenu(); btn.style.display = "none"; target = null; }
  function closeMenu() { menuOpen = false; menu.style.display = "none"; btn.classList.remove("on"); token++; }

  const videoAt = (x, y) => [...document.querySelectorAll("video")].find((v) => {
    const r = v.getBoundingClientRect(); return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
  });
  addEventListener("mousemove", (e) => {
    if (!enabled || menuOpen) return;
    const v = videoAt(e.clientX, e.clientY);
    if (v && qualifies(v)) {
      mount(); target = v; place(); startLoop();
      clearTimeout(hideT); hideT = setTimeout(() => { if (!btn.matches(":hover")) hide(); }, 3500);
    }
  }, { passive: true });
  addEventListener("scroll", () => { if (target && btn.style.display !== "none") place(); }, { passive: true, capture: true });
  addEventListener("resize", () => { if (target) place(); }, { passive: true });
  addEventListener("mousedown", (e) => { if (menuOpen && !e.composedPath().includes(host)) { closeMenu(); hide(); } }, true);
  addEventListener("keydown", (e) => { if (e.key === "Escape" && menuOpen) { closeMenu(); hide(); } }, true);
  // Sekme/çerçeve gizlenince menü kapansın
  document.addEventListener("visibilitychange", () => { if (document.hidden) { closeMenu(); hide(); } });

  // ---------------------------------------------------------------- menü
  const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const mb = (n) => (n > 0 ? (n >= 1073741824 ? (n / 1073741824).toFixed(2) + " GB" : (n / 1048576).toFixed(n < 10485760 ? 1 : 0) + " MB") : "");

  function drawMessage(title, html, cls = "") {
    menu.innerHTML = `<div class="h" title="${esc(title)}">${esc(title)}</div><div class="msg ${cls}">${html}</div>`;
    placeMenu();
  }
  function groupOf(o) { return o.kind === "file" ? "Dosya" : o.audioOnly ? "Sadece ses" : "Video"; }
  function drawOptions(res) {
    let h = `<div class="h" title="${esc(res.title)}">${esc(res.title || "Video")}</div>`;
    let last = "";
    (res.options || []).forEach((o, i) => {
      const g = groupOf(o);
      if (g !== last) { h += `<div class="g">${esc(g)}</div>`; last = g; }
      h += `<div class="row${o.isDefault ? " def" : ""}" data-i="${i}"><span>${esc(o.label)}</span><small>${esc(o.label.includes("MB") ? "" : mb(o.size))}</small></div>`;
    });
    menu.innerHTML = h;
    menu.onclick = (e) => {
      const row = e.target.closest(".row"); if (!row) return;
      const i = +row.dataset.i;
      send({ kind: "qualitySelect", selectionId: res.selectionId, index: i });
      drawMessage(res.title || "Video", "✓ T-Downloader'a gönderildi");
      setTimeout(() => { closeMenu(); hide(); }, 1100);
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
    const fin = (r) => {
      if (done || my !== token) return; done = true;
      if (r && r.ok && r.options && r.options.length) return drawOptions(r);
      const code = r && r.errorCode;
      const msg = !r ? "T-Downloader yanıt vermedi. Uygulama çalışıyor mu?"
        : code === "no_ytdlp" ? "Bu video için yt-dlp gerekiyor (Seçenekler > Video)."
        : (r.error || "Bu video okunamadı.");
      drawMessage(title, `${esc(msg)}<div class="row" id="retry" style="margin:8px -12px -10px"><span>↻ Tekrar dene</span></div>`, "err");
      const rt = sh.getElementById("retry");
      if (rt) rt.onclick = () => { closeMenu(); openMenu(); };
    };
    setTimeout(() => fin(null), 100000);
    send({ kind: "qualities", src }, fin);
  }
  btn.addEventListener("click", (e) => {
    e.stopPropagation(); e.preventDefault();
    if (menuOpen) { closeMenu(); return; }
    if (target) openMenu();
  }, true);

  if (typeof globalThis.__TD_TEST__ !== "undefined") {
    globalThis.__TD_TEST__.button = { qualifies, buttonPos, host, btn, menu, drawOptions, groupOf };
  }
} catch (e) { try { document.documentElement.dataset.tdErr = String(e && e.stack || e); } catch {} } })();
