// İndirme yakalama: duraklat -> App'e sor -> kabulse iptal+sil, değilse devam.
import * as native from "./native.js";
import * as settings from "./settings.js";
import { buildDownload, decideCapture, filenameFrom, hostOf } from "./protocol.js";

// App yanıtı artık onay penceresini BEKLEMEZ (kurallar uygunsa hemen accepted:true); 15 sn yalnızca App soğuk başlangıcı içindir.
export const opts = { timeoutMs: 15000 };
const NON_HTTP = /^(blob|data|file|filesystem|about|chrome|edge):/i;

// Alt tuşu durumu (content script bildirir).
let altUntil = 0;
export const setAlt = (ms) => { altUntil = ms > 0 ? Date.now() + ms : 0; };
export const isAltActive = () => Date.now() < altUntil;

// Kendi eklentimizin başlattığı (yedek) indirmeler yakalanmaz.
const bypass = new Set();
export const addBypass = (url) => bypass.add(url);

const safe = async (fn) => { try { await fn(); } catch { /* indirme zaten bitmiş olabilir */ } };

let seq = 0;
const newId = () => `${Date.now().toString(36)}-${(++seq).toString(36)}`;

export async function cookieHeader(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch { return ""; }
}

// Zaman aşımına uğramış isteklerin geç gelen yanıtı: requestId -> tarayıcı indirme kimliği.
// App geç de olsa kabul ettiyse (soğuk başlangıç) tarayıcıdaki kopya iptal edilir: çift indirme olmasın.
const lateWatch = new Map();
const LATE_MAX = 200;
const LATE_KEY = "lateWatch";
export const watchedCount = () => lateWatch.size;

// Servis çalışanı (MV3) bekleme sırasında yeniden başlayabilir: izleme tablosu chrome.storage.session'da da tutulur (en iyi çaba).
const sess = () => globalThis.chrome?.storage?.session;
const persistLate = () => {
  try { Promise.resolve(sess()?.set({ [LATE_KEY]: [...lateWatch] })).catch?.(() => {}); } catch { /* oturum deposu yok */ }
};
let lateReady = Promise.resolve();
// Bellekteki tabloyu atar ve depodan yeniden yükler (servis çalışanı yeniden başlatıldığında modül yüklenirken bir kez çalışır).
export function restoreLateWatch() {
  lateWatch.clear();
  lateReady = (async () => {
    try {
      const r = await sess()?.get(LATE_KEY);
      for (const [k, v] of r?.[LATE_KEY] || []) if (typeof k === "string" && !lateWatch.has(k)) lateWatch.set(k, v);
    } catch { /* depo yok */ }
  })();
  return lateReady;
}
restoreLateWatch();
native.onHostMessage(async (m) => {
  if (m.type !== "downloadResult" || typeof m.requestId !== "string") return;
  await lateReady;
  const id = lateWatch.get(m.requestId);
  if (id === undefined) return;
  lateWatch.delete(m.requestId);
  persistLate();
  if (m.accepted === true && id !== null) {
    safe(() => chrome.downloads.cancel(id)).then(() => safe(() => chrome.downloads.erase({ id })));
  }
});

// Ortak gönderim: download mesajını kurar, yanıtı bekler. true = App kabul etti.
// info.browserId: tarayıcı indirme kimliği (zaman aşımında geç kabul için izlenir); info.explicit: kullanıcı eylemi.
export async function sendDownload(info) {
  const requestId = newId();
  const t0 = Date.now();        // zaman aşımı sayacı çerez okumasını da kapsar
  const msg = buildDownload({
    requestId,
    url: info.url,
    referrer: info.referrer || "",
    cookies: await cookieHeader(info.url),
    userAgent: navigator.userAgent,
    filename: info.filename || "",
    mime: info.mime || "",
    fileSize: info.fileSize ?? -1,
    explicit: info.explicit === true,
  });
  const res = await native.request(
    msg,
    (m) => m.type === "downloadResult" && m.requestId === requestId,
    Math.max(1, (info.timeoutMs ?? opts.timeoutMs) - (Date.now() - t0)),
  );
  if (!res && info.browserId !== undefined) {
    lateWatch.set(requestId, info.browserId);
    while (lateWatch.size > LATE_MAX) lateWatch.delete(lateWatch.keys().next().value);
    persistLate();
  }
  return !!(res && res.accepted === true);
}

