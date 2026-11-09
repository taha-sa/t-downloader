// Chrome API'leri sahte nesnelerle taklit eden testler. Çalıştır: node tests/run.mjs
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- sözleşme şema doğrulayıcı (CONTRACT.md) ----
const T = { s: "string", n: "number", b: "boolean" };
const SCHEMAS = {
  download: { type: "s", requestId: "s", url: "s", referrer: "s", cookies: "s", userAgent: "s", filename: "s", mime: "s", fileSize: "n", explicit: "b" },
  media: { type: "s", tabId: "n", frameId: "n", pageUrl: "s", pageTitle: "s", frameUrl: "s", append: "b", items: "a" },
  qualities: { type: "s", requestId: "s", tabId: "n", frameId: "n", pageUrl: "s", pageTitle: "s", frameUrl: "s", src: "s", players: "a", cookies: "o", cookieHosts: "o" },
  qualitySelect: { type: "s", selectionId: "s", index: "n" },
  videoGeometry: {
    type: "s", tabId: "n", windowId: "n", visible: "b", fullscreen: "b", rect: "o",
    devicePixelRatio: "n", viewport: "o", browserWindow: "o", pageUrl: "s", pageTitle: "s",
  },
};
function validate(msg) {
  const schema = SCHEMAS[msg.type];
  assert.ok(schema, `bilinmeyen tür ${msg.type}`);
  assert.deepEqual(Object.keys(msg).sort(), Object.keys(schema).sort(), `${msg.type} alan kümesi`);
  for (const [k, t] of Object.entries(schema)) {
    const v = msg[k];
    if (t === "a") assert.ok(Array.isArray(v), `${k} dizi`);
    else if (t === "o") assert.ok(v && typeof v === "object", `${k} nesne`);
    else assert.equal(typeof v, T[t], `${msg.type}.${k} tipi`);
  }
  if (msg.type === "download") assert.equal(msg.type, "download");
  if (msg.type === "media") for (const i of msg.items) {
    assert.deepEqual(Object.keys(i).sort(), ["kind", "mime", "origin", "referer", "size", "url", "userAgent"]);
    assert.ok(["hls", "dash", "file"].includes(i.kind));
  }
  if (msg.type === "videoGeometry") {
    assert.deepEqual(Object.keys(msg.rect).sort(), ["bottom", "left", "right", "top"]);
    assert.deepEqual(Object.keys(msg.viewport).sort(), ["height", "width"]);
    assert.deepEqual(Object.keys(msg.browserWindow).sort(), ["height", "left", "top", "width"]);
  }
}

// ---- sahte chrome ----
const sent = [];            // host'a giden mesajlar
let hostBehavior = "accept"; // accept | reject | silent
const calls = [];
let searchResult = null; // downloads.search sahte yanıtı
let ports = 0;
let lastPort = null;
let hostDelayMs = 0;     // sahte App yanıt gecikmesi
const mkListener = () => { const f = []; return { addListener: (x) => f.push(x), _fire: (...a) => f.forEach((x) => x(...a)) }; };

function makePort() {
  const onMessage = mkListener(), onDisconnect = mkListener();
  ports++;
  return lastPort = {
    onMessage, onDisconnect,
    postMessage(m) {
      sent.push(m);
      if (m.type === "download" && hostBehavior !== "silent") {
        const fire = () => onMessage._fire({ type: "downloadResult", requestId: m.requestId, accepted: hostBehavior === "accept" });
        if (hostDelayMs > 0) setTimeout(fire, hostDelayMs); else queueMicrotask(fire);
      }
      if (m.type === "ping") queueMicrotask(() => onMessage._fire({ type: "pong", version: "1.2.3" }));
    },
  };
}
globalThis.navigator ??= {};
Object.defineProperty(globalThis, "navigator", { value: { userAgent: "TestUA/1.0" }, configurable: true });
const sessionStore = {};
globalThis.chrome = {
  runtime: {
    connectNative: (name) => { assert.equal(name, "com.tdownloader.host"); return makePort(); },
    lastError: undefined,
  },
  storage: {
    local: { get: async () => ({}), set: async () => {} },
    session: { get: async (k) => ({ [k]: sessionStore[k] }), set: async (o) => { Object.assign(sessionStore, structuredClone(o)); } },
  },
  cookies: { getAll: async () => [{ name: "a", value: "1" }, { name: "b", value: "2" }] },
  downloads: {
    search: async (q) => (searchResult ? [{ id: q.id, ...searchResult() }] : []),
    pause: async (id) => calls.push(["pause", id]),
    resume: async (id) => calls.push(["resume", id]),
    cancel: async (id) => calls.push(["cancel", id]),
    erase: async (q) => calls.push(["erase", q.id]),
  },
  tabs: { get: async (id) => ({ id, url: "https://site.test/watch", title: "Video" }), sendMessage: async () => ({ urls: [] }) },
  windows: { get: async () => ({ left: 10, top: 20, width: 1280, height: 720, state: "normal" }) },
};

const url = (p) => pathToFileURL(join(root, p)).href;
const native = await import(url("lib/native.js"));
const capture = await import(url("lib/capture.js"));
const media = await import(url("lib/media.js"));
const geometry = await import(url("lib/geometry.js"));
const proto = await import(url("lib/protocol.js"));
const settings = await import(url("lib/settings.js"));
const { readPlayers } = await import(url("lib/players.js"));
const menus = await import(url("lib/menus.js"));
const qlist = await import(url("lib/qualities.js"));
assert.equal(capture.opts.timeoutMs, 15000, "App soğuk başlangıcı için 15 sn");

capture.nameWait.totalMs = 150; capture.nameWait.stepMs = 30;
let passed = 0;
const test = async (name, fn) => { await fn(); passed++; console.log("ok -", name); };

// ---- protokol ----
await test("classifyMedia", () => {
  assert.equal(proto.classifyMedia("https://a/x.m3u8?t=1"), "hls");
  assert.equal(proto.classifyMedia("https://a/x", "application/dash+xml"), "dash");
  assert.equal(proto.classifyMedia("https://a/x", "video/mp4", 5_000_000), "file");
  assert.equal(proto.classifyMedia("https://a/x", "video/mp4", 100), null);
  assert.equal(proto.classifyMedia("https://a/x.js", "text/javascript"), null);
});
await test("classifyMedia: yolun sonunda olmayan m3u8/mpd", () => {
  assert.equal(proto.classifyMedia("https://a/master.m3u8/index.txt"), "hls");
  assert.equal(proto.classifyMedia("https://a/play?format=m3u8&t=1"), "hls");
  assert.equal(proto.classifyMedia("https://a/x.mpd/seg", ""), "dash");
  assert.equal(proto.classifyMedia("https://a/m3u8-player.js", "text/javascript"), null);
  assert.equal(proto.originOf("https://player.cdn.test/e/abc?x=1"), "https://player.cdn.test");
  assert.equal(proto.originOf("blob:https://a/1"), "");
});
await test("decideCapture", () => {
  const s = { ...proto.DEFAULT_SETTINGS, minFileSizeKb: 100, excludedSites: ["bad.test"] };
  assert.equal(proto.decideCapture(s, { url: "https://x/a.zip", referrer: "", filename: "a.zip", fileSize: 500000 }).capture, true);
  assert.equal(proto.decideCapture(s, { url: "https://x/a.zip", referrer: "", filename: "a.zip", fileSize: 10 }).reason, "small");
  assert.equal(proto.decideCapture(s, { url: "https://cdn.bad.test/a.zip", referrer: "", filename: "a.zip", fileSize: -1 }).reason, "excluded");
  assert.equal(proto.decideCapture(s, { url: "https://x/a.txt", referrer: "", filename: "a.txt", fileSize: -1 }).reason, "type");
  assert.equal(proto.decideCapture({ ...s, interceptEnabled: false }, { url: "https://x/a.zip", referrer: "", filename: "", fileSize: -1 }).reason, "off");
});

// ---- yakalama akışı ----
native.connect();
let nextId = 7;
const item = (o = {}) => ({ id: nextId++, url: "https://dl.test/f/setup.zip", finalUrl: "https://dl.test/f/setup.zip", referrer: "https://dl.test/", filename: "", mime: "application/zip", totalBytes: 2_000_000, ...o });

