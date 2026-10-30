// Ayarlar: App'ten gelen `settings` + kullanıcının yerel aç/kapat anahtarı.
import { DEFAULT_SETTINGS } from "./protocol.js";

let appSettings = { ...DEFAULT_SETTINGS };
let userCapture = true; // popup anahtarı
export const ready = load();

async function load() {
  try {
    const s = await chrome.storage.local.get(["appSettings", "userCapture"]);
    if (s.appSettings) appSettings = { ...DEFAULT_SETTINGS, ...s.appSettings };
    if (typeof s.userCapture === "boolean") userCapture = s.userCapture;
  } catch { /* varsayılanlarla devam */ }
}

export function applyAppSettings(msg) {
  appSettings = {
    ...appSettings,
    interceptEnabled: msg.interceptEnabled ?? appSettings.interceptEnabled,
    minFileSizeKb: Number.isFinite(msg.minFileSizeKb) ? msg.minFileSizeKb : appSettings.minFileSizeKb,
    excludedSites: Array.isArray(msg.excludedSites) ? msg.excludedSites : appSettings.excludedSites,
    fileTypes: Array.isArray(msg.fileTypes) ? msg.fileTypes : appSettings.fileTypes,
  };
  chrome.storage.local.set({ appSettings }).catch?.(() => {});
}

export function setUserCapture(v) {
  userCapture = !!v;
  chrome.storage.local.set({ userCapture }).catch?.(() => {});
}

export const getUserCapture = () => userCapture;

// Etkin ayar: App açık demiyorsa veya kullanıcı kapattıysa yakalama yok.
export function effective() {
  return { ...appSettings, interceptEnabled: appSettings.interceptEnabled && userCapture };
}
