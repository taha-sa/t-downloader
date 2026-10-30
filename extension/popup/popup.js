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
