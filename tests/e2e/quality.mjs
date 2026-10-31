// CDP ile Brave'i sürer: düğme kararlılığı (/jump), kaydırma (/direct) ve master'sız kalite menüsü (/multi, /multi2).
//   node quality.mjs <çalışmaKlasörü> <jump|scroll|multi|multi2>
import fs from "node:fs";
import path from "node:path";

const work = path.resolve(process.argv[2]);
const mode = process.argv[3];
const ports = JSON.parse(fs.readFileSync(path.join(work, "ports.json"), "utf8"));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const list = await (await fetch("http://127.0.0.1:9333/json")).json();
const page = list.find((t) => t.type === "page" && t.url.includes("localhost:" + ports.film)) || list.find((t) => t.type === "page");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const waits = new Map();
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && waits.has(m.id)) { waits.get(m.id)(m); waits.delete(m.id); } };
const call = (method, params = {}) => new Promise((res) => { const i = ++id; waits.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async (expr) => {
  const r = await call("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails).slice(0, 300));
  return r.result?.result?.value;
};
const mouse = (type, x, y, extra = {}) => call("Input.dispatchMouseEvent", { type, x, y, ...extra });
const out = { mode }; await call("Page.bringToFront"); await call("Emulation.setFocusEmulationEnabled",{enabled:true});
const nav = async (p) => { await call("Page.navigate", { url: `http://localhost:${ports.film}${p}` }); await sleep(900); };

const probe = `(() => {
  const h = document.getElementById("tdownloader-overlay"); if (!h) return { host: false };
  const b = h.shadowRoot.getElementById("btn"), m = h.shadowRoot.getElementById("menu");
  const v = document.querySelector("video"), vr = v.getBoundingClientRect(), br = b.getBoundingClientRect();
  return { host: true, disp: b.style.display, vis: b.classList.contains("vis"), dx: Math.round(vr.right - br.right), dy: Math.round(br.top - vr.top),
    inside: br.left >= vr.left - 1 && br.right <= vr.right + 1 && br.top >= vr.top - 1 && br.bottom <= vr.bottom + 1,
    cx: br.left + br.width / 2, cy: br.top + br.height / 2, menu: m.style.display, rows: [...m.querySelectorAll(".row,.g")].map(r => r.innerText.replace(/\\n/g, " | ")) };
})()`;

if (mode === "jump") {
  await nav("/jump");
  // fare, video kutusunun değişen konumlarını izler; her 50 ms'de hareket
  const t0 = Date.now();
  while (Date.now() - t0 < 3200) {
    const t = Date.now() - t0;
    const [x, y] = t < 250 ? [100, 250] : t < 500 ? [800, 300] : [400, 300];
    await mouse("mouseMoved", x + (t % 7), y); await sleep(50);
  }
  const log = await ev("window.__log");
  const shown = log.filter((e) => e.disp === "flex");
  out.frames = log.length;
  out.firstShownMs = shown.length ? shown[0].t : null;
  out.shownCount = shown.length;
  out.shownOffsets = [...new Set(shown.map((e) => `${e.dx},${e.dy},w${e.vw}`))];
  out.shownBeforeFinal = shown.filter((e) => e.t < 750 + 380).length;
  out.fadeClassWhenShown = shown.every((e) => e.vis);
} else if (mode === "scroll") {
  await nav("/direct");
  for (let i = 0; i < 12; i++) { await mouse("mouseMoved", 450 + (i % 3), 400); await sleep(100); }
  out.beforeScroll = await ev(probe);
  const samples = [];
  await ev(`window.__s = []; (function f(){ const h=document.getElementById("tdownloader-overlay"), b=h.shadowRoot.getElementById("btn"), v=document.querySelector("video");
    const vr=v.getBoundingClientRect(), br=b.getBoundingClientRect(); window.__s.push({d:b.style.display, dx:Math.round(vr.right-br.right), dy:Math.round(br.top-vr.top), vt:Math.round(vr.top), bt:Math.round(br.top), in: br.top>=vr.top-1&&br.bottom<=vr.bottom+1&&br.top>=0}); if(window.__s.length<400) requestAnimationFrame(f); })()`);
  for (let i = 0; i < 18; i++) { await mouse("mouseWheel", 450, 400, { deltaX: 0, deltaY: 60 }); await sleep(30); }
  await sleep(800);
  const s = await ev("window.__s");
  const shown = s.filter((x) => x.d !== "none");
  out.frames = s.length; out.shown = shown.length;
  out.offsets = [...new Set(shown.map((x) => x.dx + "," + x.dy))];
  out.insideVideoAndViewport = shown.every((x) => x.in);
  out.minButtonTopWhileShown = shown.length ? Math.min(...shown.map((x) => x.bt)) : null;
  out.videoTopAtEnd = s[s.length - 1].vt; out.lastDisplay = s[s.length - 1].d;
} else {
  await nav(mode === "multi2" ? "/multi2" : "/multi");
  await sleep(1500);
  for (let i = 0; i < 12; i++) { await mouse("mouseMoved", 480 + (i % 3), 300); await sleep(100); }
  const pr = await ev(probe);
  out.err = await ev("document.documentElement.dataset.tdErr || ''"); out.button = { disp: pr.disp, vis: pr.vis, dx: pr.dx, dy: pr.dy, inside: pr.inside };
  await mouse("mouseMoved", pr.cx, pr.cy); await sleep(150);
  await mouse("mousePressed", pr.cx, pr.cy, { button: "left", clickCount: 1 });
  await mouse("mouseReleased", pr.cx, pr.cy, { button: "left", clickCount: 1 });
  for (let i = 0; i < 40; i++) { await sleep(400); const p = await ev(probe); if (p.rows?.some((r) => /hls|mp4/i.test(r))) { out.menu = p.rows; break; } out.last = p.rows; }
}
fs.writeFileSync(path.join(work, `quality-${mode}.json`), JSON.stringify(out, null, 1));
console.log(JSON.stringify(out, null, 1));
ws.close(); process.exit(0);
