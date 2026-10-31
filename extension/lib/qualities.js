// Bekleyen kalite istekleri: menü kapanınca / sekme değişince / sekme kapanınca App'e `qualitiesCancel` gider
// (App yt-dlp sürecini öldürür). Düğme tıklanınca istek kimliği buradan izlenir.
import * as native from "./native.js";

const active = new Map();   // requestId -> tabId
const ID_OK = /^[A-Za-z0-9_-]{4,48}$/;

export const newId = (hint) =>
  typeof hint === "string" && ID_OK.test(hint) ? hint : `q${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;

export function track(requestId, tabId) {
  active.set(requestId, tabId);
  while (active.size > 50) active.delete(active.keys().next().value);   // üst sınır
}
export const done = (requestId) => { active.delete(requestId); };
export const pending = () => active.size;

// Koşula uyan bekleyen istekleri iptal eder; iptal edilen kimliklerin listesini döndürür.
export function cancelWhere(pred) {
  const out = [];
  for (const [id, tabId] of [...active]) {
    if (!pred(tabId)) continue;
    active.delete(id); out.push(id);
    native.send({ type: "qualitiesCancel", requestId: id });
  }
  return out;
}
export function cancel(requestId) {
  if (!active.delete(requestId)) return false;
  native.send({ type: "qualitiesCancel", requestId });
  return true;
}
