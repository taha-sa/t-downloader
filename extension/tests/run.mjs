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
  media: { type: "s", tabId: "n", frameId: "n", pageUrl: "s", pageTitle: "s", items: "a" },
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
    assert.deepEqual(Object.keys(i).sort(), ["kind", "mime", "url"]);
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
const item = (o = {}) => ({ id: 7, url: "https://dl.test/f/setup.zip", finalUrl: "https://dl.test/f/setup.zip", referrer: "https://dl.test/", filename: "", mime: "application/zip", totalBytes: 2_000_000, ...o });

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

// ---- geometri (background) ----
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

console.log(`\n${passed} test geçti`);
process.exit(0);
