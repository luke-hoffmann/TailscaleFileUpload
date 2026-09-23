import { rm } from 'node:fs/promises';
import { basename, dirname, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const ownerPid = Number.parseInt(process.argv[2], 10);
const sessionPath = resolve(process.argv[3] || '');
const tempRoot = resolve(tmpdir());

if (!Number.isInteger(ownerPid) || ownerPid < 1 || dirname(sessionPath) !== tempRoot || !basename(sessionPath).startsWith('Taildrop-')) {
  process.exit(2);
}

const ownerIsRunning = () => {
  try { process.kill(ownerPid, 0); return true; }
  catch { return false; }
};

while (ownerIsRunning()) await new Promise(resolveWait => setTimeout(resolveWait, 250));

for (let attempt = 0; attempt < 20; attempt++) {
  try { await rm(sessionPath, { recursive: true, force: true, maxRetries: 2, retryDelay: 100 }); break; }
  catch { await new Promise(resolveWait => setTimeout(resolveWait, 250)); }
}
