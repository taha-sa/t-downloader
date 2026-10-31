// İndirme yakalama: duraklat -> App'e sor -> kabulse iptal+sil, değilse devam.
import * as native from "./native.js";
import * as settings from "./settings.js";
import { buildDownload, decideCapture, filenameFrom } from "./protocol.js";

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
export const watchedCount = () => lateWatch.size;
native.onHostMessage((m) => {
  if (m.type !== "downloadResult" || typeof m.requestId !== "string") return;
  const id = lateWatch.get(m.requestId);
  if (id === undefined) return;
  lateWatch.delete(m.requestId);
  if (m.accepted === true && id !== null) {
    safe(() => chrome.downloads.cancel(id)).then(() => safe(() => chrome.downloads.erase({ id })));
  }
});

// Ortak gönderim: download mesajını kurar, yanıtı bekler. true = App kabul etti.
// info.browserId: tarayıcı indirme kimliği (zaman aşımında geç kabul için izlenir); info.explicit: kullanıcı eylemi.
export async function sendDownload(info) {
  const requestId = newId();
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
    opts.timeoutMs,
  );
  if (!res && info.browserId !== undefined) {
    lateWatch.set(requestId, info.browserId);
    while (lateWatch.size > LATE_MAX) lateWatch.delete(lateWatch.keys().next().value);
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
  if (!url || NON_HTTP.test(url)) return;                 // blob:/data: -> tarayıcıda kalsın
  if (!isFreshDownload(item)) return;                     // geçmişten gelen/tekrarlanan kayıt
  if (bypass.delete(item.url)) return;
  if (isAltActive()) return;                              // Alt basılı -> tarayıcıda kalsın
  await settings.ready;
  if (!settings.effective().interceptEnabled) return;
  if (!native.isConnected()) { native.connect(); if (!native.isConnected()) return; }

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
