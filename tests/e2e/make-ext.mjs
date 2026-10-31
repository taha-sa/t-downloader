// Uçtan uca test için eklenti kopyası: yalnızca native messaging taşıması köprüye (site.mjs) yönlendirilir.
// Gerçek registry/host'a dokunulmaz; geri kalan tüm eklenti kodu (content.js, media.js, geometry.js...) aynıdır.
//   node make-ext.mjs <çalışmaKlasörü>
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const work = path.resolve(process.argv[2] || "e2e-work");
const ports = JSON.parse(fs.readFileSync(path.join(work, "ports.json"), "utf8"));
const src = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "extension");
const dst = path.join(work, "ext");
fs.rmSync(dst, { recursive: true, force: true });
fs.cpSync(src, dst, { recursive: true, filter: (p) => !p.includes("tests") && !p.includes("node_modules") });

const shim = `
const __BR = "http://127.0.0.1:${ports.bridge}";
function shimConnect() {
  const ml = [], dl = []; let closed = false;
  const port = {
    onMessage: { addListener: (f) => ml.push(f) }, onDisconnect: { addListener: (f) => dl.push(f) },
    postMessage: (m) => { fetch(__BR + "/msg", { method: "POST", body: JSON.stringify(m) }).catch(() => {}); },
  };
  (async () => { while (!closed) { try { const r = await fetch(__BR + "/poll"); for (const m of await r.json()) ml.forEach((f) => f(m)); } catch { await new Promise((r) => setTimeout(r, 500)); } } })();
  return port;
}
`;
const np = path.join(dst, "lib", "native.js");
let s = fs.readFileSync(np, "utf8");
if (!s.includes("chrome.runtime.connectNative(HOST_NAME)")) throw new Error("native.js beklenen biçimde değil");
s = s.replace("chrome.runtime.connectNative(HOST_NAME)", "shimConnect(HOST_NAME)") + shim;
fs.writeFileSync(np, s);
console.log("eklenti kopyası:", dst);
