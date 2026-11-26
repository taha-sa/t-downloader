// Ayarlar: App'ten gelen `settings` + kullanıcının yerel aç/kapat anahtarı.
import { DEFAULT_SETTINGS } from "./protocol.js";

let appSettings = { ...DEFAULT_SETTINGS };
let userCapture = true; // popup anahtarı
let pausedHosts = []; // "Bu sitede duraklat": bu sitelerde indirmeler tarayıcıda kalır
export const ready = load();

async function load() {
  try {
    const s = await chrome.storage.local.get(["appSettings", "userCapture", "pausedHosts"]);
    if (s.appSettings) appSettings = { ...DEFAULT_SETTINGS, ...s.appSettings };
    if (typeof s.userCapture === "boolean") userCapture = s.userCapture;
    if (Array.isArray(s.pausedHosts)) pausedHosts = s.pausedHosts.filter((h) => typeof h === "string");
  } catch { /* varsayılanlarla devam */ }
}

export function applyAppSettings(msg) {
  appSettings = {
    ...appSettings,
    language: typeof msg.language === "string" ? msg.language : appSettings.language,
    interceptEnabled: msg.interceptEnabled ?? appSettings.interceptEnabled,
    minFileSizeKb: Number.isFinite(msg.minFileSizeKb) ? msg.minFileSizeKb : appSettings.minFileSizeKb,
    excludedSites: Array.isArray(msg.excludedSites) ? msg.excludedSites : appSettings.excludedSites,
    fileTypes: Array.isArray(msg.fileTypes) ? msg.fileTypes : appSettings.fileTypes,
    showVideoButton: typeof msg.showVideoButton === "boolean" ? msg.showVideoButton : appSettings.showVideoButton,
    nativeButton: typeof msg.nativeButton === "boolean" ? msg.nativeButton : appSettings.nativeButton,
  };
  chrome.storage.local.set({ appSettings }).catch?.(() => {});
}

export function setUserCapture(v) {
  userCapture = !!v;
  chrome.storage.local.set({ userCapture }).catch?.(() => {});
}

export const getUserCapture = () => userCapture;

// ---- site bazli duraklatma ----
const norm = (h) => String(h || "").toLowerCase().replace(/^www\./, "");
export const hostMatches = (paused, host) => { const p = norm(paused), h = norm(host); return !!p && !!h && (h === p || h.endsWith("." + p)); };
export const isHostPaused = (...hosts) => hosts.some((h) => h && pausedHosts.some((p) => hostMatches(p, h)));
export const getPausedHosts = () => [...pausedHosts];
export function setHostPaused(host, paused) {
  const h = norm(host);
  if (!h) return;
  pausedHosts = pausedHosts.filter((p) => norm(p) !== h);
  if (paused) pausedHosts.push(h);
  chrome.storage.local.set({ pausedHosts }).catch?.(() => {});
}

// Etkin ayar: App açık demiyorsa veya kullanıcı kapattıysa yakalama yok.
export function effective() {
  return { ...appSettings, interceptEnabled: appSettings.interceptEnabled && userCapture };
}

// Sayfa içi düğme ve geometri için arayüz ayarları.
export const uiConfig = () => ({ ...(appSettings.language ? { language: appSettings.language } : {}), showVideoButton: appSettings.showVideoButton !== false, nativeButton: appSettings.nativeButton === true });

