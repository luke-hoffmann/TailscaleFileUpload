// Browser tests for the phone page, run against the real Taildrop server (tools/DevHost) in an iPhone-sized
// Chromium. Prerequisites:
//   dotnet build tools/DevHost
//   TAILDROP_SAMPLES_DIR=/tmp/samples dotnet test Taildrop.Core.Tests --filter SamplePhotos
//   npm install --prefix tools/e2e && node tools/e2e/phone.e2e.mjs
// Environment: SAMPLES (default /tmp/samples), SHOTS (screenshot folder, default /tmp/shots), PORT (default: random).

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, devices } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const SAMPLES = process.env.SAMPLES || process.env.TAILDROP_SAMPLES_DIR || '/tmp/samples';
const SHOTS = process.env.SHOTS || '/tmp/shots';
const PORT = Number(process.env.PORT || 20000 + Math.floor(Math.random() * 20000));
const INBOX = fs.mkdtempSync(path.join(os.tmpdir(), 'Taildrop-e2e-'));
fs.mkdirSync(SHOTS, { recursive: true });

const sample = name => path.join(SAMPLES, name);
const inbox = () => fs.readdirSync(INBOX).filter(name => !name.startsWith('.') && fs.statSync(path.join(INBOX, name)).isFile());
const scans = () => inbox().filter(name => name.startsWith('Scan '));
const sizeOf = name => fs.statSync(path.join(INBOX, name)).size;

// ---------- server ----------

const host = spawn('dotnet', [path.join(root, 'tools/DevHost/bin/Debug/net8.0/DevHost.dll'), '--port', String(PORT), '--inbox', INBOX], { stdio: ['ignore', 'pipe', 'pipe'] });
host.stderr.on('data', data => process.stderr.write(`[server] ${data}`));
await new Promise((resolve, reject) => {
  host.stdout.on('data', data => { if (String(data).includes('dev host')) resolve(); });
  host.on('exit', code => reject(new Error(`DevHost exited early (${code})`)));
  setTimeout(() => reject(new Error('DevHost did not start')), 30000);
});
process.on('exit', () => { try { host.kill('SIGKILL'); } catch { /* already gone */ } });
const BASE = `http://127.0.0.1:${PORT}`;

// ---------- harness ----------

const results = [];
async function step(name, fn) {
  try {
    await fn();
    results.push({ name, ok: true });
    console.log(`  ok   ${name}`);
  } catch (error) {
    results.push({ name, ok: false, error });
    console.log(`  FAIL ${name}\n       ${String(error.message).split('\n').join('\n       ')}`);
    try { await currentPage?.screenshot({ path: path.join(SHOTS, `FAIL-${name.replace(/\W+/g, '-')}.png`) }); } catch { /* ignore */ }
  }
}

const phone = { ...devices['iPhone 14'] };
delete phone.defaultBrowserType;
const browser = await chromium.launch({ args: ['--no-sandbox'] });
let currentPage;

async function newPage(colorScheme = 'light') {
  const context = await browser.newContext({ ...phone, colorScheme, baseURL: BASE });
  const page = await context.newPage();
  page.problems = [];
  page.on('console', message => { if (['error', 'warning'].includes(message.type())) page.problems.push(`${message.type()}: ${message.text()}`); });
  page.on('pageerror', error => page.problems.push(`pageerror: ${error.message}`));
  await page.goto('/');
  await page.waitForSelector('#connection[data-state="online"]');
  currentPage = page;
  return page;
}

const shot = (page, name) => page.screenshot({ path: path.join(SHOTS, `${name}.png`) });
const visible = (page, selector) => page.locator(selector).isVisible();
/** Elements that poke out past the right/left edge of the screen (scrollWidth alone misses clipped content on mobile). */
const overflowing = page => page.evaluate(() => [...document.querySelectorAll('body *')]
  .filter(element => element.offsetParent !== null || element.closest('svg'))
  .filter(element => !element.closest('[hidden]') && !element.classList.contains('visually-hidden') && !element.closest('svg'))
  .filter(element => { const r = element.getBoundingClientRect(); return r.width > 0 && (r.right > window.innerWidth + 1 || r.left < -1); })
  .map(element => `${element.tagName.toLowerCase()}${element.id ? '#' + element.id : ''}.${element.className}`));

