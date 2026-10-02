// Renders Taildrop.Core/Assets/apple-touch-icon.png (180x180, full-bleed; iOS rounds the corners itself)
// from the same artwork as favicon.svg:  node tools/e2e/make-icon.mjs
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const out = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../Taildrop.Core/Assets/apple-touch-icon.png');
const browser = await chromium.launch({ args: ['--no-sandbox'] });
const page = await browser.newPage({ viewport: { width: 180, height: 180 }, deviceScaleFactor: 1 });
await page.setContent(`<body style="margin:0;background:#c9f86a"><svg width="180" height="180" viewBox="0 0 64 64"><path d="M20 44 44 20M27 20h17v17" fill="none" stroke="#151b0b" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/></svg></body>`);
await page.screenshot({ path: out, clip: { x: 0, y: 0, width: 180, height: 180 } });
await browser.close();
console.log('wrote', out);