await test("download kabul -> mesaj şeması, cancel+erase", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "accept";
  await capture.onDownloadCreated(item());
  const m = sent.find((x) => x.type === "download");
  validate(m);
  assert.equal(m.cookies, "a=1; b=2");
  assert.equal(m.userAgent, "TestUA/1.0");
  assert.equal(m.filename, "setup.zip");
  assert.equal(m.fileSize, 2_000_000);
  assert.deepEqual(calls.map((c) => c[0]), ["pause", "cancel", "erase"]);
});
await test("dosya adı boşsa tarayıcı belirleyince alınır (Content-Disposition)", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "accept";
  let n = 0;
  searchResult = () => (++n >= 2 ? { filename: "C:\\Users\\x\\Downloads\\Gercek Ad.zip" } : { filename: "" });
  await capture.onDownloadCreated(item({ url: "https://dl.test/get?id=5", finalUrl: "https://dl.test/get?id=5" }));
  searchResult = null;
  const m = sent.find((x) => x.type === "download");
  assert.equal(m.filename, "Gercek Ad.zip");
});
await test("download red -> resume", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "reject";
  await capture.onDownloadCreated(item());
  assert.deepEqual(calls.map((c) => c[0]), ["pause", "resume"]);
});
await test("zaman aşımı -> resume", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "silent"; capture.opts.timeoutMs = 80;
  await capture.onDownloadCreated(item());
  assert.deepEqual(calls.map((c) => c[0]), ["pause", "resume"]);
});
await test("zaman aşımından sonra gelen accepted:true tarayıcı kopyasını iptal eder (çift indirme yok)", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "silent"; capture.opts.timeoutMs = 60;
  const it = item();
  await capture.onDownloadCreated(it);
  assert.deepEqual(calls.map((c) => c[0]), ["pause", "resume"]);
  const dl = sent.find((x) => x.type === "download");
  assert.equal(capture.watchedCount() >= 1, true);
  calls.length = 0;
  lastPort.onMessage._fire({ type: "downloadResult", requestId: dl.requestId, accepted: true });
  await sleep(30);
  assert.deepEqual(calls.map((c) => c[0]), ["cancel", "erase"]);
  assert.equal(calls[0][1], it.id);
  calls.length = 0;
  lastPort.onMessage._fire({ type: "downloadResult", requestId: dl.requestId, accepted: true });   // ikinci kez: etkisiz
  await sleep(30);
  assert.equal(calls.length, 0);
});
await test("servis çalışanı yeniden başlasa da geç accepted:true tarayıcı kopyasını iptal eder (lateWatch kalıcı)", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "silent"; capture.opts.timeoutMs = 60;
  const it = item({ url: "https://dl.test/restart.zip", finalUrl: "https://dl.test/restart.zip" });
  await capture.onDownloadCreated(it);
  const dl = sent.find((x) => x.type === "download");
  await sleep(10);
  assert.ok(sessionStore.lateWatch.some(([k]) => k === dl.requestId));   // depoya yazıldı
  await capture.restoreLateWatch();                                       // yeniden başlatma: bellek sıfırlanır, depodan yüklenir
  calls.length = 0;
  lastPort.onMessage._fire({ type: "downloadResult", requestId: dl.requestId, accepted: true });
  await sleep(30);
  assert.deepEqual(calls.map((c) => c[0]), ["cancel", "erase"]);
  assert.equal(calls[0][1], it.id);
  assert.ok(!sessionStore.lateWatch.some(([k]) => k === dl.requestId));
});
await test("onay penceresi uzun sürse de (App hemen accepted:true) indirme bir kez iptal edilir; 50 eşzamanlı istek", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "accept"; capture.opts.timeoutMs = 15000; hostDelayMs = 40;
  const items = Array.from({ length: 50 }, (_, i) => item({ url: `https://dl.test/f/${i}.zip`, finalUrl: `https://dl.test/f/${i}.zip` }));
  const t0 = Date.now();
  await Promise.all(items.map((it) => capture.onDownloadCreated(it)));
  hostDelayMs = 0;
  assert.ok(Date.now() - t0 < 5000);
  assert.equal(sent.filter((x) => x.type === "download").length, 50);
  assert.equal(calls.filter((c) => c[0] === "cancel").length, 50);
  assert.equal(calls.filter((c) => c[0] === "resume").length, 0);        // hiçbiri tarayıcıya geri bırakılmadı
});
await test("sağ tık 'tüm linkleri indir': seri bekleme yok (toplu paralel), explicit=true", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "accept"; hostDelayMs = 50;
  const urls = Array.from({ length: 30 }, (_, i) => `https://dl.test/all/${i}.zip`);
  chrome.tabs.sendMessage = async () => ({ urls });
  const t0 = Date.now();
  await menus.onMenuClick({ menuItemId: "td-page", pageUrl: "https://dl.test/" }, { id: 1, url: "https://dl.test/" });
  const dt = Date.now() - t0;
  hostDelayMs = 0; chrome.tabs.sendMessage = async () => ({ urls: [] });
  const dls = sent.filter((x) => x.type === "download");
  assert.equal(dls.length, 30);
  assert.ok(dls.every((m) => m.explicit === true));
  assert.ok(dt < 700, `seri olurdu (~1500ms), ölçülen ${dt}ms`);
});
await test("geçmişten gelen / tekrarlanan indirme -> dokunulmaz", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "accept";
  const old = new Date(Date.now() - 3600_000).toISOString();
  await capture.onDownloadCreated(item({ state: "complete", startTime: old }));
  await capture.onDownloadCreated(item({ state: "in_progress", startTime: old }));
  await capture.onDownloadCreated(item({ state: "interrupted" }));
  assert.equal(sent.filter((x) => x.type === "download").length, 0);
  assert.equal(calls.length, 0);
  const fresh = item({ state: "in_progress", startTime: new Date().toISOString() });
  await capture.onDownloadCreated(fresh);
  await capture.onDownloadCreated(fresh);               // aynı id ikinci kez
  assert.equal(sent.filter((x) => x.type === "download").length, 1);
});
await test("blob:/data: ve Alt -> dokunulmaz", async () => {
  sent.length = 0; calls.length = 0; hostBehavior = "accept";
  await capture.onDownloadCreated(item({ url: "blob:https://x/1", finalUrl: "blob:https://x/1" }));
  await capture.onDownloadCreated(item({ url: "data:text/plain,a", finalUrl: "data:text/plain,a" }));
  capture.setAlt(5000);
  await capture.onDownloadCreated(item());
  capture.setAlt(0);
  assert.equal(calls.length, 0);
  assert.equal(sent.length, 0);
});
await test("yakalama kapalıyken dokunulmaz", async () => {
  calls.length = 0; settings.setUserCapture(false);
  await capture.onDownloadCreated(item());
  settings.setUserCapture(true);
  assert.equal(calls.length, 0);
});
await test("App settings uygulanır", async () => {
  settings.applyAppSettings({ type: "settings", interceptEnabled: true, minFileSizeKb: 5000, excludedSites: [], fileTypes: ["zip"] });
  calls.length = 0; hostBehavior = "accept"; capture.opts.timeoutMs = 5000;
  await capture.onDownloadCreated(item()); // 2 MB < 5000 KB
  assert.deepEqual(calls.map((c) => c[0]), ["pause", "resume"]);
  settings.applyAppSettings({ type: "settings", minFileSizeKb: 0 });
});

// ---- medya ----
await test("media mesajı şeması", async () => {
  sent.length = 0; media.opts.debounceMs = 20;
  const h = (n, v) => ({ name: n, value: v });
  media.onResponse({ tabId: 3, frameId: 0, url: "https://v.test/master.m3u8", statusCode: 200, responseHeaders: [h("Content-Type", "application/vnd.apple.mpegurl")] });
  media.onResponse({ tabId: 3, frameId: 0, url: "https://v.test/clip.mp4", statusCode: 200, responseHeaders: [h("Content-Type", "video/mp4"), h("Content-Length", "9000000")] });
  media.onResponse({ tabId: 3, frameId: 0, url: "https://v.test/app.js", statusCode: 200, responseHeaders: [h("Content-Type", "text/javascript")] });
  await sleep(100);
  const m = sent.find((x) => x.type === "media");
  validate(m);
  assert.deepEqual(m.items.map((i) => i.kind), ["hls", "file"]);
  assert.equal(m.tabId, 3);
});

await test("iframe oynatıcı: Referer/Origin/çerez ve frameUrl akışla gider", async () => {
  sent.length = 0;
  const h = (n, v) => ({ name: n, value: v });
  // tarayıcı m3u8 isteğini iframe sayfasından yaptı
  media.onSendHeaders({ requestId: "r1", tabId: 4, requestHeaders: [h("Referer", "https://player.cdn.test/"), h("Origin", "https://player.cdn.test")] });
  media.onResponse({ requestId: "r1", tabId: 4, frameId: 7, documentUrl: "https://player.cdn.test/embed/abc", initiator: "https://player.cdn.test",
    url: "https://stream.cdn.test/hls/master.m3u8?tok=1", statusCode: 200, responseHeaders: [h("Content-Type", "application/x-mpegURL")] });
  // başlık kaydı yoksa belge adresine düşer
  media.onResponse({ requestId: "r2", tabId: 4, frameId: 7, documentUrl: "https://player.cdn.test/embed/abc", initiator: "https://player.cdn.test",
    url: "https://stream.cdn.test/hls/v/720.m3u8", statusCode: 200, responseHeaders: [h("Content-Type", "application/vnd.apple.mpegurl")] });
  await sleep(120);
  const m = sent.find((x) => x.type === "media" && x.tabId === 4);
  validate(m);
  assert.equal(m.frameId, 7);
  assert.equal(m.frameUrl, "https://player.cdn.test/embed/abc");
  assert.equal(m.pageUrl, "https://site.test/watch");           // üst sayfa
  const [a, b] = m.items;
  assert.equal(a.referer, "https://player.cdn.test/");
  assert.equal(a.origin, "https://player.cdn.test");
  assert.equal(a.cookies, undefined, "çerez medya mesajıyla gitmez");
  assert.equal(a.userAgent, "TestUA/1.0");
  assert.equal(b.referer, "https://player.cdn.test/embed/abc");
  assert.equal(b.origin, "https://player.cdn.test");
});

// ---- geometri (background) ----
await test("yerel düğme kapalıyken (varsayılan) videoGeometry gönderilmez", async () => {
  sent.length = 0;
  assert.equal(settings.uiConfig().nativeButton, false);
  await geometry.onContentGeometry(
    { visible: true, area: 5000, rect: { left: 1, top: 2, right: 650, bottom: 380 }, viewport: { width: 800, height: 600 } },
    { tab: { id: 31, windowId: 9, url: "https://site.test/", title: "x" }, frameId: 0 });
  assert.equal(sent.filter((x) => x.type === "videoGeometry").length, 0);
  geometry.resetTab(31);
});
await test("qualities / qualitySelect mesaj şeması", () => {
  validate(proto.buildQualities({ requestId: "q1", tabId: 3, frameId: 2, pageUrl: "https://a/", pageTitle: "T", frameUrl: "https://p/e", src: "" }));
  validate(proto.buildQualitySelect({ selectionId: "abc", index: 2 }));
  assert.equal(proto.buildQualitySelect({ selectionId: "abc", index: "x" }).index, -1);
});
await test("App settings: showVideoButton / nativeButton uygulanır", () => {
  settings.applyAppSettings({ type: "settings", showVideoButton: false, nativeButton: true });
  assert.deepEqual(settings.uiConfig(), { showVideoButton: false, nativeButton: true });
  settings.applyAppSettings({ type: "settings", showVideoButton: true });
  assert.equal(settings.uiConfig().showVideoButton, true);
});
await test("videoGeometry şeması (background zenginleştirme)", async () => {
  sent.length = 0;
  await geometry.onContentGeometry(
    { visible: true, fullscreen: false, area: 5000, rect: { left: 10, top: 20, right: 650, bottom: 380 }, devicePixelRatio: 1.5, viewport: { width: 1200, height: 700 }, pageUrl: "https://site.test/watch", pageTitle: "Video" },
    { tab: { id: 3, windowId: 9, url: "https://site.test/watch", title: "Video" }, frameId: 0 },
  );
  const g = sent.find((x) => x.type === "videoGeometry");
  validate(g);
  assert.equal(g.windowId, 9);
  assert.deepEqual(g.browserWindow, { left: 10, top: 20, width: 1280, height: 720 });
  assert.equal(g.devicePixelRatio, 1.5);
});
await test("hideButton sonrası geometri gitmez", async () => {
  sent.length = 0; geometry.hideTab(3);
  await geometry.onContentGeometry({ visible: true, area: 1, rect: { left: 0, top: 0, right: 1, bottom: 1 }, viewport: {} }, { tab: { id: 3, windowId: 9 }, frameId: 0 });
  assert.equal(sent.length, 0);
  geometry.resetTab(3);
});
await test("resendWindow sonsuz döngüye girmez (tek mesaj)", async () => {
  geometry.resetTab(3); sent.length = 0;
  await geometry.onContentGeometry({ visible: true, area: 9, rect: { left: 0, top: 0, right: 300, bottom: 200 }, viewport: { width: 800, height: 600 } },
    { tab: { id: 3, windowId: 9, url: "https://site.test/watch", title: "V" }, frameId: 0 });
  const before = sent.length;
  await Promise.race([geometry.resendWindow(9), sleep(1500).then(() => { throw new Error("resendWindow takıldı"); })]);
  assert.equal(sent.length - before, 1);
  geometry.resetTab(3);
  settings.applyAppSettings({ type: "settings", nativeButton: false });
});

// ---- content script (vm, sahte DOM) ----
await test("content.js payload: en büyük görünür video", () => {
  const mkEl = (l, t, w, h, hiddenStyle = false) => ({
    getBoundingClientRect: () => ({ left: l, top: t, right: l + w, bottom: t + h, width: w, height: h }),
    _hidden: hiddenStyle,
  });
  const els = [mkEl(0, 0, 200, 100), mkEl(100.4, 50.6, 640, 360), mkEl(0, 0, 900, 900, true)];
  const doc = { querySelectorAll: () => els, fullscreenElement: null, title: "T", documentElement: null, addEventListener() {}, hidden: false };
  const win = {
    innerWidth: 1280, innerHeight: 720, devicePixelRatio: 2, location: { href: "https://x.test/" },
    getComputedStyle: (el) => ({ display: el._hidden ? "none" : "block", visibility: "visible", opacity: "1" }),
    addEventListener() {}, getSelection: () => null,
  };
  const sandbox = {
    window: win, document: doc, __TD_TEST__: {}, setTimeout, setInterval: () => 0, Date, JSON, Math, parseFloat,
    MutationObserver: class { observe() {} },
    chrome: { runtime: { sendMessage: () => Promise.resolve(), onMessage: mkListener() } },
  };
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(readFileSync(join(root, "content.js"), "utf8"), sandbox);
  const p = JSON.parse(JSON.stringify(sandbox.__TD_TEST__.exports.buildPayload(doc, win)));
  assert.equal(p.visible, true);
  assert.deepEqual(p.rect, { left: 100, top: 51, right: 740, bottom: 411 });
  assert.equal(p.devicePixelRatio, 2);
  assert.deepEqual(p.viewport, { width: 1280, height: 720 });
  const none = sandbox.__TD_TEST__.exports.buildPayload({ ...doc, querySelectorAll: () => [] }, win);
  assert.equal(none.visible, false);
});