async function waitForSent(page, count) {
  await page.waitForFunction(n => document.querySelectorAll('#queue .row[data-state="sent"]').length >= n, count, { timeout: 30000 });
}

async function dragHandle(page, label, dx, dy) {
  const handle = page.locator(`#overlay g[aria-label="${label}"]`);
  const box = await handle.boundingBox();
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx / 2, y + dy / 2, { steps: 4 });
  await page.mouse.move(x + dx, y + dy, { steps: 4 });
  await shot(page, 'adjust-dragging');
  await page.mouse.up();
}

// ---------- tests ----------

console.log('\nHome screen');
let page = await newPage();

await step('two clear actions, camera input opens the rear camera, files input has no restrictions', async () => {
  assert.equal(await page.locator('#scan strong').innerText(), 'Scan Document');
  assert.equal(await page.locator('#choose strong').innerText(), 'Send Photos or Files');
  assert.equal(await page.locator('#camera').getAttribute('capture'), 'environment');
  assert.equal(await page.locator('#camera').getAttribute('accept'), 'image/*');
  assert.equal(await page.locator('#files').getAttribute('accept'), null);
  assert.notEqual(await page.locator('#files').getAttribute('multiple'), null);
  assert.equal(await page.locator('#pill-text, #connection span').first().innerText(), 'Connected');
  await shot(page, '01-home-light');
});

await step('hit targets are at least 44pt and nothing scrolls sideways', async () => {
  const small = await page.$$eval('button', buttons => buttons
    .filter(button => button.offsetParent !== null && !button.closest('#sheet') && button.id !== 'connection')
    .map(button => ({ text: button.textContent.trim().slice(0, 30), h: button.getBoundingClientRect().height, w: button.getBoundingClientRect().width }))
    .filter(button => button.h < 44 || button.w < 44));
  assert.deepEqual(small, []);
  // the status pill is 36pt tall but its tap area is extended; check that a point just above it still hits it
  const hit = await page.evaluate(() => {
    const rect = document.querySelector('#connection').getBoundingClientRect();
    return document.elementFromPoint(rect.left + rect.width / 2, rect.top - 4)?.closest('#connection') !== null;
  });
  assert.ok(hit);
  assert.deepEqual(await overflowing(page), []);
});

console.log('\nSend Photos or Files (the original feature)');
await step('multiple files of any type upload, in order, to the inbox', async () => {
  await page.setInputFiles('#files', [sample('notes.txt'), sample('movie.mp4'), sample('a4-tilted.jpg')]);
  await page.waitForSelector('#keepOpen:not([hidden])');
  await waitForSent(page, 3);
  assert.ok(await page.locator('#keepOpen').isHidden());
  assert.deepEqual(['a4-tilted.jpg', 'movie.mp4', 'notes.txt'], inbox().sort());
  assert.equal(sizeOf('movie.mp4'), 300000);
  const names = await page.$$eval('#queue .name', nodes => nodes.map(node => node.textContent));
  assert.deepEqual(names, ['notes.txt', 'movie.mp4', 'a4-tilted.jpg']); // selection order, newest batch on top
  assert.equal(await page.locator('#summary').innerText(), '3 sent');
  await shot(page, '02-files-sent');
});

await step('same file twice never overwrites', async () => {
  await page.setInputFiles('#files', [sample('notes.txt')]);
  await waitForSent(page, 4);
  assert.ok(inbox().includes('notes (1).txt'));
});

