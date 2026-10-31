// T-Downloader service worker: bağlantıları kurar, olayları ilgili modüllere yönlendirir.
import * as native from "./lib/native.js";
import * as settings from "./lib/settings.js";
import * as capture from "./lib/capture.js";
import * as media from "./lib/media.js";
import * as geometry from "./lib/geometry.js";
import * as menus from "./lib/menus.js";
import { buildQualities, buildQualitySelect } from "./lib/protocol.js";
import { readPlayers } from "./lib/players.js";

// Yalnızca düğme tıklanınca: o çerçevedeki oynatıcı yapılandırmasını (jwplayer/video.js/HLS.js/Playerjs/<source>) OKUR.
async function probePlayers(tabId, frameId) {
  try {
    if (!chrome.scripting?.executeScript || tabId == null) return [];
    const run = chrome.scripting.executeScript({ target: { tabId, frameIds: [frameId ?? 0] }, world: "MAIN", func: readPlayers });
    const res = await Promise.race([run, new Promise((r) => setTimeout(() => r(null), 1500))]);
    const list = res && res[0] && res[0].result;
    return Array.isArray(list) ? list : [];
  } catch { return []; }                                  // kısıtlı sayfa / çerçeve kapandı
}

let hostVersion = null;

// ---- App -> eklenti ----
native.onHostMessage((msg) => {
  if (msg.type === "settings") { settings.applyAppSettings(msg); broadcastCfg(); }
  else if (msg.type === "hideButton" && Number.isInteger(msg.tabId)) geometry.hideTab(msg.tabId);
  else if (msg.type === "pong") hostVersion = msg.version || null;
});

// Arayüz ayarı değişince tüm çerçevelere haber ver (sayfa içi düğme / geometri).
function broadcastCfg() {
  try {
    chrome.tabs.query({}, (tabs) => {
      for (const t of tabs || []) chrome.tabs.sendMessage(t.id, { kind: "cfg", ...settings.uiConfig() }, () => void chrome.runtime.lastError);
    });
  } catch { /* test ortamı */ }
}

// ---- olay kayıtları (senkron, üst düzeyde) ----
chrome.downloads.onCreated.addListener((item) => { capture.onDownloadCreated(item); });
media.register();
chrome.runtime.onInstalled.addListener(menus.createMenus);
chrome.runtime.onStartup.addListener(menus.createMenus);
chrome.contextMenus.onClicked.addListener((info, tab) => { menus.onMenuClick(info, tab); });

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading" && change.url) { media.clearTab(tabId); geometry.resetTab(tabId); }
});
chrome.tabs.onRemoved.addListener((tabId) => { media.clearTab(tabId); geometry.resetTab(tabId); });
chrome.tabs.onActivated.addListener(({ tabId, windowId }) => { geometry.onTabActivated(windowId, tabId); });
chrome.windows.onBoundsChanged?.addListener((w) => { geometry.resendWindow(w.id); });

// ---- content script ve popup mesajları ----
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg) return false;
  switch (msg.kind) {
    case "geometry":
      geometry.onContentGeometry(msg.payload, sender);
      return false;
    case "cfg":
      sendResponse(settings.uiConfig());
      return false;
    case "qualities":
      (async () => {
        const tab = sender.tab || {};
        native.connect();
        const players = await probePlayers(tab.id, sender.frameId);
        const requestId = `q${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
        const res = await native.request(buildQualities({
          requestId, tabId: tab.id, frameId: sender.frameId ?? 0, pageUrl: tab.url, pageTitle: tab.title,
          frameUrl: sender.url, src: msg.src, players,
        }), (m) => m.type === "qualitiesResult" && m.requestId === requestId, 100000);
        sendResponse(res ? { ...res } : { ok: false, errorCode: "silent", error: "T-Downloader yanıt vermedi. Uygulama çalışıyor mu?" });
      })();
      return true;
    case "qualitySelect":
      native.send(buildQualitySelect(msg));
      sendResponse({ ok: true });
      return false;
    case "alt":
      capture.setAlt(msg.ms);
      return false;
    case "getStatus":
      (async () => {
        await settings.ready;
        native.connect();
        const pong = await native.request({ type: "ping" }, (m) => m.type === "pong", 2000);
        sendResponse({
          connected: native.isConnected(),
          hostOk: !!pong,
          version: pong?.version || hostVersion,
          captureEnabled: settings.getUserCapture(),
          error: native.getLastError(),
        });
      })();
      return true; // asenkron yanıt
    case "setCapture":
      settings.setUserCapture(msg.value);
      sendResponse({ captureEnabled: settings.getUserCapture() });
      return false;
  }
  return false;
});

native.connect();
