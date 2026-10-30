// content script'ten gelen geometriyi sekme/pencere bilgisiyle zenginleştirip host'a iletir.
import * as native from "./native.js";
import { buildVideoGeometry } from "./protocol.js";

const frames = new Map();   // tabId -> Map(frameId -> payload)
const hidden = new Set();   // App `hideButton` dediği sekmeler
const last = new Map();     // tabId -> { json, windowId }

export function hideTab(tabId) {
  hidden.add(tabId);
  frames.delete(tabId);
  last.delete(tabId);
}

export function resetTab(tabId) {
  hidden.delete(tabId);
  frames.delete(tabId);
  last.delete(tabId);
}

// Karelerden en büyük görünür olanı seç.
function best(tabId) {
  const m = frames.get(tabId);
  if (!m) return null;
  let b = null;
  for (const p of m.values()) {
    if (p.visible && (!b || p.area > b.area)) b = p;
  }
  return b || [...m.values()].pop() || null;
}

async function windowRect(windowId) {
  try {
    const w = await chrome.windows.get(windowId);
    return { rect: { left: w.left, top: w.top, width: w.width, height: w.height }, state: w.state };
  } catch { return { rect: null, state: "" }; }
}

export async function emit(tabId, windowId, tabInfo = {}) {
  if (hidden.has(tabId)) return;
  const p = best(tabId);
  if (!p) return;
  const w = await windowRect(windowId);
  const msg = buildVideoGeometry({
    tabId, windowId,
    visible: p.visible,
    fullscreen: p.fullscreen || w.state === "fullscreen",
    rect: p.rect,
    devicePixelRatio: p.devicePixelRatio,
    viewport: p.viewport,
    browserWindow: w.rect,
    pageUrl: tabInfo.url || p.pageUrl,
    pageTitle: tabInfo.title || p.pageTitle,
  });
  const json = JSON.stringify(msg);
  if (last.get(tabId)?.json === json) return;
  last.set(tabId, { json, windowId });
  native.send(msg);
}

export async function onContentGeometry(payload, sender) {
  const tab = sender.tab;
  if (!tab || tab.id == null || hidden.has(tab.id)) return;
  let m = frames.get(tab.id);
  if (!m) { m = new Map(); frames.set(tab.id, m); }
  m.set(sender.frameId ?? 0, payload);
  await emit(tab.id, tab.windowId, tab);
}

// Pencere taşınınca/boyanınca konumu yeniden gönder.
export async function resendWindow(windowId) {
  for (const [tabId, info] of last) {
    if (info.windowId === windowId) { last.delete(tabId); await emit(tabId, windowId); }
  }
}

// Başka sekmeye geçilince eskileri gizle, yeni sekmeyi yeniden gönder.
export async function onTabActivated(windowId, activeTabId) {
  for (const [tabId, info] of [...last]) {
    if (info.windowId === windowId && tabId !== activeTabId) {
      const m = frames.get(tabId);
      if (m) for (const p of m.values()) p.visible = false;
      await emit(tabId, windowId);
    }
  }
  const m = frames.get(activeTabId);
  if (m) { last.delete(activeTabId); await emit(activeTabId, windowId); }
}
