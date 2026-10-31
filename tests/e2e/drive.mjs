// CDP ile Brave'i sürer: sayfa içi düğmeyi/menüyü doğrular. Kullanım: node drive.mjs <çalışmaKlasörü> <iframe|direct>
import fs from "node:fs";
import path from "node:path";

const work = path.resolve(process.argv[2]);
const mode = process.argv[3] || "iframe";
const ports = JSON.parse(fs.readFileSync(path.join(work, "ports.json"), "utf8"));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const list = await (await fetch("http://127.0.0.1:9333/json")).json();
const page = list.find((t) => t.type === "page" && t.url.includes("localhost:" + ports.film));
if (!page) throw new Error("sayfa yok");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const waits = new Map(); const sessions = [];
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.id && waits.has(m.id)) { waits.get(m.id)(m); waits.delete(m.id); }
  if (m.method === "Target.attachedToTarget") sessions.push(m.params);
};
const call = (method, params = {}, sessionId) => new Promise((res) => {
  const i = ++id; waits.set(i, res);
  ws.send(JSON.stringify({ id: i, method, params, ...(sessionId ? { sessionId } : {}) }));
});
const ev = async (expr, sessionId) => {
  const r = await call("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true }, sessionId);
  if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails).slice(0, 300));
  return r.result?.result?.value;
};
await call("Target.setAutoAttach", { autoAttach: true, waitForDebuggerOnStart: false, flatten: true });
await sleep(1500);
const frameSession = mode === "iframe" ? sessions.find((s) => s.targetInfo.type === "iframe")?.sessionId : undefined;
if (mode === "iframe" && !frameSession) throw new Error("iframe oturumu yok: " + JSON.stringify(sessions.map((s) => s.targetInfo.type)));

const mouse = (type, x, y, extra = {}) => call("Input.dispatchMouseEvent", { type, x, y, ...extra });
const probe = `(() => {
  const h = document.getElementById("tdownloader-overlay"); if (!h) return { host: false };
  const b = h.shadowRoot.getElementById("btn"), m = h.shadowRoot.getElementById("menu");
  const v = document.querySelector("video"); const vr = v.getBoundingClientRect(), br = b.getBoundingClientRect();
  return { host: true, btn: b.style.display, text: b.textContent.trim(), dx: Math.round(vr.right - br.right), dy: Math.round(br.top - vr.top),
    menu: m.style.display, menuText: m.innerText, rows: [...m.querySelectorAll(".row")].map(r => r.innerText.replace(/\\n/g, " | ")), vr: [vr.left, vr.top, vr.width, vr.height].map(Math.round) };
})()`;
const result = { mode };
const target = frameSession;
// fareyi videonun üstüne getir (iframe içinde: iframe konumu 120,~80 + içerik)
const mv = mode === "iframe" ? [350, 300] : [450, 300 - 0];
if (mode === "direct") { await ev("window.scrollTo(0, 100)"); await sleep(300); }
await ev("window.__mm=0;addEventListener('mousemove',()=>__mm++,true)", target);
for (let i = 0; i < 4; i++) { await mouse("mouseMoved", mv[0] + i, mv[1] + i); await sleep(250); }
console.log("mousemove events in frame:", await ev("window.__mm + ' video:' + JSON.stringify([...document.querySelectorAll('video')].map(v=>{const r=v.getBoundingClientRect();return [r.width,r.height]}))", target));
result.afterHover = await ev(probe, target); console.log("hover", JSON.stringify(result.afterHover));

// hızlı kaydırma: her karede düğme-video ilişkisi sabit mi?
if (mode === "direct") {
  const samples = [];
  await ev(`window.__samples = []; (function f(){ const h=document.getElementById("tdownloader-overlay"); const b=h.shadowRoot.getElementById("btn"), v=document.querySelector("video");
    const vr=v.getBoundingClientRect(), br=b.getBoundingClientRect(); window.__samples.push([Math.round(vr.right-br.right), Math.round(br.top-vr.top), b.style.display]); if(window.__samples.length<120) requestAnimationFrame(f); })()`);
  for (let i = 0; i < 12; i++) { await mouse("mouseWheel", mv[0], mv[1], { deltaX: 0, deltaY: 90 }); await sleep(16); }
  await sleep(1200);
  const s = await ev("window.__samples");
  const shown = s.filter((x) => x[2] !== "none");
  result.scroll = { frames: s.length, shown: shown.length, distinctOffsets: [...new Set(shown.map((x) => x[0] + "," + x[1]))] };
  await ev("window.scrollTo(0, 100)"); await sleep(300);
  for (let i = 0; i < 4; i++) { await mouse("mouseMoved", mv[0] + i, mv[1] + i); await sleep(200); }
}

// tıkla: düğme konumu
const pos = await ev(`(() => { const b = document.getElementById("tdownloader-overlay").shadowRoot.getElementById("btn").getBoundingClientRect(); return [b.left + b.width/2, b.top + b.height/2]; })()`, target);
// iframe içi koordinatı sayfa koordinatına çevir
let click = pos;
if (mode === "iframe") {
  const fr = await ev(`(() => { const r = document.getElementById("pl").getBoundingClientRect(); return [r.left + 3, r.top + 3]; })()`);
  click = [pos[0] + fr[0], pos[1] + fr[1]];
}
await mouse("mouseMoved", click[0], click[1]); await sleep(150);
await mouse("mousePressed", click[0], click[1], { button: "left", clickCount: 1 });
await mouse("mouseReleased", click[0], click[1], { button: "left", clickCount: 1 });
await sleep(400);
result.loading = (await ev(probe, target)).menuText;
for (let i = 0; i < 30; i++) { await sleep(500); const p = await ev(probe, target); if (p.rows?.length) { result.menu = p; break; } }
fs.writeFileSync(path.join(work, `drive-${mode}.json`), JSON.stringify(result, null, 1));
console.log(JSON.stringify(result, null, 1));
if (process.argv[4] === "select") {
  const idx = +process.argv[5] || 0;
  const rowPos = await ev(`(() => { const r = document.getElementById("tdownloader-overlay").shadowRoot.querySelectorAll("#menu .row")[${idx}].getBoundingClientRect(); return [r.left + 20, r.top + r.height/2]; })()`, target);
  let rp = rowPos;
  if (mode === "iframe") { const fr = await ev(`(() => { const r = document.getElementById("pl").getBoundingClientRect(); return [r.left + 3, r.top + 3]; })()`); rp = [rowPos[0] + fr[0], rowPos[1] + fr[1]]; }
  await mouse("mouseMoved", rp[0], rp[1]); await sleep(100);
  await mouse("mousePressed", rp[0], rp[1], { button: "left", clickCount: 1 });
  await mouse("mouseReleased", rp[0], rp[1], { button: "left", clickCount: 1 });
  await sleep(500);
  console.log("seçildi:", idx, (await ev(probe, target)).menuText);
}
ws.close(); process.exit(0);
