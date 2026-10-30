// T-Downloader logosu: "T" harfi + aşağı ok tek parça glif, sarı yuvarlatılmış kare üstünde siyah.
// Bağımlılıksız; SVG, ICO (16..256), eklenti PNG'leri ve önizleme PNG'sini üretir.
// Kullanım (depo kökünden): node tools/make-logo.mjs
import { deflateSync } from "node:zlib";
import { writeFileSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const YELLOW = [0xff, 0xc1, 0x07], BLACK = [0x11, 0x11, 0x11];

// ---- Geometri (128x128 uzayı). Glif tek çokgen; köşeler R yarıçapıyla yuvarlatılır.
const R = 3;
const GLYPH = [[18,20],[110,20],[110,46],[78,46],[78,70],[102,70],[64,109],[26,70],[50,70],[50,46],[18,46]];
const RADIUS = 28;

const pathD = (pts) => "M" + pts.map(p => p.join(" ")).join(" L") + " Z";
function inPoly(x, y, pts) {
  let c = false;
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const [xi, yi] = pts[i], [xj, yj] = pts[j];
    if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) c = !c;
  }
  return c;
}
function segDist(x, y, a, b) {
  const dx = b[0] - a[0], dy = b[1] - a[1];
  const t = Math.max(0, Math.min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / (dx * dx + dy * dy)));
  return Math.hypot(x - (a[0] + t * dx), y - (a[1] + t * dy));
}
function inGlyph(x, y, pts) {
  if (inPoly(x, y, pts)) return true;
  for (let i = 0; i < pts.length; i++) if (segDist(x, y, pts[i], pts[(i + 1) % pts.length]) <= R) return true;
  return false;
}
function inRound(x, y, rx, ry, rw, rh, r) {
  const cx = Math.min(Math.max(x, rx + r), rx + rw - r), cy = Math.min(Math.max(y, ry + r), ry + rh - r);
  return x >= rx && x <= rx + rw && y >= ry && y <= ry + rh && (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
}

// Dönen: RGBA Buffer (size*size*4).
export function render(size, { bg = true } = {}) {
  const pts = GLYPH;
  const S = size <= 32 ? 8 : 4, buf = Buffer.alloc(size * size * 4);
  for (let py = 0; py < size; py++) for (let px = 0; px < size; px++) {
    let tile = 0, glyph = 0;
    for (let sy = 0; sy < S; sy++) for (let sx = 0; sx < S; sx++) {
      const x = ((px + (sx + 0.5) / S) / size) * 128, y = ((py + (sy + 0.5) / S) / size) * 128;
      const inTile = bg ? inRound(x, y, 0, 0, 128, 128, RADIUS) : true;
      if (!inTile) continue;
      tile++;
      if (inGlyph(x, y, pts)) glyph++;
    }
    const n = S * S, o = (py * size + px) * 4;
    if (!bg) { const a = glyph / n; buf[o] = BLACK[0]; buf[o+1] = BLACK[1]; buf[o+2] = BLACK[2]; buf[o+3] = Math.round(a * 255); continue; }
    const w = tile ? glyph / tile : 0;
    for (let k = 0; k < 3; k++) buf[o + k] = Math.round(YELLOW[k] + (BLACK[k] - YELLOW[k]) * w);
    buf[o + 3] = Math.round((tile / n) * 255);
  }
  return buf;
}

// ---- PNG / ICO yazıcıları
const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc = (b) => { let c = 0xffffffff; for (const x of b) c = crcTable[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
function chunk(type, data) {
  const t = Buffer.from(type), len = Buffer.alloc(4), c = Buffer.alloc(4);
  len.writeUInt32BE(data.length); c.writeUInt32BE(crc(Buffer.concat([t, data])));
  return Buffer.concat([len, t, data, c]);
}
export function png(w, h, rgba) {
  const raw = Buffer.alloc(h * (w * 4 + 1));
  for (let y = 0; y < h; y++) { raw[y * (w * 4 + 1)] = 0; rgba.copy(raw, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4); }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw, { level: 9 })), chunk("IEND", Buffer.alloc(0))]);
}
function bmpEntry(size, rgba) {
  const maskRow = Math.ceil(size / 32) * 4, imgSize = size * size * 4 + maskRow * size;
  const b = Buffer.alloc(40 + imgSize);
  b.writeUInt32LE(40, 0); b.writeInt32LE(size, 4); b.writeInt32LE(size * 2, 8); b.writeUInt16LE(1, 12); b.writeUInt16LE(32, 14); b.writeUInt32LE(imgSize, 20);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const s = ((size - 1 - y) * size + x) * 4, d = 40 + (y * size + x) * 4;
    b[d] = rgba[s + 2]; b[d + 1] = rgba[s + 1]; b[d + 2] = rgba[s]; b[d + 3] = rgba[s + 3];
  }
  return b;
}
function ico(sizes) {
  const entries = sizes.map(s => ({ s, data: s >= 256 ? png(s, s, render(s)) : bmpEntry(s, render(s)) }));
  const head = Buffer.alloc(6 + 16 * entries.length); head.writeUInt16LE(1, 2); head.writeUInt16LE(entries.length, 4);
  let off = head.length;
  entries.forEach((e, i) => {
    const o = 6 + 16 * i;
    head[o] = e.s >= 256 ? 0 : e.s; head[o + 1] = e.s >= 256 ? 0 : e.s; head.writeUInt16LE(1, o + 4); head.writeUInt16LE(32, o + 6);
    head.writeUInt32LE(e.data.length, o + 8); head.writeUInt32LE(off, o + 12); off += e.data.length;
  });
  return Buffer.concat([head, ...entries.map(e => e.data)]);
}

