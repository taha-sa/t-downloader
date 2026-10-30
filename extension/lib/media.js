// Medya tespiti: yanıt başlıklarından m3u8/mpd/video-audio toplar, `media` mesajı gönderir.
import * as native from "./native.js";
import { buildMedia, classifyMedia } from "./protocol.js";

export const opts = { debounceMs: 400, maxItems: 50 };
const groups = new Map(); // "tab:frame" -> { tabId, frameId, items: Map(url->item) }
const timers = new Map();

const header = (headers, name) =>
  (headers || []).find((h) => h.name.toLowerCase() === name)?.value || "";

export function onResponse(d) {
  if (d.tabId < 0 || d.statusCode >= 400 || d.statusCode === 204) return;
  const mime = header(d.responseHeaders, "content-type");
  const len = parseInt(header(d.responseHeaders, "content-length"), 10);
  const kind = classifyMedia(d.url, mime, Number.isFinite(len) ? len : -1);
  if (!kind) return;
  const key = `${d.tabId}:${d.frameId}`;
  let g = groups.get(key);
  if (!g) { g = { tabId: d.tabId, frameId: d.frameId, items: new Map() }; groups.set(key, g); }
  if (g.items.has(d.url) || g.items.size >= opts.maxItems) return;
  g.items.set(d.url, { url: d.url, mime: mime.split(";")[0].trim(), kind });
  clearTimeout(timers.get(key));
  timers.set(key, setTimeout(() => flush(key), opts.debounceMs));
}

async function flush(key) {
  timers.delete(key);
  const g = groups.get(key);
  if (!g) return;
  let tab = {};
  try { tab = await chrome.tabs.get(g.tabId); } catch { return; }
  native.send(buildMedia({
    tabId: g.tabId,
    frameId: g.frameId,
    pageUrl: tab.url || "",
    pageTitle: tab.title || "",
    items: [...g.items.values()],
  }));
}

// Sekme yeni sayfaya gidince eski listeyi at.
export function clearTab(tabId) {
  for (const [key, g] of groups) {
    if (g.tabId === tabId) { groups.delete(key); clearTimeout(timers.get(key)); timers.delete(key); }
  }
}

export function register() {
  chrome.webRequest.onResponseStarted.addListener(onResponse, { urls: ["<all_urls>"] }, ["responseHeaders"]);
}