// ---- content script: iframe içindeki video üst çerçeve koordinatlarına çevrilir ----
function loadContent(winExtra, docExtra, els = []) {
  const doc = { querySelectorAll: (sel) => (/iframe/.test(sel) ? (docExtra.frames || []) : els), fullscreenElement: null, title: "T", documentElement: null, addEventListener() {}, hidden: false };
  const win = {
    innerWidth: 1280, innerHeight: 720, devicePixelRatio: 1, location: { href: "https://film.test/izle" },
    getComputedStyle: (el) => ({ display: "block", visibility: "visible", opacity: "1", borderLeftWidth: el._b || "0px", borderTopWidth: el._b || "0px", borderRightWidth: el._b || "0px", borderBottomWidth: el._b || "0px", paddingLeft: "0px", paddingTop: "0px", paddingRight: "0px", paddingBottom: "0px" }),
    addEventListener() {}, getSelection: () => null, ...winExtra,
  };
  win.top = win;
  const sandbox = { window: win, document: doc, __TD_TEST__: {}, setTimeout, setInterval: () => 0, Date, JSON, Math, parseFloat,
    MutationObserver: class { observe() {} }, chrome: { runtime: { sendMessage: () => Promise.resolve(), onMessage: mkListener() } } };
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(readFileSync(join(root, "content.js"), "utf8"), sandbox);
  return { doc, win, ex: sandbox.__TD_TEST__.exports };
}
await test("content.js: iframe içi video üst sayfa koordinatına çevrilir", () => {
  const childWin = {};
  const frame = { contentWindow: childWin, _b: "2px", getBoundingClientRect: () => ({ left: 100, top: 200, right: 740, bottom: 560, width: 640, height: 360 }) };
  const { doc, win, ex } = loadContent({}, { frames: [frame] });
  // alt çerçeve: 636x356 görünüm, video tüm görünümü kaplıyor
  const accepted = ex.onChildMessage({ source: childWin, data: { __tdGeo: 1, payload: {
    visible: true, fullscreen: false, area: 636 * 356, rect: { left: 0, top: 0, right: 636, bottom: 356 }, viewport: { width: 636, height: 356 } } } }, doc, win);
  assert.equal(accepted, true);
  const p = JSON.parse(JSON.stringify(ex.buildPayload(doc, win)));
  assert.equal(p.visible, true);
  assert.deepEqual(p.rect, { left: 102, top: 202, right: 738, bottom: 558 });
  assert.equal(p.pageUrl, "https://film.test/izle");
  // iframe DOM'dan çıkarsa video kaybolur
  doc.querySelectorAll = () => [];
  assert.equal(ex.buildPayload(doc, win).visible, false);
});
await test("content.js: sahte/bozuk postMessage yok sayılır", () => {
  const { doc, win, ex } = loadContent({}, { frames: [] });
  assert.equal(ex.onChildMessage({ source: {}, data: "merhaba" }, doc, win), false);
  assert.equal(ex.onChildMessage({ source: {}, data: { __tdGeo: 1, payload: 5 } }, doc, win), false);
  assert.equal(ex.onChildMessage({ source: null, data: { __tdGeo: 1, payload: { rect: {} } } }, doc, win), false);
});

// ---- sayfa içi düğme (button.js): yardımcılar ----
await test("button.js: video seçimi ve düğme konumu", () => {
  const stub = () => new Proxy(function () {}, { get: (t, k) => (k === "style" ? {} : k === "classList" ? { add() {}, remove() {} } : stub()), apply: () => stub(), set: () => true });
  const doc = { createElement: () => ({ style: {}, attachShadow: () => ({ set innerHTML(v) {}, getElementById: () => stub() }), addEventListener() {} }), addEventListener() {}, documentElement: null };
  const win = { innerWidth: 1000, innerHeight: 600, top: null, getComputedStyle: (v) => ({ display: v._none ? "none" : "block", visibility: "visible", opacity: "1" }), addEventListener() {} };
  win.top = win;
  const sandbox = { window: win, document: doc, __TD_TEST__: {}, parseFloat, Math, setTimeout, clearTimeout, requestAnimationFrame: () => 0, addEventListener() {}, innerWidth: 1000, innerHeight: 600,
    chrome: { runtime: { sendMessage() {}, onMessage: mkListener(), getURL: (p) => "chrome-extension://x/" + p } } };
  sandbox.globalThis = sandbox; vm.createContext(sandbox);
  vm.runInContext(readFileSync(join(root, "button.js"), "utf8"), sandbox);
  const b = sandbox.__TD_TEST__.button;
  const vid = (w, h, o = {}) => ({ tagName: "VIDEO", closest: () => null, getBoundingClientRect: () => ({ left: 0, top: 0, width: w, height: h, right: w, bottom: h }), ...o });
  assert.equal(b.qualifies(vid(640, 360), win), true);
  assert.equal(b.qualifies(vid(150, 100), win), false);                 // küçük
  assert.equal(b.qualifies(vid(900, 100), win), false);                 // garip oran
  assert.equal(b.qualifies(vid(640, 360, { _none: true }), win), false); // görünmez
  assert.equal(b.qualifies(vid(640, 360, { closest: () => ({}) }), win), false); // önizleme kartı
  const R = (left, top, w, h) => ({ left, top, width: w, height: h, right: left + w, bottom: top + h });
  const p = b.buttonPos(R(100, 50, 640, 360), 160, 30, 1000, 600);
  assert.deepEqual(JSON.parse(JSON.stringify(p)), { left: 572, top: 58, visible: true });           // videonun İÇİNDE sağ üst (10 px)
  // clamp YOK: video yukarı kayınca düğme de aynı miktarda kayar, viewport'a yapışmaz
  const up = b.buttonPos(R(100, -100, 640, 360), 160, 30, 1000, 600);
  assert.equal(up.top, -92); assert.equal(up.left, 572);
  assert.equal(b.buttonPos(R(100, -500, 640, 360), 160, 30, 1000, 600).visible, false);              // video tamamen ekran dışı
  assert.equal(b.buttonPos(R(100, 700, 640, 360), 160, 30, 1000, 600).visible, false);               // altta
  assert.equal(b.buttonPos(R(100, -345, 640, 360), 160, 30, 1000, 600).visible, false);             // video görünür ama düğme bölgesi üst kenarın dışında
  assert.equal(b.groupOf({ kind: "file" }), "Dosya");
  assert.equal(b.groupOf({ audioOnly: true }), "Sadece ses");
});

// ---- önceden alma YOK: kalite isteği yalnızca düğme tıklamasıyla gider ----
await test("önceden alma yok: media mesajı ve düğme yüklemesi qualities göndermez", async () => {
  sent.length = 0; media.opts.debounceMs = 20;
  media.onResponse({ tabId: 8, frameId: 0, url: "https://v.test/master2.m3u8", statusCode: 200, responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }] });
  await new Promise((r) => setTimeout(r, 120));
  assert.ok(sent.some((x) => x.type === "media"));
  assert.equal(sent.filter((x) => x.type === "qualities").length, 0);
  const stub = () => new Proxy(function () {}, { get: (t, k) => (k === "style" ? {} : k === "classList" ? { add() {}, remove() {} } : stub()), apply: () => stub(), set: () => true });
  const doc = { createElement: () => ({ style: {}, attachShadow: () => ({ set innerHTML(v) {}, getElementById: () => stub() }), addEventListener() {} }), addEventListener() {}, documentElement: null };
  const win = { innerWidth: 1000, innerHeight: 600, top: null, getComputedStyle: () => ({ display: "block", visibility: "visible", opacity: "1" }), addEventListener() {} };
  win.top = win;
  const msgs = [];
  const sandbox = { window: win, document: doc, __TD_TEST__: {}, parseFloat, Math, setTimeout, clearTimeout, requestAnimationFrame: () => 0, addEventListener() {}, innerWidth: 1000, innerHeight: 600,
    chrome: { runtime: { sendMessage(m) { msgs.push(m); }, onMessage: mkListener(), getURL: (p) => "chrome-extension://x/" + p } } };
  sandbox.globalThis = sandbox; vm.createContext(sandbox);
  const src = readFileSync(join(root, "button.js"), "utf8");
  vm.runInContext(src, sandbox);
  await new Promise((r) => setTimeout(r, 50));
  assert.equal(msgs.filter((m) => m && m.kind === "qualities").length, 0);
  // kaynakta tek gönderim noktası var: openMenu içinde; o da yalnızca düğme click işleyicisi + "Tekrar dene" çağırır
  assert.equal((src.match(/kind:\s*"qualities"/g) || []).length, 1);
  assert.equal((src.match(/openMenu\(\)/g) || []).length, 3);   // tanım + click + "Tekrar dene"
});

// ---- düğme: kaydırma / kararlılık / fade-in (sahte DOM + sahte zaman) ----
function loadButton(ov = {}) {
  const mkEl = () => {
    const cls = new Set();
    return { style: {}, classList: { add: (c) => cls.add(c), remove: (c) => cls.delete(c), has: (c) => cls.has(c), contains: (c) => cls.has(c) }, offsetWidth: 160, offsetHeight: 22,
      ls: {}, addEventListener(t, f) { (this.ls[t] ??= []).push(f); }, getBoundingClientRect: () => ({ left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 }), matches: () => false, set innerHTML(v) {}, scrollHeight: 100 };
  };
  const els = {}, docLs = {};
  const doc = {
    createElement: () => { const e = mkEl(); e.attachShadow = () => ({ set innerHTML(v) {}, getElementById: (id) => (els[id] ??= mkEl()) }); return e; },
    ls: docLs, addEventListener(t, f) { (docLs[t] ??= []).push(f); }, documentElement: null, elementFromPoint: () => null, hidden: false, querySelectorAll: () => [],
  };
  const win = { innerWidth: 1000, innerHeight: 600, getComputedStyle: () => ({ display: "block", visibility: "visible", opacity: "1", position: "static" }), addEventListener() {} };
  win.top = win;
  const sandbox = { window: win, document: doc, __TD_TEST__: {}, parseFloat, Math, setTimeout, clearTimeout, requestAnimationFrame: () => 0, addEventListener() {}, innerWidth: 1000, innerHeight: 600,
    chrome: { runtime: { sendMessage() {}, onMessage: mkListener(), getURL: (p) => "chrome-extension://x/" + p } } };
  sandbox.globalThis = sandbox; vm.createContext(sandbox);
  Object.assign(doc, ov.doc || {}); Object.assign(sandbox, ov.sandbox || {});
  vm.runInContext(readFileSync(join(root, "button.js"), "utf8"), sandbox);
  return { b: sandbox.__TD_TEST__.button, sandbox, els, doc };
}
const fakeVideo = (rect) => ({ tagName: "VIDEO", isConnected: true, closest: () => null, rect, getBoundingClientRect() { const r = this.rect; return { left: r.l, top: r.t, width: r.w, height: r.h, right: r.l + r.w, bottom: r.t + r.h }; } });

