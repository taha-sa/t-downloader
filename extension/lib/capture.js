// İndirme yakalama: duraklat -> App'e sor -> kabulse iptal+sil, değilse devam.
import * as native from "./native.js";
import * as settings from "./settings.js";
import { buildDownload, decideCapture, filenameFrom } from "./protocol.js";

export const opts = { timeoutMs: 5000 };
const NON_HTTP = /^(blob|data|file|filesystem|about|chrome|edge):/i;

// Alt tuşu durumu (content script bildirir).
let altUntil = 0;
export const setAlt = (ms) => { altUntil = ms > 0 ? Date.now() + ms : 0; };
export const isAltActive = () => Date.now() < altUntil;

// Kendi eklentimizin başlattığı (yedek) indirmeler yakalanmaz.
const bypass = new Set();
export const addBypass = (url) => bypass.add(url);

let seq = 0;
const newId = () => `${Date.now().toString(36)}-${(++seq).toString(36)}`;

export async function cookieHeader(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch { return ""; }
}

// Ortak gönderim: download mesajını kurar, yanıtı bekler. true = App kabul etti.
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
  });
  const res = await native.request(
    msg,
    (m) => m.type === "downloadResult" && m.requestId === requestId,
    opts.timeoutMs,
  );
  return !!(res && res.accepted === true);
}

const safe = async (fn) => { try { await fn(); } catch { /* indirme zaten bitmiş olabilir */ } };

export async function onDownloadCreated(item) {
  const url = item.finalUrl || item.url || "";
  if (!url || NON_HTTP.test(url)) return;                 // blob:/data: -> tarayıcıda kalsın
  if (bypass.delete(item.url)) return;
  if (isAltActive()) return;                              // Alt basılı -> tarayıcıda kalsın
  await settings.ready;
  if (!settings.effective().interceptEnabled) return;
  if (!native.isConnected()) { native.connect(); if (!native.isConnected()) return; }

  await safe(() => chrome.downloads.pause(item.id));
  let accepted = false;
  try {
    const filename = filenameFrom(item);
    const fileSize = item.fileSize > 0 ? item.fileSize : item.totalBytes > 0 ? item.totalBytes : -1;
    const d = { url, referrer: item.referrer || "", filename, fileSize };
    if (decideCapture(settings.effective(), d).capture) {
      accepted = await sendDownload({ ...d, mime: item.mime || "" });
    }
  } catch { accepted = false; }

  if (accepted) {
    await safe(() => chrome.downloads.cancel(item.id));
    await safe(() => chrome.downloads.erase({ id: item.id }));
  } else {
    await safe(() => chrome.downloads.resume(item.id));
  }
}
