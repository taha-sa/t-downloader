// Basit PNG ikonları üretir (bağımlılıksız): mavi yuvarlak kare + beyaz aşağı ok.
// Kullanım: node tools/make-icons.mjs
import { deflateSync } from "node:zlib";
import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const out = join(dirname(fileURLToPath(import.meta.url)), "..", "icons");
const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0;
});
const crc = (b) => { let c = 0xffffffff; for (const x of b) c = crcTable[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
function chunk(type, data) {
  const t = Buffer.from(type), len = Buffer.alloc(4), c = Buffer.alloc(4);
  len.writeUInt32BE(data.length); c.writeUInt32BE(crc(Buffer.concat([t, data])));
  return Buffer.concat([len, t, data, c]);
}

// Şekil: (x,y) 0..1 uzayında kapsama.
function inRoundRect(x, y, r = 0.22) {
  const cx = Math.min(Math.max(x, r), 1 - r), cy = Math.min(Math.max(y, r), 1 - r);
  return (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
}
function inArrow(x, y) {
  if (x >= 0.42 && x <= 0.58 && y >= 0.2 && y <= 0.55) return true; // gövde
  const t = (y - 0.5) / 0.28;                                          // uç (üçgen)
  return y >= 0.5 && y <= 0.78 && Math.abs(x - 0.5) <= 0.24 * (1 - t);
}
function inBar(x, y) { return x >= 0.25 && x <= 0.75 && y >= 0.8 && y <= 0.86; }

function png(size) {
  const S = 4, raw = Buffer.alloc(size * (size * 4 + 1));
  for (let py = 0; py < size; py++) {
    raw[py * (size * 4 + 1)] = 0;
    for (let px = 0; px < size; px++) {
      let a = 0, white = 0;
      for (let sy = 0; sy < S; sy++) for (let sx = 0; sx < S; sx++) {
        const x = (px + (sx + 0.5) / S) / size, y = (py + (sy + 0.5) / S) / size;
        if (inRoundRect(x, y)) { a++; if (inArrow(x, y) || inBar(x, y)) white++; }
      }
      const n = S * S, o = py * (size * 4 + 1) + 1 + px * 4;
      const w = a ? white / a : 0;
      raw[o] = Math.round(31 + (255 - 31) * w);
      raw[o + 1] = Math.round(111 + (255 - 111) * w);
      raw[o + 2] = Math.round(235 + (255 - 235) * w);
      raw[o + 3] = Math.round((a / n) * 255);
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0)),
  ]);
}

for (const s of [16, 32, 48, 128]) writeFileSync(join(out, `icon${s}.png`), png(s));

writeFileSync(join(out, "icon.svg"),
`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
  <rect width="128" height="128" rx="28" fill="#1f6feb"/>
  <path d="M54 26h20v40h18L64 100 36 66h18z" fill="#fff"/>
  <rect x="32" y="103" width="64" height="8" rx="3" fill="#fff"/>
</svg>
`);
console.log("icons written");