// Tarayıcı açılışta geçmişi yüklerken onCreated eski indirmeler için de tetiklenebilir.
// Yalnızca şu an başlayan indirmeler yakalanır; aynı indirme iki kez sorulmaz.
const MAX_AGE_MS = 30 * 1000;
const seen = new Set();
export function isFreshDownload(item, now = Date.now()) {
  if (item.state && item.state !== "in_progress") return false;
  const started = Date.parse(item.startTime || "");
  if (Number.isFinite(started) && now - started > MAX_AGE_MS) return false;
  if (seen.has(item.id)) return false;
  seen.add(item.id);
  if (seen.size > 500) seen.delete(seen.values().next().value);
  return true;
}

// onCreated anında dosya adı genelde boştur (Content-Disposition henüz işlenmedi). Tarayıcı adı belirleyince
// (kısa süre içinde) gerçek adı alırız; böylece App'e sunucunun verdiği ad gider.
export const nameWait = { totalMs: 1200, stepMs: 120 };
export async function waitForFilename(item) {
  if (item.filename) return item;
  const end = Date.now() + nameWait.totalMs;
  while (Date.now() < end) {
    await new Promise((r) => setTimeout(r, nameWait.stepMs));
    try {
      const [cur] = (await chrome.downloads.search?.({ id: item.id })) || [];
      if (cur?.filename) return { ...item, ...cur };
    } catch { return item; }
  }
  return item;
}

export async function onDownloadCreated(item) {
  const url = item.finalUrl || item.url || "";
  if (!url || NON_HTTP.test(url)) return;
  if (determined.has(item.id)) return;                    // onDeterminingFilename yolu bu indirmeyi zaten ele aldi                 // blob:/data: -> tarayıcıda kalsın
  if (item.incognito) return;                             // gizli pencere: App'e/diske URL yazılmaz, yanlış çerez deposu okunmaz (A12)
  if (!isFreshDownload(item)) return;                     // geçmişten gelen/tekrarlanan kayıt
  if (bypass.delete(item.url)) return;
  if (isAltActive()) return;                              // Alt basılı -> tarayıcıda kalsın
  await settings.ready;
  if (!settings.effective().interceptEnabled) return;
  if (!native.isConnected()) { native.connect(); if (!native.isConnected()) return; }

  legacy.add(item.id);
  await safe(() => chrome.downloads.pause(item.id));
  item = await waitForFilename(item);
  let accepted = false;
  try {
    const filename = filenameFrom(item);
    const fileSize = item.fileSize > 0 ? item.fileSize : item.totalBytes > 0 ? item.totalBytes : -1;
    const d = { url, referrer: item.referrer || "", filename, fileSize };
    if (decideCapture(settings.effective(), d).capture) {
      accepted = await sendDownload({ ...d, mime: item.mime || "", browserId: item.id });
    }
  } catch { accepted = false; }

  if (accepted) {
    await safe(() => chrome.downloads.cancel(item.id));
    await safe(() => chrome.downloads.erase({ id: item.id }));
  } else {
    await safe(() => chrome.downloads.resume(item.id));
  }
}


// =====================================================================================================================
// Tarayici indirmelerini DEVRAL (birincil yol): onDeterminingFilename
// ---------------------------------------------------------------------------------------------------------------------
// Tarayici dosya adini belirlemeden (diske tek bayt yazmadan) olay gelir; "true" donup suggest() cagrisini erteleriz.
// Host/App ~2 sn icinde ACK verirse (disk gelen kutusuna yazildi ya da App kabul etti) indirme iptal + silinir: tarayicida hic
// gorunmez. ACK yoksa / host hata verdiyse / desteklenmeyen durumda suggest() cagrilir: indirme ASLA kaybolmaz.
// onCreated yolu yalnizca ikincil guvenlik agidir (onDeterminingFilename hic gelmezse).
// =====================================================================================================================
export const takeover = { ackMs: 2000, safetyNetMs: 4000, uiHoldMs: 10000 };

const determined = new Set();   // onDeterminingFilename'in gordugu tarayici indirme kimlikleri
const legacy = new Set();       // eski (onCreated) yolun ele aldiklari
const remember = (set, id) => { set.add(id); if (set.size > 500) set.delete(set.values().next().value); };

// Form POST ile baslayan indirmeler: downloads API yontemi bildirmez; webRequest ile POST adresleri kisa sure hatirlanir.
const postUrls = new Map();
export function notePost(url) {
  postUrls.set(url, Date.now());
  if (postUrls.size > 100) postUrls.delete(postUrls.keys().next().value);
}
const isPost = (url) => { const t = postUrls.get(url); return t !== undefined && Date.now() - t < 60000; };