// ---- SVG
const hex = (c) => "#" + c.map(v => v.toString(16).padStart(2, "0")).join("");
const svg = (bg) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" role="img" aria-label="T-Downloader">
${bg ? `  <rect width="128" height="128" rx="${RADIUS}" fill="${hex(YELLOW)}"/>\n` : ""}  <path d="${pathD(GLYPH)}" fill="${hex(BLACK)}" stroke="${hex(BLACK)}" stroke-width="${R * 2}" stroke-linejoin="round"/>
</svg>
`;

const out = (p, d) => { mkdirSync(dirname(join(root, p)), { recursive: true }); writeFileSync(join(root, p), d); };
out("assets/logo/logo.svg", svg(true));
out("assets/logo/logo-mono.svg", svg(false));
out("extension/icons/icon.svg", svg(true));
out("assets/logo/logo.ico", ico([16, 20, 24, 32, 40, 48, 64, 256]));
for (const s of [16, 32, 48, 128]) out(`extension/icons/icon${s}.png`, png(s, s, render(s)));
out("assets/logo/logo-256.png", png(256, 256, render(256)));

// ---- Önizleme: 256/48/32/16 px (+16 px'in 8x büyütülmüşü), açık ve koyu zemin
{
  const W = 720, H = 760, img = Buffer.alloc(W * H * 4);
  const bands = [[0xf5, 0xf5, 0xf5], [0x1e, 0x1e, 0x1e]];
  bands.forEach((c, b) => { for (let y = b * 380; y < (b + 1) * 380; y++) for (let x = 0; x < W; x++) { const o = (y * W + x) * 4; img[o] = c[0]; img[o+1] = c[1]; img[o+2] = c[2]; img[o+3] = 255; } });
  const put = (rgba, s, ox, oy, scale = 1) => {
    for (let y = 0; y < s * scale; y++) for (let x = 0; x < s * scale; x++) {
      const si = ((Math.floor(y / scale)) * s + Math.floor(x / scale)) * 4, a = rgba[si + 3] / 255, o = ((oy + y) * W + ox + x) * 4;
      for (let k = 0; k < 3; k++) img[o + k] = Math.round(rgba[si + k] * a + img[o + k] * (1 - a));
    }
  };
  for (let b = 0; b < 2; b++) {
    const base = b * 380 + 330; // taban çizgisi
    let x = 24;
    for (const s of [256, 48, 32, 16]) { put(render(s), s, x, base - s); x += s + 28; }
    put(render(16), 16, x, base - 128, 8);
  }
  out("docs/screenshots/logo-preview.png", png(W, H, img));
}
console.log("logo ve ikonlar yazıldı");
