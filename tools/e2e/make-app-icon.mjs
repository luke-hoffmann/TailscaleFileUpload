// Renders TaildropApp/app.ico, the desktop app's icon (Explorer, taskbar, title bar):  node tools/e2e/make-app-icon.mjs
// A mesh of network nodes (the tailnet) whose middle column falls as a drop into an inbox tray (the drop).
// Small sizes get simpler artwork so they stay crisp: 32/24 px keep one node, 16 px is just the drop and tray.
// 256 px is stored as PNG, the rest as 32-bit bitmaps, which every Windows version and System.Drawing.Icon read.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const out = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../TaildropApp/app.ico');

const GREEN_TOP = '#2b8a52';
const GREEN_BOTTOM = '#17532f';   // around the desktop window's green (#1e6a3d)
const LIME = '#c9f86a';           // the phone page's icon color

const tile = `<defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${GREEN_TOP}"/><stop offset="1" stop-color="${GREEN_BOTTOM}"/></linearGradient></defs>
  <rect x="8" y="8" width="240" height="240" rx="54" fill="url(#g)"/>`;
const node = (x, y, r, opacity = 1) => `<circle cx="${x}" cy="${y}" r="${r}" fill="#fff" fill-opacity="${opacity}"/>`;
/** A drop with its point at (cx, top) and a round bottom of radius r. */
const drop = (cx, top, r) => `<path d="M${cx} ${top} C${cx + r * 0.55} ${top + r * 0.85} ${cx + r} ${top + r * 1.35} ${cx + r} ${top + r * 1.95} A${r} ${r} 0 0 1 ${cx - r} ${top + r * 1.95} C${cx - r} ${top + r * 1.35} ${cx - r * 0.55} ${top + r * 0.85} ${cx} ${top} Z" fill="${LIME}"/>`;
/** An inbox tray: open box with a dipped lip, top edge at y. */
const tray = (y, stroke, x0, x1, depth, dip) => {
  const w = x1 - x0;
  return `<path d="M${x0} ${y} v${depth} a14 14 0 0 0 14 14 h${w - 28} a14 14 0 0 0 14 -14 v-${depth} M${x0} ${y} h${w * 0.26} l${dip * 0.75} ${dip} h${w * 0.48 - dip * 1.5} l${dip * 0.75} -${dip} h${w * 0.26}" fill="none" stroke="#fff" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round"/>`;
};
/** The same tray without the lip, for 16 px. */
const cup = (y, stroke, x0, x1, depth) => `<path d="M${x0} ${y} v${depth} a16 16 0 0 0 16 16 h${x1 - x0 - 32} a16 16 0 0 0 16 -16 v-${depth}" fill="none" stroke="#fff" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round"/>`;

const full = tile
  + [76, 180].flatMap(x => [48, 88, 128].map(y => node(x, y, 13, 0.3))).join('')
  + node(128, 48, 13) + node(128, 88, 13)
  + drop(128, 110, 20)
  + tray(178, 13, 62, 194, 24, 16);
const medium = tile + node(128, 54, 19) + drop(128, 86, 31) + tray(170, 17, 50, 206, 34, 18);
const small = tile + drop(128, 46, 38) + cup(150, 26, 44, 212, 44);

const frames = [[256, full], [128, full], [64, full], [48, full], [32, medium], [24, medium], [16, small]];

const browser = await chromium.launch({ args: ['--no-sandbox'] });
const page = await browser.newPage();
const images = [];
for (const [size, art] of frames) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 256 256">${art}</svg>`;
  images.push(await page.evaluate(async ({ svg, size }) => {
    const img = new Image();
    img.src = 'data:image/svg+xml,' + encodeURIComponent(svg);
    await img.decode();
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = size;
    const context = canvas.getContext('2d');
    context.drawImage(img, 0, 0, size, size);
    return { size, rgba: [...context.getImageData(0, 0, size, size).data], png: canvas.toDataURL('image/png').split(',')[1] };
  }, { svg, size }));
}
await browser.close();

/** 32-bit BGRA bitmap as stored in an .ico: header with doubled height, bottom-up rows, then the 1-bit AND mask. */
function bitmap({ size, rgba }) {
  const maskStride = Math.ceil(size / 32) * 4;
  const data = Buffer.alloc(40 + size * size * 4 + maskStride * size);
  data.writeUInt32LE(40, 0);
  data.writeInt32LE(size, 4);
  data.writeInt32LE(size * 2, 8);
  data.writeUInt16LE(1, 12);
  data.writeUInt16LE(32, 14);
  data.writeUInt32LE(size * size * 4 + maskStride * size, 20);
  for (let y = 0; y < size; y++) {
    const row = size - 1 - y;
    for (let x = 0; x < size; x++) {
      const i = (y * size + x) * 4;
      const o = 40 + (row * size + x) * 4;
      data[o] = rgba[i + 2];
      data[o + 1] = rgba[i + 1];
      data[o + 2] = rgba[i];
      data[o + 3] = rgba[i + 3];
      if (rgba[i + 3] === 0) data[40 + size * size * 4 + row * maskStride + (x >> 3)] |= 0x80 >> (x & 7);
    }
  }
  return data;
}

const payloads = images.map(image => image.size === 256 ? Buffer.from(image.png, 'base64') : bitmap(image));
const header = Buffer.alloc(6 + 16 * images.length);
header.writeUInt16LE(1, 2);                  // type: icon
header.writeUInt16LE(images.length, 4);
let offset = header.length;
images.forEach((image, i) => {
  const entry = 6 + i * 16;
  header[entry] = image.size === 256 ? 0 : image.size;   // 0 means 256
  header[entry + 1] = image.size === 256 ? 0 : image.size;
  header.writeUInt16LE(1, entry + 4);        // planes
  header.writeUInt16LE(32, entry + 6);       // bits per pixel
  header.writeUInt32LE(payloads[i].length, entry + 8);
  header.writeUInt32LE(offset, entry + 12);
  offset += payloads[i].length;
});
fs.writeFileSync(out, Buffer.concat([header, ...payloads]));
console.log('wrote', out, `(${frames.map(([size]) => size).join(', ')} px)`);
