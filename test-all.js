import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const port = 18787 + Math.floor(Math.random() * 1000);
const base = `http://127.0.0.1:${port}`;
const inbox = await mkdtemp(join(tmpdir(), 'taildrop-test-'));
const child = spawn(process.execPath, ['server.mjs'], {
  cwd: fileURLToPath(new URL('.', import.meta.url)),
  env: { ...process.env, HOST: '127.0.0.1', PORT: String(port), INBOX_DIR: inbox },
  stdio: ['ignore', 'pipe', 'pipe']
});
let output = '';
child.stdout.on('data', chunk => { output += chunk; });
child.stderr.on('data', chunk => { output += chunk; });

const tests = [];
function test(name, action) { tests.push({ name, action }); }

test('server becomes ready', async () => {
  const response = await fetch(`${base}/api/health`);
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { ready: true });
});

test('phone page has exactly one file picker', async () => {
  const html = await (await fetch(base)).text();
  assert.match(html, /<input id="files" type="file" multiple hidden>/);
  assert.equal((html.match(/type="file"/g) || []).length, 1);
  assert.doesNotMatch(html, /Recent files|Delete|Photo \/ video|Scan/);
});

test('QR contains the receiver address', async () => {
  const response = await fetch(`${base}/api/qr`);
  assert.equal(response.status, 200);
  const data = await response.json();
  assert.equal(data.url, base);
  assert.match(data.qr, /^data:image\/png;base64,/);
});

test('10 MB streams intact', async () => {
  const payload = Buffer.alloc(10 * 1024 * 1024, 0x5a);
  const started = performance.now();
  const response = await fetch(`${base}/api/upload`, {
    method: 'POST',
    headers: { 'X-File-Name': encodeURIComponent('ten-megabytes.bin'), 'Content-Length': String(payload.length) },
    body: payload
  });
  assert.equal(response.status, 201);
  assert.deepEqual(await response.json(), { name: 'ten-megabytes.bin', size: payload.length });
  assert.deepEqual(await readFile(join(inbox, 'ten-megabytes.bin')), payload);
  console.log(`    local 10 MB transfer: ${(performance.now() - started).toFixed(0)} ms`);
});

test('duplicate names never overwrite', async () => {
  const response = await fetch(`${base}/api/upload`, {
    method: 'POST', headers: { 'X-File-Name': 'ten-megabytes.bin' }, body: 'different'
  });
  assert.equal(response.status, 201);
  const data = await response.json();
  assert.equal(data.name, 'ten-megabytes (1).bin');
  assert.equal((await readFile(join(inbox, 'ten-megabytes.bin'))).length, 10 * 1024 * 1024);
});

test('unsafe names stay inside the inbox', async () => {
  const response = await fetch(`${base}/api/upload`, {
    method: 'POST', headers: { 'X-File-Name': encodeURIComponent('../../bad?.txt') }, body: 'safe'
  });
  assert.equal(response.status, 201);
  const data = await response.json();
  assert.ok(!data.name.includes('/') && !data.name.includes('..'));
});

test('removed file-management API stays removed', async () => {
  assert.equal((await fetch(`${base}/api/files`)).status, 404);
  assert.equal((await fetch(`${base}/api/files/example`, { method: 'DELETE' })).status, 404);
});

async function waitUntilReady() {
  for (let i = 0; i < 60; i++) {
    if (child.exitCode !== null) throw new Error(`Server exited early.\n${output}`);
    try { if ((await fetch(`${base}/api/health`)).ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error(`Server did not start.\n${output}`);
}

let failed = 0;
try {
  await waitUntilReady();
  for (const { name, action } of tests) {
    try { await action(); console.log(`PASS ${name}`); }
    catch (error) { failed++; console.error(`FAIL ${name}\n  ${error.stack || error}`); }
  }
} finally {
  child.kill('SIGTERM');
  await Promise.race([
    new Promise(resolve => child.once('exit', resolve)),
    new Promise(resolve => setTimeout(resolve, 2000))
  ]);
  const leftovers = (await readdir(inbox)).filter(name => name.endsWith('.part'));
  if (leftovers.length) { failed++; console.error(`FAIL partial uploads remain: ${leftovers.join(', ')}`); }
  await rm(inbox, { recursive: true, force: true });
}

console.log(`\n${tests.length - failed}/${tests.length} tests passed`);
process.exitCode = failed ? 1 : 0;