console.log('\nScan Document');
await step('photo → page found, flattened and saved to the PC; nothing about it is stored on the phone', async () => {
  await page.setInputFiles('#camera', sample('a4-tilted.jpg'));
  await page.waitForSelector('#view-result:not([hidden])', { timeout: 30000 });
  await page.waitForFunction(() => document.querySelector('#resultImage').naturalWidth > 0);
  assert.equal(scans().length, 1);
  assert.equal(await page.locator('#resultTitle').innerText(), 'Scan 1');
  assert.equal(await page.locator('#resultStatus').innerText(), '✓ Saved to your PC'.replace('✓ ', ''));
  assert.match(scans()[0], /^Scan \d{4}-\d{2}-\d{2} at \d{2}\.\d{2}\.\d{2}\.jpg$/);
  assert.ok(await visible(page, '#adjust') && await visible(page, '#rotate') && await visible(page, '#nextPage'));
  const dims = await page.evaluate(() => ({ w: document.querySelector('#resultImage').naturalWidth, h: document.querySelector('#resultImage').naturalHeight }));
  assert.ok(Math.abs(dims.w / dims.h - 210 / 297) < 0.03, `preview aspect ${dims.w}x${dims.h}`);
  await shot(page, '03-scan-result-light');
  // no cached copies of private scan images
  const headers = await (await page.request.get(await page.locator('#resultImage').getAttribute('src'))).headers();
  assert.match(headers['cache-control'], /no-store/);
});

await step('look: Black & White replaces the same file on the PC', async () => {
  const [name] = scans();
  const before = sizeOf(name);
  await page.click('[data-filter="bw"]');
  await page.waitForFunction(() => document.querySelector('[data-filter="bw"]').getAttribute('aria-checked') === 'true');
  await page.waitForFunction(() => document.querySelector('#resultBusy').hidden);
  assert.deepEqual(scans(), [name]);
  assert.notEqual(sizeOf(name), before);
  await shot(page, '04-scan-bw');
  await page.click('[data-filter="auto"]');
  await page.waitForFunction(() => document.querySelector('[data-filter="auto"]').getAttribute('aria-checked') === 'true');
  await page.waitForFunction(() => document.querySelector('#resultBusy').hidden);
});

await step('rotate turns the page a quarter turn', async () => {
  const before = await page.evaluate(() => [document.querySelector('#resultImage').naturalWidth, document.querySelector('#resultImage').naturalHeight]);
  await page.click('#rotate');
  await page.waitForFunction(w => document.querySelector('#resultImage').naturalHeight === w && document.querySelector('#resultBusy').hidden, before[0]);
  const after = await page.evaluate(() => [document.querySelector('#resultImage').naturalWidth, document.querySelector('#resultImage').naturalHeight]);
  assert.ok(Math.abs(after[0] - before[1]) <= 1 && Math.abs(after[1] - before[0]) <= 1);
  await page.click('#rotate'); await page.click('#rotate'); await page.click('#rotate'); // back upright
  await page.waitForFunction(w => document.querySelector('#resultImage').naturalWidth === w && document.querySelector('#resultBusy').hidden, before[0], { timeout: 30000 });
});

await step('adjust edges: handles can be dragged and the PC re-renders the same file', async () => {
  const [name] = scans();
  const before = sizeOf(name);
  await page.click('#adjust');
  await page.waitForSelector('#view-adjust:not([hidden])');
  await page.waitForFunction(() => document.querySelectorAll('#overlay g[role="slider"]').length === 8);
  await shot(page, '05-adjust-light');
  assert.ok(await page.locator('#adjustDone').isEnabled());

  const rect = await page.locator('#overlay').boundingBox();
  const photo = await page.locator('#photoWrap').boundingBox();
  assert.ok(rect.width <= photo.width + 1 && rect.height <= photo.height + 1, 'overlay sits exactly on the photo');

  // drag the top-left corner inwards and bend the left edge
  await dragHandle(page, 'Move top left corner', 40, 60);
  await dragHandle(page, 'Bend left edge', -25, 0);
  await page.click('#adjustDone');
  await page.waitForSelector('#view-result:not([hidden])');
  await page.waitForFunction(() => document.querySelector('#resultBusy').hidden);
  assert.deepEqual(scans(), [name]);
  assert.notEqual(sizeOf(name), before);
});

