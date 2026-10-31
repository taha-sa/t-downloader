// Mesaj üreticileri ve saf yardımcılar (docs/CONTRACT.md ile birebir).
export const HOST_NAME = "com.tdownloader.host";

export const DEFAULT_SETTINGS = {
  interceptEnabled: true,
  showVideoButton: true,   // sayfa içi "T-Downloader ile İndir" düğmesi
  nativeButton: false,     // eski yerel düğme penceresi (videoGeometry); varsayılan kapalı
  minFileSizeKb: 0,
  excludedSites: [],
  // Boş liste = tüm türler. Varsayılan: yaygın arşiv/program/belge/medya.
  fileTypes: [
    "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "img",
    "exe", "msi", "msix", "apk", "dmg", "deb", "rpm",
    "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx",
    "mp3", "flac", "wav", "aac", "m4a", "ogg",
    "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg",
  ],
};

const str = (v) => (typeof v === "string" ? v : "");

export function buildDownload(p) {
  return {
    type: "download",
    requestId: str(p.requestId),
    url: str(p.url),
    referrer: str(p.referrer),
    cookies: str(p.cookies),
    userAgent: str(p.userAgent),
    filename: str(p.filename),
    mime: str(p.mime),
    fileSize: Number.isFinite(p.fileSize) ? p.fileSize : -1,
    explicit: p.explicit === true,   // sağ tık menüsü gibi kullanıcı eylemi: App "yakalama kapalı" ayarını yok sayar
  };
}

export function buildMedia(p) {
  return {
    type: "media",
    tabId: p.tabId,
    frameId: p.frameId ?? 0,
    pageUrl: str(p.pageUrl),
    pageTitle: str(p.pageTitle),
    frameUrl: str(p.frameUrl),
    // referer/origin/cookies/userAgent: iframe oynatıcıdan gelen akış için istek başlıkları (CONTRACT.md, isteğe bağlı alanlar)
    items: (p.items || []).map((i) => ({
      url: i.url, mime: str(i.mime), kind: i.kind,
      referer: str(i.referer), origin: str(i.origin), cookies: str(i.cookies), userAgent: str(i.userAgent),
      size: Number.isFinite(i.size) ? i.size : -1,        // bilinen toplam bayt (Content-Range/Content-Length), yoksa -1
    })),
  };
}

export function buildQualities(p) {
  return {
    type: "qualities",
    requestId: str(p.requestId),
    tabId: p.tabId,
    frameId: p.frameId ?? 0,
    pageUrl: str(p.pageUrl),
    pageTitle: str(p.pageTitle),
    frameUrl: str(p.frameUrl),
    src: str(p.src),
    // Düğme tıklanınca oynatıcı yapılandırmasından okunan kaynaklar: [{ url, label, height }] (en fazla 40)
    players: (Array.isArray(p.players) ? p.players : [])
      .filter((x) => x && typeof x.url === "string" && x.url.length <= 2000).slice(0, 40)
      .map((x) => ({ url: x.url, label: str(x.label).slice(0, 40), height: Number.isFinite(x.height) ? Math.max(0, Math.round(x.height)) : 0 })),
  };
}

export function buildQualitySelect(p) {
  return { type: "qualitySelect", selectionId: str(p.selectionId), index: Number.isInteger(p.index) ? p.index : -1 };
}

export function buildVideoGeometry(p) {
  const r = p.rect || { left: 0, top: 0, right: 0, bottom: 0 };
  const bw = p.browserWindow || {};
  return {
    type: "videoGeometry",
    tabId: p.tabId,
    windowId: p.windowId,
    visible: !!p.visible,
    fullscreen: !!p.fullscreen,
    rect: { left: r.left, top: r.top, right: r.right, bottom: r.bottom },
    devicePixelRatio: p.devicePixelRatio || 1,
    viewport: { width: p.viewport?.width || 0, height: p.viewport?.height || 0 },
    browserWindow: {
      left: bw.left ?? 0, top: bw.top ?? 0, width: bw.width ?? 0, height: bw.height ?? 0,
    },
    pageUrl: str(p.pageUrl),
    pageTitle: str(p.pageTitle),
  };
}

// ---- medya sınıflandırma ----
const MEDIA_EXT = /\.(mp4|m4v|webm|mkv|mov|flv|avi|mp3|m4a|aac|ogg|oga|opus|wav|flac)(?:$|[?#])/i;

// m3u8/mpd adresi yolun sonunda olmak zorunda değil (ör. /master.m3u8/index.txt, ?format=m3u8).
const HLS_URL = /\.m3u8(?:$|[/?#&])|[?&](?:format|type|ext)=m3u8(?:$|[&#])/i;
const DASH_URL = /\.mpd(?:$|[/?#&])/i;

export function originOf(url) {
  try { const u = new URL(url); return /^https?:$/.test(u.protocol) ? u.origin : ""; } catch { return ""; }
}

export function classifyMedia(url, contentType = "", contentLength = -1) {
  const path = (url || "").split(/[?#]/)[0].toLowerCase();
  const ct = (contentType || "").split(";")[0].trim().toLowerCase();
  if (HLS_URL.test(url || "") || ct === "application/vnd.apple.mpegurl" || ct === "application/x-mpegurl" || ct === "audio/mpegurl" || ct === "audio/x-mpegurl") return "hls";
  if (DASH_URL.test(url || "") || ct === "application/dash+xml") return "dash";
  const isMime = ct.startsWith("video/") || ct.startsWith("audio/");
  if (isMime || MEDIA_EXT.test(url || "")) {
    // Çok küçük yanıtlar (ses efekti, parça başlığı vb.) gürültüdür.
    if (contentLength >= 0 && contentLength < 50 * 1024) return null;
    if (ct.startsWith("video/mp2t")) return null; // HLS parçaları
    return "file";
  }
  return null;
}

// ---- ayar eşleme ----
export function hostOf(url) {
  try { return new URL(url).hostname.toLowerCase(); } catch { return ""; }
}

export function extOf(nameOrUrl) {
  const p = (nameOrUrl || "").split(/[?#]/)[0];
  const m = /\.([A-Za-z0-9]{1,6})$/.exec(p);
  return m ? m[1].toLowerCase() : "";
}

export function isExcludedSite(settings, ...urls) {
  const list = (settings.excludedSites || [])
    .map((s) => String(s).trim().toLowerCase().replace(/^\*\./, "").replace(/^www\./, ""))
    .filter(Boolean);
  return urls.some((u) => {
    const h = hostOf(u).replace(/^www\./, "");
    return h && list.some((s) => h === s || h.endsWith("." + s));
  });
}

// Yakalama kararı: { capture:boolean, reason }
export function decideCapture(settings, d) {
  if (!settings.interceptEnabled) return { capture: false, reason: "off" };
  if (isExcludedSite(settings, d.url, d.referrer)) return { capture: false, reason: "excluded" };
  if (d.fileSize >= 0 && d.fileSize < (settings.minFileSizeKb || 0) * 1024) {
    return { capture: false, reason: "small" };
  }
  const types = (settings.fileTypes || []).map((t) => String(t).toLowerCase().replace(/^\./, ""));
  if (types.length) {
    const ext = extOf(d.filename) || extOf(d.url);
    if (!types.includes(ext)) return { capture: false, reason: "type" };
  }
  return { capture: true, reason: "ok" };
}

export function filenameFrom(item) {
  const fromItem = (item.filename || "").split(/[\\/]/).pop();
  if (fromItem) return fromItem;
  try {
    const p = new URL(item.finalUrl || item.url).pathname;
    return decodeURIComponent(p.split("/").pop() || "");
  } catch { return ""; }
}
