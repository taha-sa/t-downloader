// Medya tespiti: yanıt başlıklarından m3u8/mpd/video-audio toplar, `media` mesajı gönderir.
// Film siteleri videoyu başka alan adındaki iframe oynatıcıdan verir; bu yüzden her öğe, isteği yapan
// çerçevenin (iframe) belge adresini Referer, origin'ini Origin olarak taşır. Çerezler öğeyle GİTMEZ: düğmeye
// tıklanınca (qualities) `cookiesForTab` ile tek seferde alınır.
//
// Yük azaltma: webRequest yalnızca medya olabilecek kaynak türlerinde dinlenir; statik kaynaklar (resim/css/js/yazı tipi)
// başlıklara bakılmadan elenir; HLS/DASH parçaları (.ts/.m4s...) listeye girmez ve kotayı doldurmaz; `media` mesajı
// artımlıdır (yalnızca yeni öğeler, append:true) ve 1 MB Host sınırına karşı bölünür.
import * as native from "./native.js";
import { buildMedia, classifyMedia, isNoiseUrl, isSegment, originOf } from "./protocol.js";

export const opts = {
  debounceMs: 400, maxItems: 50, maxHeaders: 400,
  maxGroups: 200,            // toplam (sekme:çerçeve) grubu
  maxTabItems: 120,          // sekme başına toplam öğe
  maxMessageBytes: 600 * 1024, // tek `media` mesajı (Host->tarayıcı sınırı 1 MB; güvenli pay)
};
// "tab:frame" -> { tabId, frameId, frameUrl, items: Map(url->item), fresh: Map(url->item) (henüz gönderilmemiş) }
const groups = new Map();
const timers = new Map();
const tabCount = new Map();     // tabId -> toplam öğe sayısı
const tabManifest = new Set();  // manifest (hls/dash) görülmüş sekmeler: parça adı sezgisi yalnızca bunlarda
const sentHeaders = new Map();  // requestId -> { referer, origin } (tarayıcının gerçekten gönderdikleri)
export const stats = { dropped: 0, segments: 0, noise: 0 };

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

// Düğme tıklanınca: sekmedeki öğelerin origin'leri için çerez başlığı (origin başına bir kez, en fazla 12 origin).
export async function cookiesForTab(tabId) {
  const samples = new Map();   // origin -> örnek url
  for (const g of groups.values()) {
    if (g.tabId !== tabId) continue;
    for (const it of g.items.values()) {
      const o = originOf(it.url);
      if (o && !samples.has(o)) samples.set(o, it.url);
      if (samples.size >= 12) break;
    }
  }
  const out = {};
  await Promise.all([...samples].map(async ([o, u]) => { const c = await cookieHeaderFor(u); if (c) out[o] = c; }));
  return out;
}

function evictGroups() {
  while (groups.size > opts.maxGroups) {
    const key = groups.keys().next().value;
    dropGroup(key);
  }
}
function dropGroup(key) {
  const g = groups.get(key);
  groups.delete(key); clearTimeout(timers.get(key)); timers.delete(key);
  if (g) tabCount.set(g.tabId, Math.max(0, (tabCount.get(g.tabId) || 0) - g.items.size));
}

