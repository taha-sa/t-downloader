// Yerel uçtan uca test sitesi (gerçek siteye istek atılmaz).
//   node site.mjs <çalışmaKlasörü>
// - FILM sitesi   http://localhost:PF/         : sayfa, içinde başka alan adında iframe
// - OYNATICI      http://127.0.0.1:PP/player   : çerez koyar, <video> oynatır, m3u8'i fetch eder (iframe içeriği)
// - CDN           http://127.0.0.1:PC/hls/...  : Referer + Origin + çerez ister (yoksa 403), gerçek HLS (ffmpeg ile üretilir)
// - KÖPRÜ         http://127.0.0.1:PB/         : eklentinin (native.js yerine geçen) taşıması <-> App named pipe
import http from "node:http";
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

const work = path.resolve(process.argv[2] || "e2e-work");
const pipeName = process.env.TDOWNLOADER_PIPE || "TDownloader.Ipc.test";
fs.mkdirSync(work, { recursive: true });
const hlsDir = path.join(work, "media");
fs.mkdirSync(hlsDir, { recursive: true });

// ---- medya üret (ffmpeg gerekir) ----
function ff(...args) {
  const r = spawnSync("ffmpeg", ["-y", "-hide_banner", "-loglevel", "error", ...args], { stdio: "inherit" });
  if (r.status !== 0) throw new Error("ffmpeg başarısız");
}
for (const [name, size] of [["720", "1280x720"], ["360", "640x360"]]) {
  const d = path.join(hlsDir, name);
  if (fs.existsSync(path.join(d, "index.m3u8"))) continue;
  fs.mkdirSync(d, { recursive: true });
  ff("-f", "lavfi", "-i", `testsrc=size=${size}:rate=25`, "-f", "lavfi", "-i", "sine=frequency=440", "-t", "6",
    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod",
    "-hls_segment_filename", path.join(d, "s%d.ts"), path.join(d, "index.m3u8"));
}
const movie = path.join(hlsDir, "movie.mp4");
if (!fs.existsSync(movie)) ff("-f", "lavfi", "-i", "testsrc=size=640x360:rate=25", "-t", "30", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", movie);

const listen = (srv) => new Promise((r) => srv.listen(0, "127.0.0.1", () => r(srv.address().port)));
const ports = {};
const log = [];                              // CDN erişim günlüğü
const bridgeLog = path.join(work, "bridge.jsonl");
fs.writeFileSync(bridgeLog, "");

// ---- CDN ----
const cdn = http.createServer((req, res) => {
  const u = new URL(req.url, "http://x");
  const origin = `http://127.0.0.1:${ports.player}`;
  const entry = { path: u.pathname, referer: req.headers.referer || "", origin: req.headers.origin || "", cookie: req.headers.cookie || "" };
  const ok = entry.referer.startsWith(origin + "/") && entry.origin === origin;  // çerez: 3. taraf çerez engeli nedeniyle zorunlu değil (günlüğe yazılır)
  entry.status = ok ? 200 : 403; log.push(entry);
  res.setHeader("Access-Control-Allow-Origin", origin);
  res.setHeader("Access-Control-Allow-Credentials", "true");
  if (req.method === "OPTIONS") { res.writeHead(204); return res.end(); }
  if (!ok) { res.writeHead(403); return res.end("forbidden"); }
  let f;
  if (u.pathname === "/hls/master.m3u8") {
    res.writeHead(200, { "Content-Type": "application/vnd.apple.mpegurl" });
    return res.end("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360\n360/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720\n720/index.m3u8\n");
  }
  f = path.join(hlsDir, u.pathname.replace(/^\/hls\//, ""));
  if (!f.startsWith(hlsDir) || !fs.existsSync(f)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { "Content-Type": f.endsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t" });
  fs.createReadStream(f).pipe(res);
});

// ---- oynatıcı (iframe içeriği) ----
const player = http.createServer((req, res) => {
  const u = new URL(req.url, "http://x");
  if (u.pathname === "/movie.mp4") {
    const st = fs.statSync(movie);
    const range = req.headers.range;
    if (range) {
      const [a, b] = range.replace("bytes=", "").split("-"); const s = +a, e = b ? +b : st.size - 1;
      res.writeHead(206, { "Content-Type": "video/mp4", "Content-Range": `bytes ${s}-${e}/${st.size}`, "Content-Length": e - s + 1, "Accept-Ranges": "bytes" });
      return fs.createReadStream(movie, { start: s, end: e }).pipe(res);
    }
    res.writeHead(200, { "Content-Type": "video/mp4", "Content-Length": st.size, "Accept-Ranges": "bytes" });
    return fs.createReadStream(movie).pipe(res);
  }
  res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Set-Cookie": "sid=e2e-cookie; Path=/" });
  res.end(`<!doctype html><meta charset=utf-8><title>player</title><body style="margin:0;background:#000">
<video id=v muted autoplay loop playsinline src="/movie.mp4" style="width:100%;height:100%;object-fit:contain"></video>
<script>
  // hls.js yerine: oynatıcı master playlist'i çerezle ve CORS ile ister (eklenti bunu görüp sniff eder)
  fetch("http://127.0.0.1:${ports.cdn}/hls/master.m3u8", { credentials: "include" }).then(r => r.text()).then(t => { window.__m3u8 = t.length; });
</script>`);
});

// ---- film sitesi (üst sayfa) ----
const film = http.createServer((req, res) => {
  if (req.url.startsWith("/direct")) {
    // üst sayfada doğrudan <video> + uzun sayfa (kaydırma testi)
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    return res.end(`<!doctype html><meta charset=utf-8><title>Direct video</title><body style="margin:0;height:4000px">
<div style="height:300px"></div><video id=v muted autoplay loop src="http://127.0.0.1:${ports.player}/movie.mp4" style="display:block;margin-left:150px;width:640px;height:360px"></video>`);
  }
  res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
  res.end(`<!doctype html><meta charset=utf-8><title>Test Film - izle</title>
<body style="margin:0;font:16px sans-serif;background:#222;color:#ddd"><h1 style="margin:20px 40px">Test Film (2026)</h1>
<iframe id=pl src="http://127.0.0.1:${ports.player}/player" allowfullscreen
  style="display:block;margin:30px 0 0 120px;width:800px;height:450px;border:3px solid #555"></iframe>
<script>
  // beklenen: iframe içindeki videonun üst sayfadaki mutlak konumu (içerik alanı, border içeri)
  addEventListener("load", () => { setTimeout(() => {
    const r = document.getElementById("pl").getBoundingClientRect();
    fetch("http://127.0.0.1:${ports.bridge}/expect", { method: "POST", body: JSON.stringify({ left: r.left + 3, top: r.top + 3, right: r.right - 3, bottom: r.bottom - 3, dpr: devicePixelRatio, iw: innerWidth, ih: innerHeight }) });
  }, 800); });
</script>`);
});

// ---- köprü: eklenti <-> App pipe ----
const toExt = [];                 // App'ten eklentiye
let waiter = null;
let expected = null;
let pipe = null, buf = "";
function connectPipe() {
  pipe = net.connect("\\\\.\\pipe\\" + pipeName);
  pipe.on("connect", () => console.log("pipe bağlandı", pipeName));
  pipe.on("data", (d) => {
    buf += d.toString("utf8"); let i;
    while ((i = buf.indexOf("\n")) >= 0) {
      const line = buf.slice(0, i); buf = buf.slice(i + 1);
      if (!line.trim()) continue;
      fs.appendFileSync(bridgeLog, JSON.stringify({ dir: "app->ext", msg: JSON.parse(line) }) + "\n");
      toExt.push(JSON.parse(line)); if (waiter) { waiter(); waiter = null; }
    }
  });
  pipe.on("error", (e) => { console.log("pipe hata", e.message); setTimeout(connectPipe, 1000); });
  pipe.on("close", () => setTimeout(connectPipe, 1000));
}
const bridge = http.createServer((req, res) => {
  res.setHeader("Access-Control-Allow-Origin", "*");
  res.setHeader("Access-Control-Allow-Headers", "*");
  if (req.method === "OPTIONS") { res.writeHead(204); return res.end(); }
  const u = new URL(req.url, "http://x");
  let body = ""; req.on("data", (d) => (body += d));
  req.on("end", () => {
    if (u.pathname === "/msg") {
      const m = JSON.parse(body);
      fs.appendFileSync(bridgeLog, JSON.stringify({ dir: "ext->app", msg: m }) + "\n");
      if (pipe && !pipe.destroyed) pipe.write(JSON.stringify(m) + "\n");
      res.writeHead(200); return res.end("{}");
    }
    if (u.pathname === "/poll") {
      const give = () => { res.writeHead(200, { "Content-Type": "application/json" }); res.end(JSON.stringify(toExt.splice(0))); };
      if (toExt.length) return give();
      const t = setTimeout(() => { waiter = null; give(); }, 15000);
      waiter = () => { clearTimeout(t); give(); };
      return;
    }
    if (u.pathname === "/expect") { expected = JSON.parse(body); fs.writeFileSync(path.join(work, "expected.json"), body); res.writeHead(200); return res.end("{}"); }
    if (u.pathname === "/cdnlog") { res.writeHead(200, { "Content-Type": "application/json" }); return res.end(JSON.stringify(log)); }
    res.writeHead(404); res.end();
  });
});

ports.cdn = await listen(cdn);
ports.player = await listen(player);
ports.bridge = await listen(bridge);
ports.film = await listen(film);
fs.writeFileSync(path.join(work, "ports.json"), JSON.stringify(ports));
console.log("PORTS " + JSON.stringify(ports));
connectPipe();