await step('adjust: Reset and Straight Edges work; Cancel leaves the scan alone', async () => {
  const [name] = scans();
  const before = sizeOf(name);
  await page.click('#adjust');
  await page.waitForFunction(() => document.querySelectorAll('#overlay g[role="slider"]').length === 8);
  const path1 = await page.locator('#overlay .outline').getAttribute('d');
  await page.click('#straightenEdges');
  const path2 = await page.locator('#overlay .outline').getAttribute('d');
  assert.notEqual(path1, path2);
  await page.click('#resetEdges');
  assert.equal(await page.locator('#overlay .outline').getAttribute('d'), path1);
  await page.click('#adjustCancel');
  await page.waitForSelector('#view-result:not([hidden])');
  assert.equal(sizeOf(name), before);
});

await step('scan next page → Scan 2, then Done leaves both in the list with thumbnails', async () => {
  await page.click('#nextPage'); // opens the camera (a click on the hidden input); the test then supplies the photo
  await page.setInputFiles('#camera', sample('receipt-curled.jpg'));
  await page.waitForFunction(() => document.querySelector('#resultTitle').textContent === 'Scan 2', null, { timeout: 30000 });
  await page.waitForFunction(() => document.querySelector('#resultImage').naturalWidth > 0);
  assert.equal(scans().length, 2);
  const dims = await page.evaluate(() => ({ w: document.querySelector('#resultImage').naturalWidth, h: document.querySelector('#resultImage').naturalHeight }));
  assert.ok(dims.h / dims.w > 2.4, `a receipt is tall and narrow: ${dims.w}x${dims.h}`);
  await shot(page, '06-receipt-result');
  await page.click('#done');
  await page.waitForSelector('#sheet', { state: 'hidden' });
  await page.waitForFunction(() => [...document.querySelectorAll('#queue .thumb img')].filter(img => img.naturalWidth > 0).length >= 2);
  await shot(page, '07-home-with-scans');
});

await step('tapping a finished scan reopens it', async () => {
  await page.locator('#queue .row[data-openable]').first().click();
  await page.waitForSelector('#view-result:not([hidden])');
  await page.waitForFunction(() => document.querySelector('#resultImage').naturalWidth > 0);
  assert.match(await page.locator('#resultTitle').innerText(), /Scan [12]/);
});

await step('retake removes the old page from the PC and takes its place', async () => {
  const before = scans();
  const title = await page.locator('#resultTitle').innerText();
  await page.click('#retake');
  await page.waitForSelector('#view-wait:not([hidden])');
  await shot(page, '08-retake-wait');
  await page.setInputFiles('#camera', sample('a4-tilted.jpg'));
  await page.waitForSelector('#view-result:not([hidden])', { timeout: 30000 });
  await page.waitForFunction(t => document.querySelector('#resultTitle').textContent === t, title);
  const after = scans();
  assert.equal(after.length, before.length, 'same number of scans');
  assert.equal(after.filter(name => !before.includes(name)).length, 1, 'one new');
  assert.equal(before.filter(name => !after.includes(name)).length, 1, 'one gone');
  await page.click('#done');
});

console.log('\nWhen detection is unsure');
await step('opens the edge editor straight away, with a clear instruction', async () => {
  await page.setInputFiles('#camera', sample('no-page.jpg'));
  await page.waitForSelector('#view-adjust:not([hidden])', { timeout: 30000 });
  await page.waitForFunction(() => document.querySelectorAll('#overlay g[role="slider"]').length === 8);
  assert.match(await page.locator('#adjustHint').innerText(), /couldn’t find the page edges for sure/);
  await shot(page, '09-adjust-unsure');
  await page.click('#adjustCancel');
  await page.waitForSelector('#view-result:not([hidden])');
  await page.click('#done');
});