await test("düğme: kaydırmada videoya göre sabit ofset, clamp yok, video ekran dışına çıkınca gizlenir", () => {
  const { b, sandbox, els } = loadButton();
  let now = 0; b.setClock(() => now);
  const v = fakeVideo({ l: 100, t: 200, w: 640, h: 360 });
  b.setTarget(v);
  b.place(); now = 500; b.place();                       // kararlı -> görünür
  const btn = els.btn;
  assert.equal(btn.style.display, "flex");
  assert.ok(btn.classList.has("vis"));
  const off0 = [parseFloat(btn.style.left) - v.rect.l, parseFloat(btn.style.top) - v.rect.t];
  assert.deepEqual(off0, [640 - 160 - 8, 8]);          // video içinde, sağ üst
  for (const top of [150, 90, 30, -20, -200]) {          // sayfa aşağı kayar: video yukarı çıkar (scrollY artar)
    sandbox.scrollY = 200 - top; v.rect.t = top; now += 16; b.place();
    if (top === -200) break;
    assert.equal(btn.style.display, "flex", `top=${top} görünür kalmalı`);
    assert.deepEqual([parseFloat(btn.style.left) - v.rect.l, parseFloat(btn.style.top) - v.rect.t], off0, `top=${top} ofset sabit`);
  }
  assert.equal(btn.style.display, "none");               // top=-200: düğme bölgesi ekran dışı (-190+30 < 0)
  assert.ok(!btn.classList.has("vis"));
  v.rect.t = -400; now += 16; b.place();
  assert.equal(btn.style.display, "none");               // video tamamen çıktı
  sandbox.scrollY = 0; v.rect.t = 120; now += 16; b.place();
  assert.equal(btn.style.display, "flex");               // geri gelince (aynı boyut) anında görünür
});

await test("düğme: kararsız rect'te görünmez, 400 ms kararlılıktan sonra fade-in ile görünür", () => {
  const { b, els } = loadButton();
  let now = 0; b.setClock(() => now);
  const v = fakeVideo({ l: 0, t: 0, w: 300, h: 200 });
  b.setTarget(v);
  const btn = els.btn, step = (dt) => { now += dt; b.place(); return btn.style.display === "flex"; };
  assert.equal(step(0), false);
  v.rect = { l: 50, t: 10, w: 500, h: 300 }; assert.equal(step(100), false);   // oynatıcı yükleniyor: boyut/konum değişiyor
  v.rect = { l: 10, t: 10, w: 800, h: 450 }; assert.equal(step(100), false);
  v.rect = { l: 200, t: 60, w: 640, h: 360 }; assert.equal(step(100), false);  // son değişim
  assert.equal(step(300), false);                         // 300 ms sabit: henüz yok
  assert.equal(step(120), true);                          // 420 ms sabit -> görünür
  assert.ok(btn.classList.has("vis"));                    // opaklık CSS geçişiyle (sınıf), konumda geçiş yok
  assert.equal(parseFloat(btn.style.left), 200 + 640 - 160 - 8);
  // yeterince büyük değil: asla görünmez
  const { b: b2, els: e2 } = loadButton(); let n2 = 0; b2.setClock(() => n2);
  b2.setTarget(fakeVideo({ l: 0, t: 0, w: 190, h: 150 })); b2.place(); n2 = 2000; b2.place();
  assert.notEqual(e2.btn.style.display, "flex");
});

await test("düğme: görünürken büyük boyut değişimi (tam ekran/sinema) yeniden kararlılık bekler; küçük/konum değişimi anında", () => {
  const { b, els } = loadButton();
  let now = 0; b.setClock(() => now);
  const v = fakeVideo({ l: 100, t: 100, w: 640, h: 360 });
  b.setTarget(v); b.place(); now = 500; b.place();
  assert.equal(els.btn.style.display, "flex");
  v.rect = { l: 100, t: 100, w: 650, h: 366 }; now += 16; b.place();
  assert.equal(els.btn.style.display, "flex");            // %2 değişim: takip
  v.rect = { l: 0, t: 0, w: 1000, h: 560 }; now += 16; b.place();
  assert.equal(els.btn.style.display, "none");            // sinema modu: gizle, bekle
  now += 200; b.place(); assert.equal(els.btn.style.display, "none");
  now += 250; b.place(); assert.equal(els.btn.style.display, "flex");
  assert.equal(parseFloat(els.btn.style.left), 1000 - 160 - 8);
});

await test("stepTracker: kaydırma (sayfa koordinatı sabit) kararlılığı bozmaz", () => {
  const { b } = loadButton();
  const t = b.newTracker();
  const at = (top, sy, now) => b.stepTracker(t, { left: 10, top, width: 640, height: 360 }, 0, sy, now);
  assert.equal(at(100, 0, 0), false);
  assert.equal(at(60, 40, 200), false);
  assert.equal(at(20, 80, 450), true);                    // viewport'ta kaydı ama sayfada sabit: 450 ms kararlı
});

// ---- CPU: geometri altyapısı yalnızca nativeButton=true iken; button.js keşfi IntersectionObserver ile ----
await test("content.js: nativeButton=false iken geometri altyapısı HİÇ kurulmaz; açılınca kurulur, kapanınca sökülür", () => {
  const added = [], removed = []; let observers = 0, disconnects = 0, intervals = 0, cleared = 0;
  const doc = { querySelectorAll: () => [], fullscreenElement: null, title: "T", documentElement: {}, hidden: false,
    addEventListener: (t) => added.push("d:" + t), removeEventListener: (t) => removed.push("d:" + t) };
  const win = { innerWidth: 1280, innerHeight: 720, devicePixelRatio: 1, location: { href: "https://x.test/" },
    getComputedStyle: () => ({ display: "block", visibility: "visible", opacity: "1" }), getSelection: () => null,
    addEventListener: (t) => added.push("w:" + t), removeEventListener: (t) => removed.push("w:" + t) };
  win.top = win;
  const om = mkListener();
  const sandbox = { window: win, document: doc, __TD_TEST__: {}, setTimeout, clearTimeout, Date, JSON, Math, parseFloat,
    setInterval: () => { intervals++; return 7; }, clearInterval: () => { cleared++; },
    MutationObserver: class { constructor() { observers++; } observe() {} disconnect() { disconnects++; } },
    chrome: { runtime: { sendMessage: (m, cb) => { if (typeof cb === "function") cb({ nativeButton: false }); return Promise.resolve(); }, onMessage: om } } };
  sandbox.globalThis = sandbox; vm.createContext(sandbox);
  vm.runInContext(readFileSync(join(root, "content.js"), "utf8"), sandbox);
  const heavy = ["w:scroll", "w:resize", "w:mousemove", "w:message", "d:fullscreenchange", "d:play", "d:loadedmetadata"];
  assert.deepEqual(heavy.filter((x) => added.includes(x)), [], "geometri dinleyicileri kurulmamalı");
  assert.ok(added.includes("w:keydown"), "Alt tuşu izleme her zaman açık");
  assert.equal(observers, 0); assert.equal(intervals, 0);
  om._fire({ kind: "cfg", nativeButton: true });
  assert.deepEqual(heavy.filter((x) => !added.includes(x)), [], "açılınca kurulmalı");
  assert.equal(observers, 1); assert.equal(intervals, 1);
  om._fire({ kind: "cfg", nativeButton: true });                       // yinelenen ayar: ikinci kurulum yok
  assert.equal(observers, 1); assert.equal(intervals, 1);
  om._fire({ kind: "cfg", nativeButton: false });
  assert.deepEqual(heavy.filter((x) => !removed.includes(x)), [], "kapanınca sökülmeli");
  assert.equal(disconnects, 1); assert.equal(cleared, 1);
});
await test("button.js: video keşfi IntersectionObserver ile; mousemove tüm <video>ları taramaz; zamanlayıcı yalnızca görünür video varken; rAF görünmeyen videoda durur", () => {
  let ioCb = null; const observed = [];
  let qs = 0, rafs = 0, nextId = 1; const timers = new Map(); const handlers = {};
  const video = fakeVideo({ l: 100, t: 100, w: 640, h: 360 });
  const { b, sandbox } = loadButton({
    doc: { querySelectorAll: () => { qs++; return [video]; }, elementsFromPoint: () => [{ tagName: "DIV" }, video], appendChild() {} },
    sandbox: {
      IntersectionObserver: class { constructor(cb) { ioCb = cb; } observe(v) { observed.push(v); } },
      setInterval: (f, ms) => { const id = nextId++; timers.set(id, ms); return id; }, clearInterval: (id) => timers.delete(id),
      requestAnimationFrame: () => { rafs++; return rafs; },
      addEventListener: (t, fn) => { handlers[t] = fn; },
    },
  });
  assert.deepEqual(observed, [video]);                                  // başlangıç keşfi
  assert.deepEqual([...timers.values()], [4000]);                       // yalnızca seyrek güvenlik ağı; 250 ms zamanlayıcı yok
  const q0 = qs;
  ioCb([{ target: video, isIntersecting: true }]);
  assert.deepEqual([...timers.values()].sort((x, y) => x - y), [250, 4000]);
  for (let i = 0; i < 20; i++) handlers.mousemove({ clientX: 200, clientY: 200 });
  assert.equal(qs, q0, "mousemove DOM taraması yapmamalı");
  assert.equal(b.videoUnder(200, 200), video);
  // video görünür alandan çıktı: zamanlayıcı durur ve rAF döngüsü kendini bitirir
  b.setTarget(video);
  ioCb([{ target: video, isIntersecting: false }]);
  assert.deepEqual([...timers.values()], [4000]);
  rafs = 0; b.loop();
  assert.equal(rafs, 0, "görünmeyen videoda rAF yeniden planlanmamalı");
  ioCb([{ target: video, isIntersecting: true }]);
  rafs = 0; b.setTarget(video); b.loop();   // görünür olan video hedef olup döngüyü kendisi başlatır; sayaç sıfırlanır
  assert.equal(rafs, 1);                                              // görünürken döngü sürer
});

