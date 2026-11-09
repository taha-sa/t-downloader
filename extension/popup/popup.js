// Popup: bağlantı durumu (ping/pong) ve yakalama anahtarı.
const statusEl = document.getElementById("status");
const toggle = document.getElementById("capture");
const hint = document.getElementById("hint");

function render(s) {
  toggle.checked = !!s.captureEnabled;
  if (s.hostOk) {
    statusEl.textContent = "Bağlı" + (s.version ? ` (sürüm ${s.version})` : "");
    statusEl.className = "status ok";
    hint.textContent = "";
  } else {
    statusEl.textContent = "Uygulamaya bağlı değil";
    statusEl.className = "status bad";
    hint.textContent = "T-Downloader kurulu/çalışır değilse indirmeler tarayıcıda kalır." + (s.error ? ` (Hata: ${s.error})` : "");
  }
}

chrome.runtime.sendMessage({ kind: "getStatus" }, (s) => {
  void chrome.runtime.lastError;
  render(s || { hostOk: false, captureEnabled: true, error: "eklenti arka planı yanıt vermedi" });
});

toggle.addEventListener("change", () => {
  chrome.runtime.sendMessage({ kind: "setCapture", value: toggle.checked }, () => {
    void chrome.runtime.lastError;
  });
});

// "Bu sitede duraklat": etkin sekmenin sitesinde indirmeler tarayıcıda kalır.
const siteRow = document.getElementById("siteRow");
const sitePause = document.getElementById("sitePause");
const siteLabel = document.getElementById("siteLabel");
let siteHost = "";
try {
  chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
    void chrome.runtime.lastError;
    try {
      const u = new URL(tabs?.[0]?.url || "");
      if (!/^https?:$/.test(u.protocol)) return;
      siteHost = u.hostname.replace(/^www\./, "");
    } catch { return; }
    siteLabel.textContent = `Bu sitede duraklat (${siteHost})`;
    siteRow.hidden = false;
    chrome.runtime.sendMessage({ kind: "getSitePause", host: siteHost }, (r) => {
      void chrome.runtime.lastError;
      sitePause.checked = !!r?.paused;
    });
  });
} catch { /* test ortamı */ }
sitePause.addEventListener("change", () => {
  chrome.runtime.sendMessage({ kind: "setSitePause", host: siteHost, value: sitePause.checked }, () => void chrome.runtime.lastError);
});
