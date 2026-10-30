// T-Downloader service worker: bağlantıları kurar, olayları ilgili modüllere yönlendirir.
import * as native from "./lib/native.js";
import * as settings from "./lib/settings.js";
import * as capture from "./lib/capture.js";
import * as media from "./lib/media.js";
import * as geometry from "./lib/geometry.js";
import * as menus from "./lib/menus.js";

let hostVersion = null;

// ---- App -> eklenti ----
native.onHostMessage((msg) => {
  if (msg.type === "settings") settings.applyAppSettings(msg);
  else if (msg.type === "hideButton" && Number.isInteger(msg.tabId)) geometry.hideTab(msg.tabId);
  else if (msg.type === "pong") hostVersion = msg.version || null;
});

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