// ---- oynatıcı yapılandırması ayrıştırma ----
const mkDoc = ({ scripts = [], sources = [], base = "https://film.test/izle/1" } = {}) => ({
  baseURI: base,
  querySelectorAll: (sel) => (sel.startsWith("script") ? scripts.map((t) => ({ textContent: t })) : sources.map((s) => ({ getAttribute: (n) => s[n] ?? null, currentSrc: s.src }))),
});
await test("players: Playerjs [1080p]url,[720p]url dizesi", () => {
  const ex = {};
  readPlayers({}, mkDoc(), ex);
  const r = ex.parseList("[1080p]https://c.test/a/1080/index.m3u8,[720p]https://c.test/a/720/index.m3u8,[480p]//c.test/a/480/index.m3u8 or https://b.test/x.m3u8,[Auto]https://c.test/m.m3u8");
  assert.deepEqual(r.map((x) => [x.label, x.height, x.url]), [
    ["1080p", 1080, "https://c.test/a/1080/index.m3u8"], ["720p", 720, "https://c.test/a/720/index.m3u8"],
    ["480p", 480, "https://c.test/a/480/index.m3u8"], ["Auto", 0, "https://c.test/m.m3u8"]]);
  assert.equal(ex.heightOfLabel("1920x1080"), 1080);
  assert.equal(ex.heightOfLabel("4K"), 2160);
});
await test("players: satır içi script (Playerjs file, sources[], çıplak m3u8)", () => {
  const doc = mkDoc({ scripts: [
    'var p = new Playerjs({id:"player", file:"[1080p]https:\\/\\/c.test\\/v\\/1080.m3u8,[720p]https:\\/\\/c.test\\/v\\/720.m3u8"});',
    'jwplayer("x").setup({sources:[{file:"https://d.test/h.mp4",label:"360p"},{"file":"https://d.test/m.mp4","label":"480p"}]});',
    'cfg = {"hls":"https:\\/\\/e.test\\/master.m3u8?t=1"}; var track = "https://e.test/sub.vtt";',
  ] });
  const r = readPlayers({}, doc);
  const byUrl = Object.fromEntries(r.map((x) => [x.url, x]));
  assert.equal(byUrl["https://c.test/v/1080.m3u8"].height, 1080);
  assert.equal(byUrl["https://c.test/v/720.m3u8"].label, "720p");
  assert.equal(byUrl["https://d.test/h.mp4"].height, 360);
  assert.equal(byUrl["https://d.test/m.mp4"].label, "480p");
  assert.ok(byUrl["https://e.test/master.m3u8?t=1"]);
  assert.ok(!byUrl["https://e.test/sub.vtt"]);
});
await test("players: jwplayer sources, video.js currentSources, HLS.js levels, <source> öznitelikleri", () => {
  const win = {
    jwplayer: () => ({ getPlaylist: () => [{ sources: [{ file: "/hls/1080.m3u8", label: "1080p", type: "hls" }, { file: "https://j.test/720.mp4", label: "720p" }] }] }),
    videojs: { getPlayers: () => ({ a: { currentSources: () => [{ src: "https://v.test/a.m3u8", type: "application/x-mpegURL", label: "Auto" }] } }) },
    hls: { levels: [{ url: ["https://h.test/l/480.m3u8"], height: 480 }, { url: "https://h.test/l/360.m3u8", height: 360, name: "360p" }], loadSource() {} },
  };
  const doc = mkDoc({ sources: [{ src: "https://s.test/hd.mp4", size: "1080", type: "video/mp4" }, { src: "blob:https://x/abc" }, { src: "https://s.test/x.js" }] });
  const r = readPlayers(win, doc);
  const m = Object.fromEntries(r.map((x) => [x.url, x]));
  assert.equal(m["https://film.test/hls/1080.m3u8"].height, 1080);
  assert.equal(m["https://j.test/720.mp4"].height, 720);
  assert.ok(m["https://v.test/a.m3u8"]);
  assert.equal(m["https://h.test/l/480.m3u8"].height, 480);
  assert.equal(m["https://h.test/l/360.m3u8"].label, "360p");
  assert.equal(m["https://s.test/hd.mp4"].height, 1080);
  assert.equal(r.length, 6);                              // blob: ve .js atıldı
  JSON.stringify(r);                                      // düz veri (executeScript'ten döner)
});
await test("qualities mesajı: players alanı biçimi ve sınırı", () => {
  const m = proto.buildQualities({ requestId: "q", tabId: 1, frameId: 0, players: [{ url: "https://a/1.m3u8", label: "1080p", height: 1080 }, { url: 5 }, null, ...Array(60).fill({ url: "https://a/x.mp4" })] });
  validate(m);
  assert.equal(m.players.length, 40);
  assert.deepEqual(m.players[0], { url: "https://a/1.m3u8", label: "1080p", height: 1080 });
});
await test("media: 206 Content-Range toplam boyutu kullanılır; boyut mesajda gider; clearTab boş liste yollar", async () => {
  sent.length = 0; media.opts.debounceMs = 20;
  const h = (n, v) => ({ name: n, value: v });
  media.onResponse({ tabId: 21, frameId: 0, url: "https://v.test/ist.mp4", statusCode: 206, responseHeaders: [h("Content-Type", "video/mp4"), h("Content-Length", "1000"), h("Content-Range", "bytes 0-999/734003200")] });
  await sleep(80);
  const m = sent.find((x) => x.type === "media" && x.tabId === 21);
  validate(m);
  assert.equal(m.items[0].size, 734003200);
  sent.length = 0;
  media.clearTab(21);                                      // sayfa değişince App'e boş liste gider
  const e = sent.find((x) => x.type === "media" && x.tabId === 21);
  assert.deepEqual(e && e.items, []);
});