// ---- indirme arayuzu (balon/raf): devralma etkinken gizle, yedege dusulurken geri ac ----
let uiHidden = false;
let uiHoldUntil = 0;
let uiTimer = null;
export const isUiHidden = () => uiHidden;
export async function syncUi() {
  if (!chrome.downloads?.setUiOptions) return;                         // ozellik algilama (Edge/eski surum)
  try {
    await settings.ready;
    const want = settings.effective().interceptEnabled && native.isConnected() && Date.now() >= uiHoldUntil;
    if (want === uiHidden) return;
    await chrome.downloads.setUiOptions({ enabled: !want });
    uiHidden = want;
  } catch { /* downloads.ui izni yok / baska eklenti yonetiyor: sessizce gec */ }
}
async function holdUi() {
  uiHoldUntil = Date.now() + takeover.uiHoldMs;
  await syncUi();                                                      // yedek indirme normal gorunsun
  if (uiTimer) clearTimeout(uiTimer);
  uiTimer = setTimeout(() => { uiTimer = null; syncUi(); }, takeover.uiHoldMs + 50);
}
native.onStatus(() => { syncUi(); });

function skipReason(item, url) {
  if (!url || NON_HTTP.test(url)) return "scheme";                     // blob:, data:, file:, chrome: ...
  if (!/^https?:/i.test(url)) return "scheme";
  if (item.incognito) return "incognito";
  if (item.byExtensionId && item.byExtensionId === chrome.runtime?.id) return "own";   // kendi yedek indirmelerimiz
  if (bypass.delete(item.url)) return "bypass";
  if (isAltActive()) return "alt";
  if (isPost(item.url) || isPost(url)) return "post";
  return "";
}

// chrome.downloads.onDeterminingFilename dinleyicisi: true dondurur (suggest asenkron cagrilir).
export function onDeterminingFilename(item, suggest) {
  remember(determined, item.id);
  let done = false;
  const pass = () => { if (done) return; done = true; try { suggest(); } catch { /* yut */ } };
  // TEK cikis: devralmadigimiz HER indirme (skipReason, legacy, kapali, site duraklat, kural disi, host yok, red, istisna)
  // buradan gecer; tarayici balonu/rafi once geri acilir, sonra suggest() cagrilir. Hicbir dal holdUi'yi unutamaz.
  const release = async () => { try { await holdUi(); } catch { /* UI API yok: yut */ } pass(); };
  (async () => {
    try {
      const url = item.finalUrl || item.url || "";
      if (skipReason(item, url)) { await release(); return; }
      if (legacy.has(item.id)) { await release(); return; }                     // yavas basliklarda eski yol onceden devraldi
      await settings.ready;
      const eff = settings.effective();
      if (!eff.interceptEnabled) { await release(); return; }
      if (settings.isHostPaused(hostOf(item.referrer || ""), hostOf(url))) { await release(); return; }   // "bu sitede duraklat"
      const filename = filenameFrom(item);
      const fileSize = item.fileSize > 0 ? item.fileSize : item.totalBytes > 0 ? item.totalBytes : -1;
      const d = { url, referrer: item.referrer || "", filename, fileSize };
      if (!decideCapture(eff, d).capture) { await release(); return; }          // tur/boyut/site kurali: tarayici indirsin
      if (!native.isConnected()) { native.connect(); if (!native.isConnected()) { await release(); return; } }

      const accepted = await sendDownload({ ...d, mime: item.mime || "", browserId: item.id, timeoutMs: takeover.ackMs });
      if (accepted) {
        done = true;                                                   // suggest() cagrilmaz: indirme iptal edilir
        await safe(() => chrome.downloads.cancel(item.id));
        await safe(() => chrome.downloads.erase({ id: item.id }));     // tarayici listesinde/balonunda iz kalmasin
      } else {
        await release();                                               // yedek indirme gorunur olsun
      }
    } catch {
      await release();
    }
  })();
  return true;
}

// Ikincil guvenlik agi: onDeterminingFilename safetyNetMs icinde hic gelmediyse eski (onCreated) yolu calistir.
export function onCreatedSafetyNet(item) {
  setTimeout(() => {
    if (!determined.has(item.id)) onDownloadCreated(item);
  }, takeover.safetyNetMs);
}