export function onResponse(d) {
  if (d.tabId < 0 || d.statusCode >= 400 || d.statusCode === 204) return;
  if (isNoiseUrl(d.url)) { stats.noise++; sentHeaders.delete(d.requestId); return; }
  const mime = header(d.responseHeaders, "content-type");
  const len = parseInt(header(d.responseHeaders, "content-length"), 10);
  // 206 yanıtında Content-Length yalnızca parçadır; gerçek boyut Content-Range'in sonundadır ("bytes 0-99/12345").
  const crTotal = parseInt((/\/(\d+)\s*$/.exec(header(d.responseHeaders, "content-range")) || [])[1], 10);
  const size = Number.isFinite(crTotal) ? crTotal : Number.isFinite(len) ? len : -1;
  const sent = sentHeaders.get(d.requestId);
  sentHeaders.delete(d.requestId);
  const kind = classifyMedia(d.url, mime, size);
  if (!kind) return;
  // Manifest görülmüş sekmede parça gibi adlandırılan dosyalar da elenir (kotayı doldurmasın).
  if (kind === "file" && isSegment(d.url, mime, tabManifest.has(d.tabId))) { stats.segments++; return; }
  const key = `${d.tabId}:${d.frameId}`;
  let g = groups.get(key);
  if (!g) {
    g = { tabId: d.tabId, frameId: d.frameId, frameUrl: "", items: new Map(), fresh: new Map() };
    groups.set(key, g);
    evictGroups();
  }
  if (g.items.has(d.url)) return;
  if (g.items.size >= opts.maxItems || (tabCount.get(d.tabId) || 0) >= opts.maxTabItems) { stats.dropped++; return; }
  const frameUrl = d.documentUrl || "";
  if (frameUrl) g.frameUrl = frameUrl;
  // Referer: tarayıcının gönderdiği; yoksa isteği yapan belge (iframe) adresi; yoksa initiator origin'i.
  const referer = sent?.referer || frameUrl || (d.initiator ? d.initiator + "/" : "");
  const origin = sent?.origin || originOf(referer) || d.initiator || "";
  const item = { url: d.url, mime: mime.split(";")[0].trim(), kind, referer, origin, size, userAgent: navigator.userAgent };
  g.items.set(d.url, item);
  g.fresh.set(d.url, item);
  tabCount.set(d.tabId, (tabCount.get(d.tabId) || 0) + 1);
  if (kind === "hls" || kind === "dash") tabManifest.add(d.tabId);
  clearTimeout(timers.get(key));
  timers.set(key, setTimeout(() => flush(key), opts.debounceMs));
}

// Öğeleri, her mesaj maxMessageBytes'ı aşmayacak şekilde parçalara böler (tek öğe sınırı aşarsa tek başına gider).
export function chunkItems(items, maxBytes = opts.maxMessageBytes) {
  const chunks = [];
  let cur = [], bytes = 0;
  for (const it of items) {
    const n = JSON.stringify(it).length + 2;
    if (cur.length && bytes + n > maxBytes) { chunks.push(cur); cur = []; bytes = 0; }
    cur.push(it); bytes += n;
  }
  if (cur.length) chunks.push(cur);
  return chunks;
}

async function flush(key) {
  timers.delete(key);
  const g = groups.get(key);
  if (!g || g.fresh.size === 0) return;
  let tab = {};
  try { tab = await chrome.tabs.get(g.tabId); } catch { return; }
  const items = [...g.fresh.values()];
  g.fresh.clear();                                  // yalnızca YENİ öğeler gider; tekrar gönderilmez
  for (const part of chunkItems(items)) {
    native.send(buildMedia({
      tabId: g.tabId,
      frameId: g.frameId,
      frameUrl: g.frameUrl,
      pageUrl: tab.url || "",
      pageTitle: tab.title || "",
      items: part,
      append: true,
    }));
  }
}

// Sekme yeni sayfaya gidince eski listeyi at.
// App'e de boş liste bildirilir (append yok -> App listeyi değiştirir): eski sayfanın akışları yeni sayfanın kalite menüsüne karışmasın.
export function clearTab(tabId) {
  for (const [key, g] of [...groups]) {
    if (g.tabId !== tabId) continue;
    dropGroup(key);
    if (g.items.size > 0) native.send(buildMedia({ tabId, frameId: g.frameId, frameUrl: "", pageUrl: "", pageTitle: "", items: [] }));
  }
  tabCount.delete(tabId);
  tabManifest.delete(tabId);
}

// Sekme kapandı: eklentideki durum + (App'e) tabClosed.
export function closeTab(tabId) {
  clearTab(tabId);
  native.send({ type: "tabClosed", tabId });
}

export const _debug = () => ({ groups: groups.size, tabs: tabCount.size, manifests: tabManifest.size, headers: sentHeaders.size });

export function register() {
  // extraHeaders: Referer/Origin başlıklarını görebilmek için gerekir.
  const types = ["media", "xmlhttprequest", "other", "sub_frame"];
  chrome.webRequest.onSendHeaders.addListener(onSendHeaders,
    { urls: ["<all_urls>"], types }, ["requestHeaders", "extraHeaders"]);
  chrome.webRequest.onResponseStarted.addListener(onResponse, { urls: ["<all_urls>"], types }, ["responseHeaders"]);
}
