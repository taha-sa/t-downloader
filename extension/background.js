import "./localization.js";
const L = globalThis.TDownloaderLocale;
// T-Downloader service worker: bağlantıları kurar, olayları ilgili modüllere yönlendirir.
import * as native from "./lib/native.js";
import * as settings from "./lib/settings.js";
import * as capture from "./lib/capture.js";
import * as media from "./lib/media.js";
import * as geometry from "./lib/geometry.js";
import * as menus from "./lib/menus.js";
import * as qlist from "./lib/qualities.js";
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
  if (msg.type === "settings") {
    L.set(msg.language); menus.createMenus(); settings.applyAppSettings(msg); broadcastCfg(); capture.syncUi();
    media.resendAll();      // App yeni bağlandığında ilk mesaj budur: açık sayfaların medya listesini yeniden gönder (A8)
  }
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
// Birincil: dosya adi belirlenirken (diske yazmadan) devral. Ikincil: onCreated guvenlik agi (4 sn icinde ilki gelmezse).
chrome.downloads.onDeterminingFilename.addListener((item, suggest) => capture.onDeterminingFilename(item, suggest));
chrome.downloads.onCreated.addListener((item) => { capture.onCreatedSafetyNet(item); });
// POST ile baslayan istekleri hatirla (downloads API yontemi bildirmez; POST'u App yeniden gonderemez -> tarayici indirsin).
try {
  chrome.webRequest.onBeforeRequest.addListener((d) => { if (d.method === "POST") capture.notePost(d.url); },
    { urls: ["<all_urls>"], types: ["main_frame", "sub_frame"] });
} catch { /* test ortami */ }
capture.syncUi();
media.register();
chrome.runtime.onInstalled.addListener(menus.createMenus);
chrome.runtime.onStartup.addListener(menus.createMenus);
chrome.contextMenus.onClicked.addListener((info, tab) => { menus.onMenuClick(info, tab); });

// status "loading" = ana çerçevede yeni belge (adres değişmese de: F5). Adres değişimi tek başına (pushState) sıfırlamaz.
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading") { qlist.cancelWhere((t) => t === tabId); media.navigated(tabId); geometry.resetTab(tabId); }
});
chrome.tabs.onRemoved.addListener((tabId) => { qlist.cancelWhere((t) => t === tabId); media.closeTab(tabId); geometry.resetTab(tabId); });
chrome.tabs.onActivated.addListener(({ tabId, windowId }) => { qlist.cancelWhere((t) => t !== tabId); geometry.onTabActivated(windowId, tabId); });
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
        const requestId = qlist.newId(msg.rid);
        qlist.track(requestId, tab.id);     // await'ten ÖNCE: menü hemen kapanırsa qualitiesCancel bu kimlikle gidebilir (A15)
        const [players, cookies] = await Promise.all([probePlayers(tab.id, sender.frameId), media.cookiesForTab(tab.id)]);
        const res = await native.request(buildQualities({
          requestId, tabId: tab.id, frameId: sender.frameId ?? 0, pageUrl: msg.pageUrl || tab.url, pageTitle: tab.title,
          frameUrl: sender.url, src: msg.src, players, cookies,
        }), (m) => m.type === "qualitiesResult" && m.requestId === requestId, 100000);
        qlist.done(requestId);
        sendResponse(res ? { ...res } : { ok: false, errorCode: "silent", error: L.t("ExtNoReply") });
      })();
      return true;
    case "qualitiesCancel":
      qlist.cancel(msg.rid);
      return false;
    case "qualitySelect":
      sendResponse({ ok: native.send(buildQualitySelect(msg)) });   // gönderilemediyse düğme hata gösterir (A8)
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
          language: settings.uiConfig().language,
          error: native.getLastError(),
        });
      })();
      return true; // asenkron yanıt
    case "setCapture":
      settings.setUserCapture(msg.value);
      capture.syncUi();
      sendResponse({ captureEnabled: settings.getUserCapture() });
      return false;
    case "getSitePause":
      (async () => { await settings.ready; sendResponse({ paused: settings.isHostPaused(msg.host) }); })();
      return true;
    case "setSitePause":
      settings.setHostPaused(msg.host, !!msg.value);
      sendResponse({ paused: settings.isHostPaused(msg.host) });
      return false;
  }
  return false;
});

settings.ready.then(() => { L.set(settings.uiConfig().language); menus.createMenus(); });
native.connect();