// ---- performans: segment/gürültü elemesi, artımlı media, çerez yalnızca tıklamada ----
const hh = (n, v) => ({ name: n, value: v });
const mediaMsgs = (tabId) => sent.filter((x) => x.type === "media" && x.tabId === tabId);
await test("parça (.ts/.m4s/video/mp2t) ve statik kaynaklar listeye girmez; kota dolmaz", async () => {
  sent.length = 0; media.opts.debounceMs = 20;
  const before = { ...media.stats };
  media.onResponse({ tabId: 40, frameId: 0, url: "https://c.test/v/master.m3u8", statusCode: 200, responseHeaders: [hh("Content-Type", "application/vnd.apple.mpegurl")] });
  for (let i = 0; i < 400; i++) {
    media.onResponse({ tabId: 40, frameId: 0, url: `https://c.test/v/seg-${i}.m4s`, statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "900000")] });
    media.onResponse({ tabId: 40, frameId: 0, url: `https://c.test/v/${i}.ts`, statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp2t"), hh("Content-Length", "900000")] });
    media.onResponse({ tabId: 40, frameId: 0, url: `https://c.test/v/chunk_${String(i).padStart(4, "0")}.mp4`, statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "900000")] });
    media.onResponse({ tabId: 40, frameId: 0, url: `https://c.test/img/${i}.jpg`, statusCode: 200, responseHeaders: [hh("Content-Type", "image/jpeg")] });
  }
  media.onResponse({ tabId: 40, frameId: 0, url: "https://c.test/v/movie.mp4", statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "50000000")] });
  await sleep(100);
  const items = mediaMsgs(40).flatMap((m) => m.items);
  assert.deepEqual(items.map((i) => i.url), ["https://c.test/v/master.m3u8", "https://c.test/v/movie.mp4"]);
  assert.ok(media.stats.segments - before.segments >= 400);
  assert.ok(media.stats.noise - before.noise >= 400);
  media.clearTab(40);
});
await test("media artımlıdır: yalnızca yeni öğeler (append:true); tekrarlanan öğe yeniden gönderilmez", async () => {
  sent.length = 0;
  media.onResponse({ tabId: 41, frameId: 0, url: "https://v.test/a.mp4", statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  await sleep(80);
  media.onResponse({ tabId: 41, frameId: 0, url: "https://v.test/b.mp4", statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  media.onResponse({ tabId: 41, frameId: 0, url: "https://v.test/a.mp4", statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  await sleep(80);
  const ms = mediaMsgs(41);
  assert.equal(ms.length, 2);
  validate(ms[0]); validate(ms[1]);
  assert.deepEqual(ms.map((m) => m.items.map((i) => i.url.slice(-5))), [["a.mp4"], ["b.mp4"]]);
  assert.ok(ms.every((m) => m.append === true));
  sent.length = 0; media.clearTab(41);
  const clr = mediaMsgs(41)[0];
  assert.equal(clr.append, false);                       // temizleme: App listeyi değiştirir
  assert.deepEqual(clr.items, []);
});
await test("büyük media mesajı 1 MB sınırına karşı bölünür (sessizce düşmez)", () => {
  const items = Array.from({ length: 50 }, (_, i) => ({ url: "https://v.test/" + "x".repeat(15000) + i + ".mp4", mime: "video/mp4", kind: "file" }));
  const chunks = media.chunkItems(items, 200 * 1024);
  assert.ok(chunks.length >= 4);
  assert.equal(chunks.flat().length, 50);
  assert.ok(chunks.every((c) => JSON.stringify(proto.buildMedia({ tabId: 1, items: c })).length < 220 * 1024));
});
await test("sekme başına öğe kotası ve grup üst sınırı", async () => {
  sent.length = 0; media.opts.debounceMs = 5;
  const oldTab = media.opts.maxTabItems; media.opts.maxTabItems = 30;
  for (let f = 0; f < 3; f++) for (let i = 0; i < 40; i++)
    media.onResponse({ tabId: 42, frameId: f, url: `https://v.test/f${f}/${i}.mp4`, statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  await sleep(60);
  assert.equal(mediaMsgs(42).flatMap((m) => m.items).length, 30);
  media.opts.maxTabItems = oldTab;
  const oldG = media.opts.maxGroups; media.opts.maxGroups = 10;
  for (let t = 100; t < 150; t++) media.onResponse({ tabId: t, frameId: 0, url: `https://v.test/${t}.mp4`, statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  assert.ok(media._debug().groups <= 10);
  media.opts.maxGroups = oldG;
  for (let t = 100; t < 150; t++) media.clearTab(t);
  media.clearTab(42);
  await sleep(30);
});
await test("closeTab: durum temizlenir ve App'e tabClosed gider", () => {
  sent.length = 0;
  media.onResponse({ tabId: 43, frameId: 0, url: "https://v.test/c.mp4", statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  media.closeTab(43);
  assert.ok(sent.some((x) => x.type === "tabClosed" && x.tabId === 43));
});
await test("çerez yalnızca tıklamada: cookiesForTab origin başına bir kez; qualities.cookies şeması", async () => {
  media.opts.debounceMs = 5;
  media.onResponse({ tabId: 44, frameId: 0, url: "https://cdn1.test/a.mp4", statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  media.onResponse({ tabId: 44, frameId: 0, url: "https://cdn1.test/b.mp4", statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  media.onResponse({ tabId: 44, frameId: 0, url: "https://cdn2.test/x.m3u8", statusCode: 200, responseHeaders: [hh("Content-Type", "application/x-mpegURL")] });
  let calls2 = 0; const orig = chrome.cookies.getAll;
  chrome.cookies.getAll = async () => { calls2++; return [{ name: "k", value: "v" }]; };
  const map = await media.cookiesForTab(44);
  chrome.cookies.getAll = orig;
  assert.deepEqual(map, { "https://cdn1.test": "k=v", "https://cdn2.test": "k=v" });
  assert.equal(calls2, 2);
  validate(proto.buildQualities({ requestId: "q", tabId: 44, cookies: map }));
  assert.deepEqual(proto.buildQualities({ requestId: "q", tabId: 44, cookies: { "https://a.test": "x", "https://b.test": 5, "not-an-origin": "y" } }).cookies, { "https://a.test": "x" });
  media.clearTab(44);
});
await test("segment sezgisi: manifest yoksa seg-1.mp4 normal dosyadır", () => {
  assert.equal(proto.isSegment("https://a/seg-1.mp4", "video/mp4", false), false);
  assert.equal(proto.isSegment("https://a/seg-1.mp4", "video/mp4", true), true);
  assert.equal(proto.isSegment("https://a/x.m4s", "video/mp4", false), true);
  assert.equal(proto.isSegment("https://instagram.ferz3-1.fna.fbcdn.net/o1/v/t2/f2/m367/AQOGbs.mp4?efg=eyJ2ZW5", "video/mp4", false), true);
  assert.equal(proto.isSegment("https://a/film.mp4", "video/mp4", true), false);
  assert.equal(proto.isNoiseUrl("https://a/x.png?v=1"), true);
  assert.equal(proto.isNoiseUrl("https://a/x.m3u8"), false);
  assert.equal(proto.isNoiseUrl("https://a/get.php?id=1"), false);
});

await test("kalite istekleri: menü kapanınca/sekme değişince qualitiesCancel gider; tamamlananlar izlenmez", () => {
  sent.length = 0;
  const a = qlist.newId("abc12345"), b = qlist.newId("bad id!"), c = qlist.newId(undefined);
  assert.equal(a, "abc12345"); assert.notEqual(b, "bad id!"); assert.ok(c.startsWith("q"));
  qlist.track("r-a", 10); qlist.track("r-b", 11); qlist.track("r-c", 11);
  assert.equal(qlist.pending(), 3);
  qlist.done("r-c");
  assert.equal(qlist.cancel("r-a"), true);
  assert.equal(qlist.cancel("r-a"), false);                                   // ikinci kez gönderilmez
  const gone = qlist.cancelWhere((tab) => tab === 11);                         // sekme değişti/kapandı
  assert.deepEqual(gone, ["r-b"]);
  assert.deepEqual(sent.filter((x) => x.type === "qualitiesCancel").map((x) => x.requestId), ["r-a", "r-b"]);
  assert.equal(qlist.pending(), 0);
  for (let i = 0; i < 80; i++) qlist.track("x" + i + "____", i);
  assert.ok(qlist.pending() <= 50);
  qlist.cancelWhere(() => true);
});
// ---- düğme: sabit/yarı saydam üst çubuk örtüsü ----
function overlayHarness(videoRect, { stack, cands = [] } = {}) {
  const mkNode = (rect, pos, parent = null, style = {}) => {
    const n = { nodeType: 1, _pos: pos, _style: style, parentElement: parent, rect, isConnected: true,
      contains(x) { for (let c = x; c; c = c.parentElement) if (c === this) return true; return false; },
      getBoundingClientRect() { const q = this.rect; return { left: q.l, top: q.t, right: q.r, bottom: q.b, width: q.r - q.l, height: q.b - q.t }; } };
    return n;
  };
  const video = fakeVideo(videoRect); video.contains = (x) => x === video; video.parentElement = null;
  let stackFn = () => [];
  const h = loadButton({ doc: { elementsFromPoint: (x, y) => stackFn(x, y), getElementsByTagName: () => cands(h) }, sandbox: { setTimeout: () => 0, location: { hostname: "site.test" } } });
  h.sandbox.window.getComputedStyle = (el) => ({ display: "block", visibility: "visible", opacity: "1", position: el._pos || "static", backdropFilter: (el._style || {}).backdropFilter });
  h.video = video; h.mkNode = mkNode;
  if (stack) stackFn = () => stack(h);
  let now = 0; h.b.setClock(() => now); h.tick = (dt) => { now += dt; h.b.place(); };
  h.b.setTarget(video);
  return h;
}
const bpos = (h) => [parseFloat(h.els.btn.style.left), parseFloat(h.els.btn.style.top)];
await test("düğme: yarı saydam sabit üst çubuk yokken ve varken konum = videonun GERÇEK sağ üst köşesi (a)", () => {
  const h = overlayHarness({ l: 100, t: 200, w: 640, h: 360 }, { cands: () => [h.bar] });
  h.bar = h.mkNode({ l: 0, t: 0, r: 1000, b: 53 }, "fixed", null, { backdropFilter: "blur(12px)" });   // videonun altında değil: örtü yok
  h.tick(0); h.tick(500);
  assert.deepEqual(bpos(h), [740 - 160 - 8, 208]);
  assert.ok(!h.els.btn.style.clipPath || h.els.btn.style.clipPath === "none");
});
await test("düğme: sabit konum ATANIN stilinde; elementsFromPoint ilk eleman olarak kendi host'umuzu döndürse de örtü bulunur ve kırpılır; konum değişmez", () => {
  const h = overlayHarness({ l: 100, t: 20, w: 640, h: 360 }, { stack: (hh) => [hh.b.host, hh.tab, hh.video] });
  const wrap = h.mkNode({ l: 0, t: 0, r: 1000, b: 39 }, "fixed");
  h.tab = h.mkNode({ l: 300, t: 5, r: 500, b: 45 }, "static", h.mkNode({ l: 0, t: 0, r: 1000, b: 39 }, "static", wrap));
  h.tick(0); h.tick(500);
  assert.equal(h.els.btn.style.display, "flex");
  assert.deepEqual(bpos(h), [740 - 160 - 8, 28]);
  assert.equal(h.els.btn.style.clipPath, "inset(11px 0 0px 0)");
});
await test("düğme: video yukarı kaydıkça düğme video köşesine göre SABİT ofsette kalır, aşağı kaymaz (b)", () => {
  const h = overlayHarness({ l: 100, t: 20, w: 640, h: 360 }, { cands: () => [h.bar] });
  h.bar = h.mkNode({ l: 0, t: 0, r: 1000, b: 53 }, "fixed");
  h.tick(0); h.tick(500);
  for (const t of [20, -20, -100, -200, -300]) {
    h.video.rect.t = t; h.tick(120);
    if (h.els.btn.style.display !== "flex") continue;
    assert.equal(bpos(h)[1] - t, 8, `t=${t} dikey ofset sabit`);
    assert.equal(bpos(h)[0], 740 - 160 - 8);
  }
});
await test("düğme: çubuk düğmenin üst yarısını örterken clip-path üst değeri = örtülen yükseklik, konum değişmez (c); tamamen örtülünce gizli (d)", () => {
  const h = overlayHarness({ l: 100, t: 20, w: 640, h: 360 }, { cands: () => [h.bar] });
  h.bar = h.mkNode({ l: 0, t: 0, r: 1000, b: 39 }, "fixed");      // düğme 28..50, bh=22 -> örtülen 11 px
  h.tick(0); h.tick(500);
  assert.equal(h.els.btn.style.display, "flex");
  assert.deepEqual(bpos(h), [740 - 160 - 8, 28]);
  assert.equal(h.els.btn.style.clipPath, "inset(11px 0 0px 0)");
  h.bar.rect.b = 60; h.tick(120);                                  // düğme tamamen çubuğun altında
  assert.equal(h.els.btn.style.display, "none");
  assert.ok(!h.els.btn.classList.has("vis"));
  h.bar.rect.b = 0; h.tick(120);
});
await test("düğme: alttan örten sabit çubuk düğmeyi alttan kırpar (inset ... 0 0 alt)", () => {
  const h = overlayHarness({ l: 100, t: 400, w: 640, h: 360 }, { cands: () => [h.bar] });   // video alt kenarı viewport dışı; düğme 408..430
  h.bar = h.mkNode({ l: 0, t: 420, r: 1000, b: 600 }, "fixed");
  h.tick(0); h.tick(500);
  assert.deepEqual(bpos(h), [740 - 160 - 8, 408]);
  assert.equal(h.els.btn.style.clipPath, "inset(0px 0 10px 0)");
});
await test("düğme: dikey yan paneller (YouTube sağ menüsü vb.) üst çubuk sanılıp düğmeyi gizlemez", () => {
  const h = overlayHarness({ l: 100, t: 56, w: 640, h: 360 }, { cands: () => [h.sidePanel] });
  h.sidePanel = h.mkNode({ l: 700, t: 56, r: 760, b: 900 }, "fixed"); // sağda dikey uzanan sabit panel
  h.tick(0); h.tick(500);
  assert.equal(h.els.btn.style.display, "flex");
  assert.deepEqual(bpos(h), [740 - 160 - 8, 64]);
  assert.ok(!h.els.btn.style.clipPath || h.els.btn.style.clipPath === "none");
});
await test("düğme: YouTube izleme sayfası — video üstü 56'ya bitişik, videonun ARKASINDAKİ uzun yapışkan/sabit sütun (secondary-split-scroll-spacer) düğmeyi gizlemez", () => {
  const h = overlayHarness({ l: 359, t: 56, w: 640, h: 480 }, { cands: () => [h.spacer, h.secondary, h.masthead] });
  h.spacer = h.mkNode({ l: 347, t: 56, r: 1003, b: 614 }, "sticky");       // videoyla aynı sütun, tüm yükseklik, videonun arkasında
  h.secondary = h.mkNode({ l: 240, t: 56, r: 1663, b: 614 }, "fixed");
  h.masthead = h.mkNode({ l: 0, t: 0, r: 1443, b: 56 }, "fixed");           // bitişik üst çubuk: düğmeyi (64..) örtmez
  h.tick(0); h.tick(500);
  assert.equal(h.els.btn.style.display, "flex", "düğme gizlenmemeli");
  assert.deepEqual(bpos(h), [999 - 160 - 8, 64]);
  assert.ok(!h.els.btn.style.clipPath || h.els.btn.style.clipPath === "none");
});
function clickHarness() {
  const h = overlayHarness({ l: 100, t: 20, w: 640, h: 360 }, { cands: () => [h.bar] });
  h.bar = h.mkNode({ l: 0, t: 0, r: 1000, b: 39 }, "fixed");
  h.tick(0); h.tick(500);
  h.els.btn.getBoundingClientRect = () => ({ left: 572, top: 28, right: 732, bottom: 50, width: 160, height: 22 });
  h.click = (y) => { for (const f of h.els.btn.ls.click) f({ isTrusted: true, clientX: 600, clientY: y, stopPropagation() {}, preventDefault() {} }); };
  return h;
}
await test("düğme: kırpılmış bölgeye tıklama menü açmaz (e); görünen bölgeye tıklama açar", () => {
  const h = clickHarness();
  assert.equal(h.els.btn.style.clipPath, "inset(11px 0 0px 0)");
  h.click(33);
  assert.notEqual(h.els.menu.style.display, "block", "kırpılmış bölge tıklaması yok sayılmalı");
  h.click(45);
  assert.equal(h.els.menu.style.display, "block");
});
await test("düğme: menü açıkken kırpma başlayınca menü kapanır (f)", () => {
  const h = overlayHarness({ l: 100, t: 20, w: 640, h: 360 }, { cands: () => [h.bar] });
  h.bar = h.mkNode({ l: 0, t: 0, r: 1000, b: 10 }, "fixed");        // başta düğmeyi (28..50) örtmez
  h.tick(0); h.tick(500);
  assert.ok(!h.els.btn.style.clipPath || h.els.btn.style.clipPath === "none");
  for (const f of h.els.btn.ls.click) f({ isTrusted: true, clientX: 600, clientY: 40, stopPropagation() {}, preventDefault() {} });
  assert.equal(h.els.menu.style.display, "block");
  h.bar.rect.b = 39; h.tick(120);                                    // çubuk düğmenin üst kısmına girdi
  assert.equal(h.els.btn.style.clipPath, "inset(11px 0 0px 0)");
  assert.equal(h.els.menu.style.display, "none");
});

await test("button.js: menü kapanınca bekleyen kalite isteği iptal edilir (kaynakta tek gönderim noktası korunur)", () => {
  const src = readFileSync(join(root, "button.js"), "utf8");
  assert.ok(/kind:\s*"qualitiesCancel",\s*rid:\s*pendingRid/.test(src));
  assert.equal((src.match(/kind:\s*"qualities"/g) || []).length, 1);
});

// ---- 02-app-ipc-eklenti.md düzeltmeleri ----
function loadButtonCapturing() {
  const winLs = {}, btnLs = {}, msgs = [];
  let shadowMode = null, elFromPointCalls = 0;
  const els = {};
  const mkEl = (ls) => {
    const cls = new Set();
    return { style: {}, classList: { add: (c) => cls.add(c), remove: (c) => cls.delete(c), has: (c) => cls.has(c), contains: (c) => cls.has(c) }, offsetWidth: 160, offsetHeight: 22,
      addEventListener: (t, f) => { (ls[t] ??= []).push(f); }, getBoundingClientRect: () => ({ left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 }), matches: () => false,
      set innerHTML(v) {}, scrollHeight: 100 };
  };
  const vid = { tagName: "VIDEO", isConnected: true, closest: () => null, currentSrc: "", src: "", getBoundingClientRect: () => ({ left: 10, top: 10, width: 640, height: 360, right: 650, bottom: 370 }) };
  const doc = {
    title: "Sayfa", appendChild() {}, fullscreenElement: null, createElement: () => { const e = mkEl({}); e.attachShadow = (o) => { shadowMode = o.mode; return { set innerHTML(v) {}, getElementById: (id) => (els[id] ??= mkEl(id === "btn" ? btnLs : {})) }; }; return e; },
    addEventListener() {}, documentElement: null, elementFromPoint: () => null, hidden: false, querySelectorAll: () => [],
    elementsFromPoint: () => { elFromPointCalls++; return [vid]; },
  };
  const win = { innerWidth: 1000, innerHeight: 600, getComputedStyle: () => ({ display: "block", visibility: "visible", opacity: "1", position: "static" }), addEventListener() {} };
  win.top = win;
  const sandbox = { window: win, document: doc, __TD_TEST__: {}, parseFloat, Math, setTimeout: () => 0, clearTimeout, setInterval: () => 0, clearInterval() {}, requestAnimationFrame: () => 0,
    addEventListener: (t, f) => { (winLs[t] ??= []).push(f); }, innerWidth: 1000, innerHeight: 600, location: { hostname: "site.test" },
    chrome: { runtime: { sendMessage(m, cb) { msgs.push(m); if (m && m.kind === "cfg" && cb) cb({ showVideoButton: true }); }, onMessage: mkListener(), getURL: (p) => "chrome-extension://x/" + p } } };
  sandbox.globalThis = sandbox; vm.createContext(sandbox);
  vm.runInContext(readFileSync(join(root, "button.js"), "utf8"), sandbox);
  return { b: sandbox.__TD_TEST__.button, winLs, btnLs, msgs, els, vid, shadowMode: () => shadowMode, elFromPointCalls: () => elFromPointCalls };
}
await test("A2: shadow root 'closed'; sentetik (isTrusted=false) mousemove/click/menü tıklaması menü açmaz, istek/indirme tetiklemez", () => {
  const h = loadButtonCapturing();
  assert.equal(h.shadowMode(), "closed");
  const ev = (trusted, extra = {}) => ({ isTrusted: trusted, clientX: 50, clientY: 50, stopPropagation() {}, preventDefault() {}, composedPath: () => [], ...extra });
  for (const f of h.winLs.mousemove) f(ev(false));
  assert.equal(h.elFromPointCalls(), 0, "sentetik mousemove video aramasını bile başlatmaz");
  for (const f of h.winLs.mousemove) f(ev(true));
  assert.ok(h.elFromPointCalls() > 0, "gerçek mousemove çalışır");
  h.b.setTarget(h.vid);
  for (const f of h.btnLs.click) f(ev(false));
  assert.equal(h.msgs.filter((m) => m && m.kind === "qualities").length, 0, "sentetik btn.click() kalite isteği göndermez");
  for (const f of h.btnLs.click) f(ev(true));
  assert.equal(h.msgs.filter((m) => m && m.kind === "qualities").length, 1, "gerçek tıklama kalite isteği gönderir");
  h.b.drawOptions({ title: "T", selectionId: "sel1", options: [{ label: "720p", kind: "video", size: 1, isDefault: true }] });
  const row = { closest: () => ({ dataset: { i: "0" } }) };
  h.els.menu.onclick(ev(false, { target: row }));
  assert.equal(h.msgs.filter((m) => m && m.kind === "qualitySelect").length, 0, "sentetik .row.click() indirme başlatmaz");
  h.els.menu.onclick(ev(true, { target: row }));
  assert.equal(h.msgs.filter((m) => m && m.kind === "qualitySelect").length, 1);
  const before = h.msgs.length;
  for (const f of h.winLs.keydown) f(ev(false, { key: "Escape" }));
  assert.equal(h.msgs.length, before);
});
await test("A8: qualitySelect gönderilemediyse düğme hata gösterir (başarı sayılmaz)", () => {
  const src = readFileSync(join(root, "button.js"), "utf8");
  assert.ok(/r\.ok === true/.test(src) && /ulaşılamadı; indirme başlatılamadı/.test(src));
  const bg = readFileSync(join(root, "background.js"), "utf8");
  assert.ok(/sendResponse\(\{ ok: native\.send\(buildQualitySelect/.test(bg));
});
await test("A17: content.js Alt tuşu/tıklama olayları isTrusted ister", () => {
  const src = readFileSync(join(root, "content.js"), "utf8");
  assert.ok(/e\.isTrusted && e\.key === "Alt"/.test(src) && /e\.isTrusted && e\.altKey/.test(src));
});
await test("A1: çerez her zaman belirli bir URL için (getAll({url})) okunur; oynatıcı (players) adresleri için ASLA; mesaj host bilgisini taşır", async () => {
  media.opts.debounceMs = 5;
  media.onResponse({ tabId: 61, frameId: 0, url: "https://film.test/master.m3u8", statusCode: 200, responseHeaders: [hh("Content-Type", "application/x-mpegURL")] });
  const asked = []; const orig = chrome.cookies.getAll;
  chrome.cookies.getAll = async (q) => { asked.push(q); return [{ name: "sid", value: "S" }]; };
  const cookies = await media.cookiesForTab(61);
  chrome.cookies.getAll = orig;
  assert.deepEqual(asked, [{ url: "https://film.test/master.m3u8" }], "yalnızca yakalanan öğenin kendi URL'si");
  assert.deepEqual(cookies, { "https://film.test": "sid=S" });
  const q = proto.buildQualities({ requestId: "q61", tabId: 61, cookies, players: [{ url: "https://saldirgan.example/a.m3u8", label: "x", height: 0 }] });
  validate(q);
  assert.deepEqual(q.cookies, { "https://film.test": "sid=S" });
  assert.deepEqual(q.cookieHosts, { "https://film.test": "film.test" });
  assert.equal(Object.keys(q.cookies).some((k) => k.includes("saldirgan")), false);
  const bg = readFileSync(join(root, "background.js"), "utf8");
  assert.equal(/cookies\.getAll/.test(bg), false, "background.js çerezi doğrudan okumaz; yalnızca media.cookiesForTab");
  media.clearTab(61);
});
await test("A8/A9: resendAll append:false ile yeniden yollar; navigasyon (grup belleği boşken de) tabClosed yollar", async () => {
  media.opts.debounceMs = 5;
  media.onResponse({ tabId: 62, frameId: 0, url: "https://v.test/film.mp4", statusCode: 200, responseHeaders: [hh("Content-Type", "video/mp4"), hh("Content-Length", "9000000")] });
  sent.length = 0;
  await media.resendAll();
  const rs = sent.filter((x) => x.type === "media" && x.tabId === 62);
  assert.equal(rs.length, 1); assert.equal(rs[0].append, false); assert.equal(rs[0].items.length, 1);
  sent.length = 0;
  media.navigated(999);
  assert.deepEqual(sent.filter((x) => x.type === "tabClosed").map((x) => x.tabId), [999]);
  media.clearTab(62);
  const bg = readFileSync(join(root, "background.js"), "utf8");
  assert.ok(/change\.status === "loading"\) \{ qlist\.cancelWhere/.test(bg), "adres değişmese de (F5) loading sıfırlar");
});
await test("A15: kalite isteği kimliği await'ten önce izlenir", () => {
  const bg = readFileSync(join(root, "background.js"), "utf8");
  assert.ok(bg.indexOf("qlist.track(requestId") > 0 && bg.indexOf("qlist.track(requestId") < bg.indexOf("Promise.all([probePlayers"));
});
await test("A12: gizli pencere (incognito) indirmesi yakalanmaz", async () => {
  sent.length = 0; calls.length = 0;
  await capture.onDownloadCreated({ id: 777, url: "https://x.test/gizli.zip", incognito: true, state: "in_progress", startTime: new Date().toISOString() });
  assert.equal(sent.filter((x) => x.type === "download").length, 0);
  assert.equal(calls.length, 0, "tarayıcı indirmesine dokunulmaz");
});


// ---- düğme kaybolma hatası: video yüklenince oynatıcı DOM'u yeniden kurulur / video öğesi değişir ----
await test("düğme: yükleme ekranındaki video yerine yenisi gelince (eski kopar) düğme yeni videoda yeniden belirir; çift host/düğme yok; mousemove capture", async () => {
  let ioCb = null; const winLs = []; const appended = [];
  const mkV = (rect) => Object.assign(fakeVideo(rect), { currentSrc: "", src: "" });
  const oldV = mkV({ l: 100, t: 100, w: 640, h: 360 });
  const newV = mkV({ l: 100, t: 100, w: 640, h: 360 });
  let list = [oldV], mo = null;
  const { b, els, sandbox, doc } = loadButton({
    doc: { querySelectorAll: () => list, elementsFromPoint: () => [newV], appendChild() {}, documentElement: { appendChild: (n) => appended.push(n) } },
    sandbox: {
      IntersectionObserver: class { constructor(cb) { ioCb = cb; } observe() {} },
      MutationObserver: class { constructor(cb) { mo = cb; } observe() {} },
      setInterval: () => 0, clearInterval() {}, requestAnimationFrame: () => 1,
      addEventListener: (t, f, o) => { winLs.push([t, o]); },
    },
  });
  let now = 1e6; b.setClock(() => now); b.resetTrackers();   // başlangıç keşfi gerçek saatle izleyici açmış olabilir
  ioCb([{ target: oldV, isIntersecting: true }]);        // yükleme ekranı videosu hedef olur
  b.place(); now += 500; b.place();
  assert.equal(els.btn.style.display, "flex");
  // oynatıcı yeniden kuruldu: eski video DOM'dan çıktı, yenisi eklendi
  oldV.isConnected = false; list = [newV];
  now += 16; b.place();
  assert.equal(els.btn.style.display, "none");
  mo([{ type: "childList", addedNodes: [{ nodeType: 1 }] }]);   // gözlemci yeniden keşfi planlar
  await new Promise((r) => setTimeout(r, 250));
  ioCb([{ target: newV, isIntersecting: true }]);          // IO yeni videoyu görünür bildirir -> hedef olur
  now += 16; b.place(); now += 500; b.place();
  assert.equal(els.btn.style.display, "flex", "yeni videoda düğme yeniden görünmeli");
  assert.equal(parseFloat(els.btn.style.left), 100 + 640 - 160 - 8);
  // host sayfa tarafından sökülürse geri takılır
  doc.createElement; const before = appended.length;
  now += 16; b.place();
  assert.ok(appended.length >= before);
  const mm = winLs.find(([t]) => t === "mousemove");
  assert.ok(mm && mm[1] && mm[1].capture === true, "mousemove capture ile dinlenmeli (oynatıcı stopPropagation yapsa da)");
});

await test("Instagram: indirme butonuna basılınca aşağı kaydırmada menü ve buton kapanmaz, ekranda sabitlenir", () => {
  const h = overlayHarness({ l: 100, t: 80, w: 640, h: 360 }, { cands: () => [h.bar] });
  h.sandbox.location.hostname = "www.instagram.com";
  h.bar = h.mkNode({ l: 0, t: 0, r: 1000, b: 60 }, "fixed");
  h.tick(0); h.tick(500);
  assert.equal(h.els.btn.style.display, "flex");
  for (const f of h.els.btn.ls.click) f({ isTrusted: true, clientX: 600, clientY: 90, stopPropagation() {}, preventDefault() {} });
  assert.equal(h.els.menu.style.display, "block");
  // Aşağı kaydır: video yukarı çıksın, üst çubuğun altına girsin (t = 20)
  h.video.rect.t = 20;
  h.tick(120);
  assert.equal(h.els.menu.style.display, "block", "menü açık kalmalı");
  assert.equal(h.els.btn.style.display, "flex", "buton görünür kalmalı");
  assert.equal(h.els.btn.style.clipPath, "none", "buton kırpılmamalı");
});

await test("Instagram: aşağı kaydırırken buton mevcut videoda sabit kalır, sağ üstte sabitlenir, alttaki videolara otomatik geçmez", () => {
  const v1 = fakeVideo({ l: 100, t: 80, w: 640, h: 360 });
  const v2 = fakeVideo({ l: 100, t: 500, w: 640, h: 360 });
  let list = [v1, v2];
  const { b, els, sandbox } = loadButton({
    doc: {
      querySelectorAll: () => list,
      elementsFromPoint: () => [v1],
    },
    sandbox: {
      location: { hostname: "www.instagram.com", pathname: "/" }
    }
  });

  b.setTarget(v1);
  let now = 100;
  b.setClock(() => now);
  b.place(); now += 500; b.place();
  assert.equal(els.btn.style.display, "flex");

  // Aşağı kaydır: v1 yukarı çıksın (t = -200), ancak video altı halen ekranda (b = 160)
  v1.rect.t = -200;
  b.inView.add(v2);
  now += 100;
  b.place();

  // v1 hedef kalmalı, v2'ye otomatik atlamamalı
  assert.equal(b.getTarget(), v1, "hedef video 1 olarak kilitli kalmalı");
  // Buton ekranda görünür kalmalı ve sağ üstte (12px) sabitlenmeli
  assert.equal(els.btn.style.display, "flex", "aşağı kaydırmada buton görünür kalmalı");
  assert.equal(els.btn.style.top, "12px", "buton sağ üstte (12px) sabit kalmalı");
  assert.equal(els.btn.style.left, (100 + 640 - 160 - 8) + "px", "buton videonun sağ kenarında olmalı");
  assert.equal(els.btn.style.clipPath, "none", "buton kırpılmamalı");

  // Alttaki videoya (v2) tıklanırsa hedef v2'ye geçer
  const docLs = sandbox.document.ls || {};
  for (const f of (docLs.pointerdown || [])) f({ isTrusted: true, target: v2 });
  assert.equal(b.getTarget(), v2, "alttaki videoya basınca hedef v2 olmalı");
});

// ---- onDeterminingFilename: birincil devralma yolu ----
const uiCalls = [];
chrome.downloads.setUiOptions = async (o) => { uiCalls.push(o.enabled); };
chrome.runtime.id = "ext-self";
const det = (it) => new Promise((resolve) => {
  let suggested = 0;
  const ret = capture.onDeterminingFilename(it, () => { suggested++; });
  assert.equal(ret, true, "suggest asenkron: true dönmeli");
  setTimeout(() => resolve({ suggested }), 60);
});
const resetDet = () => {
  sent.length = 0; calls.length = 0; uiCalls.length = 0; hostBehavior = "accept"; hostDelayMs = 0;
  settings.applyAppSettings({ interceptEnabled: true, minFileSizeKb: 0, excludedSites: [], fileTypes: [] });
  settings.setUserCapture(true);
  native.connect();
};

await test("onDeterminingFilename: ACK -> iptal+sil, suggest ÇAĞRILMAZ, duraklatma yok", async () => {
  resetDet();
  const it = item();
  const r = await det(it);
  assert.equal(r.suggested, 0);
  const m = sent.find((x) => x.type === "download");
  validate(m);
  assert.equal(m.cookies, "a=1; b=2");
  assert.equal(m.filename, "setup.zip");
  assert.deepEqual(calls.map((c) => c[0]), ["cancel", "erase"]);
});
await test("onDeterminingFilename: host red -> suggest() (indirme kaybolmaz), arayüz geri açılır", async () => {
  resetDet(); hostBehavior = "reject"; capture.takeover.uiHoldMs = 30;
  await capture.syncUi(); assert.equal(capture.isUiHidden(), true); uiCalls.length = 0;
  const r = await det(item());
  assert.equal(r.suggested, 1);
  assert.deepEqual(calls, []);
  assert.ok(uiCalls.includes(true), "yedek indirme görünsün diye downloads UI geri açılmalı");
});
await test("onDeterminingFilename: ACK gelmezse zaman aşımı -> suggest()", async () => {
  resetDet(); hostBehavior = "silent"; capture.takeover.ackMs = 50;
  const it = item();
  const t0 = Date.now();
  const r = await new Promise((resolve) => {
    let n = 0; capture.onDeterminingFilename(it, () => { n++; });
    setTimeout(() => resolve({ n }), 250);
  });
  assert.equal(r.n, 1);
  assert.ok(Date.now() - t0 >= 50);
  capture.takeover.ackMs = 2000;
});
await test("onDeterminingFilename: desteklenmeyen/hariç durumlar -> suggest(), host'a hiçbir şey gitmez", async () => {
  const cases = [
    ["blob", item({ url: "blob:https://x/1", finalUrl: "blob:https://x/1" })],
    ["data", item({ url: "data:text/plain,a", finalUrl: "data:text/plain,a" })],
    ["file", item({ url: "file:///c:/a.zip", finalUrl: "file:///c:/a.zip" })],
    ["gizli pencere", item({ incognito: true })],
    ["kendi eklentimiz", item({ byExtensionId: "ext-self" })],
  ];
  for (const [name, it] of cases) {
    resetDet();
    const r = await det(it);
    assert.equal(r.suggested, 1, name);
    assert.equal(sent.filter((x) => x.type === "download").length, 0, name);
  }
  resetDet(); capture.notePost("https://dl.test/post.zip");
  assert.equal((await det(item({ url: "https://dl.test/post.zip", finalUrl: "https://dl.test/post.zip" }))).suggested, 1, "POST");
  resetDet(); capture.setAlt(5000);
  assert.equal((await det(item())).suggested, 1, "Alt basılı"); capture.setAlt(0);
  resetDet(); settings.setUserCapture(false);
  assert.equal((await det(item())).suggested, 1, "kullanıcı kapattı"); settings.setUserCapture(true);
  resetDet(); settings.applyAppSettings({ fileTypes: ["pdf"] });
  assert.equal((await det(item())).suggested, 1, "tür dışı (zip)");
  resetDet(); settings.applyAppSettings({ minFileSizeKb: 9_000_000 });
  assert.equal((await det(item())).suggested, 1, "çok küçük dosya");
  resetDet(); settings.applyAppSettings({ excludedSites: ["dl.test"] });
  assert.equal((await det(item())).suggested, 1, "hariç site");
  resetDet(); settings.setHostPaused("dl.test", true);
  assert.equal((await det(item())).suggested, 1, "bu sitede duraklat");
  assert.equal(sent.filter((x) => x.type === "download").length, 0);
  settings.setHostPaused("dl.test", false);
  resetDet();
  assert.equal((await det(item())).suggested, 0, "duraklatma kalkınca yeniden devralır");
});
await test("Y1: devralınmayan HER dal tarayıcı indirme arayüzünü (balon/raf) geri açar", async () => {
  capture.takeover.uiHoldMs = 30;
  const branches = [
    ["blob", () => {}, item({ url: "blob:https://x/1", finalUrl: "blob:https://x/1" })],
    ["data", () => {}, item({ url: "data:text/plain,a", finalUrl: "data:text/plain,a" })],
    ["POST", () => capture.notePost("https://dl.test/post2.zip"), item({ url: "https://dl.test/post2.zip", finalUrl: "https://dl.test/post2.zip" })],
    ["Alt", () => capture.setAlt(5000), item()],
    ["gizli", () => {}, item({ incognito: true })],
    ["kendi", () => {}, item({ byExtensionId: "ext-self" })],
    ["tür kuralı", () => settings.applyAppSettings({ fileTypes: ["pdf"] }), item()],
    ["boyut kuralı", () => settings.applyAppSettings({ minFileSizeKb: 9_000_000 }), item()],
    ["hariç site", () => settings.applyAppSettings({ excludedSites: ["dl.test"] }), item()],
    ["bu sitede duraklat", () => settings.setHostPaused("dl.test", true), item()],
    ["devralma kapalı", () => settings.applyAppSettings({ interceptEnabled: false }), item()],
    ["host red", () => { hostBehavior = "reject"; }, item()],
  ];
  for (const [name, arrange, it] of branches) {
    resetDet();
    await sleep(90);                                   // önceki tutma süresi dolsun
    await capture.syncUi();
    if (name !== "devralma kapalı") assert.equal(capture.isUiHidden(), true, name + ": önkoşul: arayüz gizli");
    uiCalls.length = 0;
    arrange();
    const r = await det(it);
    assert.equal(r.suggested, 1, name + ": suggest");
    if (name !== "devralma kapalı") assert.ok(uiCalls.includes(true), name + ": arayüz geri açılmalı");
    capture.setAlt(0); settings.setHostPaused("dl.test", false);
  }
  resetDet();
});
await test("site duraklatma: alt alan adı eşleşir, www yok sayılır", () => {
  settings.setHostPaused("www.Example.com", true);
  assert.equal(settings.isHostPaused("cdn.example.com"), true);
  assert.equal(settings.isHostPaused("example.com"), true);
  assert.equal(settings.isHostPaused("notexample.com"), false);
  settings.setHostPaused("example.com", false);
  assert.equal(settings.isHostPaused("example.com"), false);
});
await test("onCreated güvenlik ağı: onDeterminingFilename gelmişse dokunmaz, gelmemişse eski yolu çalıştırır", async () => {
  resetDet(); capture.takeover.safetyNetMs = 40;
  const a = item(), b = item();
  capture.onDeterminingFilename(a, () => {});
  capture.onCreatedSafetyNet(a);
  capture.onCreatedSafetyNet(b);               // determining gelmedi
  await sleep(300);
  assert.ok(!calls.some((c) => c[1] === a.id && c[0] === "pause"), "a için eski yol çalışmamalı");
  assert.ok(calls.some((c) => c[1] === b.id && c[0] === "pause"), "b için eski yol çalışmalı");
  capture.takeover.safetyNetMs = 4000;
});
await test("downloads.ui: bağlıyken arayüz gizlenir, devralma kapanınca geri açılır", async () => {
  resetDet(); await sleep(150);                 // önceki yedek-düşme tutma süresi dolsun
  settings.setUserCapture(false); await capture.syncUi(); uiCalls.length = 0;
  settings.setUserCapture(true);
  await capture.syncUi();
  assert.equal(capture.isUiHidden(), true);
  assert.equal(uiCalls.at(-1), false);
  settings.setUserCapture(false);
  await capture.syncUi();
  assert.equal(capture.isUiHidden(), false);
  assert.equal(uiCalls.at(-1), true);
  settings.setUserCapture(true);
});

console.log(`
${passed} test geçti`);
process.exit(0);
