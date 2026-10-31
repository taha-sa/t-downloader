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
  download: { type: "s", requestId: "s", url: "s", referrer: "s", cookies: "s", userAgent: "s", filename: "s", mime: "s", fileSize: "n" },
  media: { type: "s", tabId: "n", frameId: "n", pageUrl: "s", pageTitle: "s", frameUrl: "s", items: "a" },
  qualities: { type: "s", requestId: "s", tabId: "n", frameId: "n", pageUrl: "s", pageTitle: "s", frameUrl: "s", src: "s" },
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
    assert.deepEqual(Object.keys(i).sort(), ["cookies", "kind", "mime", "origin", "referer", "url", "userAgent"]);
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
const mkListener = () => { const f = []; return { addListener: (x) => f.push(x), _fire: (...a) => f.forEach((x) => x(...a)) }; };

function makePort() {
  const onMessage = mkListener(), onDisconnect = mkListener();
  ports++;
  return {
    onMessage, onDisconnect,
    postMessage(m) {
      sent.push(m);
      if (m.type === "download" && hostBehavior !== "silent") {
        queueMicrotask(() => onMessage._fire({ type: "downloadResult", requestId: m.requestId, accepted: hostBehavior === "accept" }));
      }
      if (m.type === "ping") queueMicrotask(() => onMessage._fire({ type: "pong", version: "1.2.3" }));
    },
  };
}
globalThis.navigator ??= {};
Object.defineProperty(globalThis, "navigator", { value: { userAgent: "TestUA/1.0" }, configurable: true });
globalThis.chrome = {
  runtime: {
    connectNative: (name) => { assert.equal(name, "com.tdownloader.host"); return makePort(); },
    lastError: undefined,
  },
  storage: { local: { get: async () => ({}), set: async () => {} } },
  cookies: { getAll: async () => [{ name: "a", value: "1" }, { name: "b", value: "2" }] },
  downloads: {
    search: async (q) => (searchResult ? [{ id: q.id, ...searchResult() }] : []),
    pause: async (id) => calls.push(["pause", id]),
    resume: async (id) => calls.push(["resume", id]),
    cancel: async (id) => calls.push(["cancel", id]),
    erase: async (q) => calls.push(["erase", q.id]),
  },
  tabs: { get: async (id) => ({ id, url: "https://site.test/watch", title: "Video" }) },
  windows: { get: async () => ({ left: 10, top: 20, width: 1280, height: 720, state: "normal" }) },
};

const url = (p) => pathToFileURL(join(root, p)).href;
const native = await import(url("lib/native.js"));
const capture = await import(url("lib/capture.js"));
const media = await import(url("lib/media.js"));
const geometry = await import(url("lib/geometry.js"));
const proto = await import(url("lib/protocol.js"));
const settings = await import(url("lib/settings.js"));

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
  assert.equal(a.cookies, "a=1; b=2");
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
  const p = b.buttonPos({ left: 100, top: 50, width: 640, height: 360 }, 160, 1000, 600);
  assert.deepEqual(JSON.parse(JSON.stringify(p)), { left: 570, top: 60 });                          // videonun sağ üstü
  assert.deepEqual(JSON.parse(JSON.stringify(b.buttonPos({ left: 900, top: -200, width: 640, height: 360 }, 160, 1000, 600))), { left: 836, top: 4 }); // ekrana sığar
  assert.equal(b.groupOf({ kind: "file" }), "Dosya");
  assert.equal(b.groupOf({ audioOnly: true }), "Sadece ses");
});

console.log(`\n${passed} test geçti`);
process.exit(0);
