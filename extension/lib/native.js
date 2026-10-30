// Native messaging bağlantısı: yeniden bağlanma (üstel bekleme), istek/yanıt eşleme.
import { HOST_NAME } from "./protocol.js";

const MIN_BACKOFF = 1000;
const MAX_BACKOFF = 5 * 60 * 1000;

let port = null;
let backoff = MIN_BACKOFF;
let timer = null;
const listeners = new Set(); // (msg) => void
const pending = new Set();   // { match, resolve, timer }

export const isConnected = () => port !== null;
export const onHostMessage = (fn) => listeners.add(fn);

export function connect() {
  if (port) return;
  let p;
  try {
    p = chrome.runtime.connectNative(HOST_NAME);
  } catch {
    scheduleReconnect();
    return;
  }
  port = p;
  p.onMessage.addListener((msg) => {
    backoff = MIN_BACKOFF; // canlı host: bekleme sıfırlanır
    for (const w of [...pending]) {
      if (w.match(msg)) { clearTimeout(w.timer); pending.delete(w); w.resolve(msg); }
    }
    for (const fn of listeners) { try { fn(msg); } catch { /* yut */ } }
  });
  p.onDisconnect.addListener(() => {
    void chrome.runtime.lastError; // host yoksa sessiz geç
    if (port === p) port = null;
    for (const w of [...pending]) { clearTimeout(w.timer); pending.delete(w); w.resolve(null); }
    scheduleReconnect();
  });
}

function scheduleReconnect() {
  if (timer) return;
  timer = setTimeout(() => { timer = null; connect(); }, backoff);
  backoff = Math.min(backoff * 2, MAX_BACKOFF);
}

export function send(msg) {
  if (!port) connect();
  if (!port) return false;
  try { port.postMessage(msg); return true; } catch { return false; }
}

// Yanıt bekleyen gönderim; zaman aşımı/host yok -> null.
export function request(msg, match, timeoutMs = 5000) {
  return new Promise((resolve) => {
    if (!port) { connect(); }
    if (!port) { resolve(null); return; }
    const w = { match, resolve, timer: null };
    w.timer = setTimeout(() => { pending.delete(w); resolve(null); }, timeoutMs);
    pending.add(w);
    if (!send(msg)) { clearTimeout(w.timer); pending.delete(w); resolve(null); }
  });
}

// Testler için sıfırlama.
export function _reset() {
  port = null; backoff = MIN_BACKOFF;
  if (timer) { clearTimeout(timer); timer = null; }
  pending.clear(); listeners.clear();
}
