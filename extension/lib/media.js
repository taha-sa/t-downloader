// Medya tespiti: yanıt başlıklarından m3u8/mpd/video-audio toplar, `media` mesajı gönderir.
// Film siteleri videoyu başka alan adındaki iframe oynatıcıdan verir; bu yüzden her öğe, isteği yapan
// çerçevenin (iframe) belge adresini Referer, origin'ini Origin olarak ve o adresin çerezlerini taşır.
import * as native from "./native.js";
import { buildMedia, classifyMedia, originOf } from "./protocol.js";

export const opts = { debounceMs: 400, maxItems: 50, maxHeaders: 400 };
const groups = new Map(); // "tab:frame" -> { tabId, frameId, frameUrl, items: Map(url->item) }
const timers = new Map();
const sentHeaders = new Map(); // requestId -> { referer, origin } (tarayıcının gerçekten gönderdikleri)

const header = (headers, name) =>
  (headers || []).find((h) => h.name.toLowerCase() === name)?.value || "";

// Tarayıcının isteğe koyduğu Referer/Origin'i kaydet: CDN'in beklediği değerler bunlardır.
export function onSendHeaders(d) {
  if (d.tabId < 0) return;
  const referer = header(d.requestHeaders, "referer");
  const origin = header(d.requestHeaders, "origin");
  if (!referer && !origin) return;
  sentHeaders.set(d.requestId, { referer, origin });
  while (sentHeaders.size > opts.maxHeaders) sentHeaders.delete(sentHeaders.keys().next().value);
}

export async function cookieHeaderFor(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch { return ""; }
}

export function onResponse(d) {
  if (d.tabId < 0 || d.statusCode >= 400 || d.statusCode === 204) return;
  const mime = header(d.responseHeaders, "content-type");
  const len = parseInt(header(d.responseHeaders, "content-length"), 10);
  const kind = classifyMedia(d.url, mime, Number.isFinite(len) ? len : -1);
  const sent = sentHeaders.get(d.requestId);
  sentHeaders.delete(d.requestId);
  if (!kind) return;
  const key = `${d.tabId}:${d.frameId}`;
  let g = groups.get(key);
  if (!g) { g = { tabId: d.tabId, frameId: d.frameId, frameUrl: "", items: new Map() }; groups.set(key, g); }
  if (g.items.has(d.url) || g.items.size >= opts.maxItems) return;
  const frameUrl = d.documentUrl || "";
  if (frameUrl) g.frameUrl = frameUrl;
  // Referer: tarayıcının gönderdiği; yoksa isteği yapan belge (iframe) adresi; yoksa initiator origin'i.
  const referer = sent?.referer || frameUrl || (d.initiator ? d.initiator + "/" : "");
  const origin = sent?.origin || originOf(referer) || d.initiator || "";
  g.items.set(d.url, { url: d.url, mime: mime.split(";")[0].trim(), kind, referer, origin });
  clearTimeout(timers.get(key));
  timers.set(key, setTimeout(() => flush(key), opts.debounceMs));
}

async function flush(key) {
  timers.delete(key);
  const g = groups.get(key);
  if (!g) return;
  let tab = {};
  try { tab = await chrome.tabs.get(g.tabId); } catch { return; }
  const items = [...g.items.values()];
  for (const it of items) {
    if (it.cookies === undefined) it.cookies = await cookieHeaderFor(it.url);
    it.userAgent = navigator.userAgent;
  }
  native.send(buildMedia({
    tabId: g.tabId,
    frameId: g.frameId,
    frameUrl: g.frameUrl,
    pageUrl: tab.url || "",
    pageTitle: tab.title || "",
    items,
  }));
}

// Sekme yeni sayfaya gidince eski listeyi at.
export function clearTab(tabId) {
  for (const [key, g] of groups) {
    if (g.tabId === tabId) { groups.delete(key); clearTimeout(timers.get(key)); timers.delete(key); }
  }
}

export function register() {
  // extraHeaders: Referer/Origin/Cookie başlıklarını görebilmek için gerekir.
  chrome.webRequest.onSendHeaders.addListener(onSendHeaders,
    { urls: ["<all_urls>"], types: ["xmlhttprequest", "media", "other"] }, ["requestHeaders", "extraHeaders"]);
  chrome.webRequest.onResponseStarted.addListener(onResponse, { urls: ["<all_urls>"] }, ["responseHeaders"]);
}
