import "../localization.js";
const L = globalThis.TDownloaderLocale;
// Sağ tık menüsü (Türkçe).
import * as native from "./native.js";
import { sendDownload, addBypass } from "./capture.js";

export const BATCH = 10;
const ITEMS = () => [
  { id: "td-link", title: L.t("ExtDownload"), contexts: ["link"] },
  { id: "td-page", title: L.t("ExtAllLinks"), contexts: ["page"] },
  { id: "td-selection", title: L.t("ExtSelectedLinks"), contexts: ["selection"] },
  { id: "td-media", title: L.t("ExtVideoDownload"), contexts: ["video", "audio"] },
];

export function createMenus() {
  chrome.contextMenus.removeAll(() => {
    void chrome.runtime.lastError;
    for (const it of ITEMS()) chrome.contextMenus.create(it);
  });
}

async function queueUrl(url, referrer) {
  if (!/^https?:/i.test(url || "")) return;
  // Host yoksa sessizce tarayıcıya bırak (yedek indirme yakalanmasın diye bypass).
  if (!native.isConnected()) {
    native.connect();
    if (!native.isConnected()) { addBypass(url); try { await chrome.downloads.download({ url }); } catch { /* yut */ } return; }
  }
  const ok = await sendDownload({ url, referrer, explicit: true });
  if (!ok) { addBypass(url); try { await chrome.downloads.download({ url }); } catch { /* yut */ } }
}

async function collectLinks(tabId, frameId, selectionOnly) {
  try {
    const r = await chrome.tabs.sendMessage(tabId, { kind: "collectLinks", selectionOnly }, { frameId });
    return r?.urls || [];
  } catch { return []; }
}

export async function onMenuClick(info, tab) {
  const referrer = info.pageUrl || tab?.url || "";
  switch (info.menuItemId) {
    case "td-link": return queueUrl(info.linkUrl, referrer);
    case "td-media": return queueUrl(info.srcUrl, referrer);
    case "td-page":
    case "td-selection": {
      if (!tab?.id) return;
      const sel = info.menuItemId === "td-selection";
      const urls = await collectLinks(tab.id, sel ? info.frameId ?? 0 : 0, sel);
      // Seri bekleme yok: App yanıtı onay penceresini beklemez; yine de en fazla BATCH istek eşzamanlı gider.
      for (let i = 0; i < urls.length; i += BATCH) await Promise.all(urls.slice(i, i + BATCH).map((u) => queueUrl(u, referrer)));
    }
  }
}