console.log('\nProblems');
await step('an unreadable photo explains itself and offers to send it as-is', async () => {
  const sentBefore = await page.locator('#queue .row[data-state="sent"]').count();
  await page.setInputFiles('#camera', sample('not-a-photo.jpg'));
  await page.waitForSelector('#busyActions:not([hidden])', { timeout: 30000 });
  assert.match(await page.locator('#busyText').innerText(), /doesn.t look like a photo/);
  assert.ok(await visible(page, '#busyRetry') && await visible(page, '#busyAsIs'));
  await shot(page, '10-scan-error');
  await page.click('#busyAsIs');
  await page.waitForSelector('#sheet', { state: 'hidden' });
  await waitForSent(page, sentBefore + 1);
  assert.ok(inbox().includes('not-a-photo.jpg'));
  assert.equal(await page.locator('#queue .row[data-state="failed"]').count(), 0, 'the failed scan attempt leaves no stray row');
});

await step('losing the PC shows a banner; coming back clears it', async () => {
  await page.context().setOffline(true);
  await page.waitForSelector('#connection[data-state="offline"]', { timeout: 20000 });
  assert.ok(await visible(page, '#banner'));
  await shot(page, '11-offline');
  await page.context().setOffline(false);
  await page.click('#connection');
  await page.waitForSelector('#connection[data-state="online"]', { timeout: 20000 });
  assert.ok(await page.locator('#banner').isHidden());
});

await step('no console errors or CSP violations', async () => {
  assert.deepEqual(page.problems.filter(problem => !/Failed to load resource.*(415|400)/.test(problem)), []);
});

console.log('\nAppearance');
await step('dark mode and very large text still fit', async () => {
  const dark = await newPage('dark');
  await shot(dark, '12-home-dark');
  await dark.setInputFiles('#camera', sample('a4-tilted.jpg'));
  await dark.waitForSelector('#view-result:not([hidden])', { timeout: 30000 });
  await dark.waitForFunction(() => document.querySelector('#resultImage').naturalWidth > 0);
  await shot(dark, '13-result-dark');
  await dark.click('#adjust');
  await dark.waitForFunction(() => document.querySelectorAll('#overlay g[role="slider"]').length === 8);
  await shot(dark, '14-adjust-dark');
  await dark.click('#adjustCancel');
  await dark.click('#done');

  // Accessibility text size (Dynamic Type "Larger Accessibility Sizes" is roughly 1.6x).
  await dark.evaluate(() => document.documentElement.style.setProperty('font-size', '27px', 'important'));
  await shot(dark, '15-home-large-text');
  assert.deepEqual(await overflowing(dark), [], 'nothing runs off the screen at large text');
  await dark.click('#library'); // just to exercise the button; the file picker is not shown in headless mode
  assert.deepEqual(dark.problems.filter(problem => !/Failed to load resource/.test(problem)), []);
});

await step('landscape iPhone', async () => {
  const context = await browser.newContext({ ...phone, viewport: { width: 844, height: 390 }, screen: { width: 844, height: 390 }, baseURL: BASE });
  const landscape = await context.newPage();
  currentPage = landscape;
  await landscape.goto('/');
  await landscape.waitForSelector('#connection[data-state="online"]');
  await shot(landscape, '16-home-landscape');
  assert.deepEqual(await overflowing(landscape), []);
  await landscape.setInputFiles('#camera', sample('a4-tilted.jpg'));
  await landscape.waitForSelector('#view-result:not([hidden])', { timeout: 30000 });
  await landscape.waitForFunction(() => document.querySelector('#resultImage').naturalWidth > 0);
  await shot(landscape, '17-result-landscape');
  const preview = await landscape.locator('#resultImage').boundingBox();
  assert.ok(preview.height > 250, `page preview is big enough to read in landscape (${Math.round(preview.height)}px tall)`);
  // every control reachable without scrolling the sheet
  for (const selector of ['#nextPage', '#done', '#retake', '#adjust']) {
    const box = await landscape.locator(selector).boundingBox();
    assert.ok(box && box.y >= 0 && box.y + box.height <= 390, `${selector} on screen in landscape`);
  }
});

// ---------- done ----------

await browser.close();
host.kill();
const failed = results.filter(result => !result.ok);
console.log(`\n${results.length - failed.length}/${results.length} passed. Screenshots: ${SHOTS}`);
fs.rmSync(INBOX, { recursive: true, force: true });
process.exit(failed.length ? 1 : 0);
