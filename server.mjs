import http from 'node:http';
import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir, readdir, rename, rm, stat } from 'node:fs/promises';
import { extname, join, resolve, sep } from 'node:path';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import crypto from 'node:crypto';
import QRCode from 'qrcode';

const root = fileURLToPath(new URL('.', import.meta.url));
const publicDir = join(root, 'public');
const inboxDir = resolve(process.env.INBOX_DIR || join(root, 'inbox'));
const host = process.env.HOST || '127.0.0.1';
const port = Number.parseInt(process.env.PORT || '8787', 10);
const maxFileSize = parseSize(process.env.MAX_FILE_SIZE || '5GB');
const activeParts = new Set();
const reservedNames = new Set();
const sockets = new Set();
let allocationQueue = Promise.resolve();
let shuttingDown = false;

if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('PORT must be between 1 and 65535');
await mkdir(inboxDir, { recursive: true });
await removeStaleUploads();

const mimeTypes = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.svg': 'image/svg+xml'
};

const server = http.createServer(async (req, res) => {
  try {
    setSecurityHeaders(res);
    const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
    if (req.method === 'GET' && url.pathname === '/api/health') return json(res, 200, { ready: true });
    if (req.method === 'GET' && url.pathname === '/api/qr') return generateQr(req, res);
    if (req.method === 'POST' && url.pathname === '/api/upload') return upload(req, res);
    if (url.pathname.startsWith('/api/')) return json(res, 404, { error: 'Not found' });
    if (req.method !== 'GET' && req.method !== 'HEAD') return json(res, 405, { error: 'Method not allowed' });
    return serveStatic(req, res, url);
  } catch (error) {
    console.error(error);
    if (!res.headersSent) json(res, error.statusCode || 500, { error: error.publicMessage || 'Upload failed' });
    else res.destroy();
  }
});

server.on('connection', socket => {
  sockets.add(socket);
  socket.on('close', () => sockets.delete(socket));
});

server.on('error', error => {
  console.error(`LISTEN_ERROR:${error.code || 'UNKNOWN'}:${error.message}`);
  process.exit(1);
});

server.listen(port, host, () => {
  console.log(`READY:http://${host}:${port}`);
  console.log(`INBOX:${inboxDir}`);
});

async function generateQr(req, res) {
  const address = `http://${req.headers.host || `${host}:${port}`}`;
  const qr = await QRCode.toDataURL(address, {
    width: 360, margin: 2, errorCorrectionLevel: 'M',
    color: { dark: '#151714', light: '#ffffff' }
  });
  json(res, 200, { qr, url: address });
}

async function upload(req, res) {
  const rawName = decodeHeader(req.headers['x-file-name']);
  if (!rawName) return json(res, 400, { error: 'Missing file name' });
  const lengthHeader = req.headers['content-length'];
  const length = lengthHeader === undefined ? null : Number(lengthHeader);
  if (length !== null && (!Number.isFinite(length) || length < 0)) return json(res, 400, { error: 'Invalid file size' });
  if (length !== null && length > maxFileSize) return json(res, 413, { error: `File exceeds ${formatBytes(maxFileSize)} limit` });

  const name = await reserveAvailableName(sanitizeName(rawName));
  const finalPath = safeInboxPath(name);
  const tempPath = join(inboxDir, `.taildrop-${crypto.randomUUID()}.part`);
  activeParts.add(tempPath);
  let received = 0;
  const limit = async function* (source) {
    for await (const chunk of source) {
      received += chunk.length;
      if (received > maxFileSize) {
        throw Object.assign(new Error('File too large'), { statusCode: 413, publicMessage: `File exceeds ${formatBytes(maxFileSize)} limit` });
      }
      yield chunk;
    }
  };

  try {
    await pipeline(req, limit, createWriteStream(tempPath, { flags: 'wx', highWaterMark: 1024 * 1024 }));
    await rename(tempPath, finalPath);
    activeParts.delete(tempPath);
    return json(res, 201, { name, size: received });
  } catch (error) {
    await rm(tempPath, { force: true }).catch(() => {});
    activeParts.delete(tempPath);
    throw error;
  } finally {
    reservedNames.delete(name);
  }
}

async function serveStatic(req, res, url) {
  const requested = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
  if (!['index.html', 'app.js', 'styles.css', 'favicon.svg'].includes(requested)) return json(res, 404, { error: 'Not found' });
  const path = join(publicDir, requested);
  const info = await stat(path);
  res.writeHead(200, { 'Content-Type': mimeTypes[extname(path)] || 'application/octet-stream', 'Content-Length': info.size, 'Cache-Control': 'no-cache' });
  if (req.method === 'HEAD') return res.end();
  createReadStream(path).pipe(res);
}

function sanitizeName(name) {
  const cleaned = name.replace(/[<>:"/\\|?*\u0000-\u001F]/g, '_').replace(/\.\.+/g, '_').replace(/[. ]+$/g, '').trim().slice(0, 180);
  return cleaned && cleaned !== '.' && cleaned !== '..' ? cleaned : `upload-${Date.now()}`;
}

function safeInboxPath(name) {
  if (name !== sanitizeName(name)) throw Object.assign(new Error('Unsafe file name'), { statusCode: 400, publicMessage: 'Invalid file name' });
  const path = resolve(inboxDir, name);
  if (!path.startsWith(inboxDir + sep)) throw Object.assign(new Error('Unsafe path'), { statusCode: 400, publicMessage: 'Invalid file name' });
  return path;
}

async function availableName(name) {
  const extension = extname(name);
  const stem = name.slice(0, name.length - extension.length);
  for (let i = 0; i < 10000; i++) {
    const candidate = i === 0 ? name : `${stem} (${i})${extension}`;
    if (reservedNames.has(candidate)) continue;
    try { await stat(join(inboxDir, candidate)); }
    catch (error) {
      if (error.code === 'ENOENT') { reservedNames.add(candidate); return candidate; }
      throw error;
    }
  }
  const candidate = `${stem}-${crypto.randomUUID()}${extension}`;
  reservedNames.add(candidate);
  return candidate;
}

function reserveAvailableName(name) {
  const allocation = allocationQueue.then(() => availableName(name));
  allocationQueue = allocation.catch(() => {});
  return allocation;
}

function setSecurityHeaders(res) {
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('X-Frame-Options', 'DENY');
  res.setHeader('Referrer-Policy', 'no-referrer');
  res.setHeader('Content-Security-Policy', "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'");
}

function json(res, status, body) {
  const data = Buffer.from(JSON.stringify(body));
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': data.length, 'Cache-Control': 'no-store' });
  res.end(data);
}

function decodeHeader(value) {
  if (!value || Array.isArray(value)) return '';
  try { return decodeURIComponent(value); } catch { return ''; }
}

function parseSize(value) {
  const match = String(value).trim().match(/^(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB)?$/i);
  if (!match) throw new Error('MAX_FILE_SIZE must look like 500MB or 5GB');
  const units = { B: 1, KB: 1024, MB: 1024 ** 2, GB: 1024 ** 3, TB: 1024 ** 4 };
  return Math.floor(Number(match[1]) * units[(match[2] || 'B').toUpperCase()]);
}

function formatBytes(bytes) {
  if (!bytes) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** i).toFixed(i ? 1 : 0)} ${units[i]}`;
}

async function removeStaleUploads() {
  const entries = await readdir(inboxDir, { withFileTypes: true });
  await Promise.all(entries.filter(entry => entry.isFile() && entry.name.startsWith('.taildrop-') && entry.name.endsWith('.part')).map(entry => rm(join(inboxDir, entry.name), { force: true })));
}

async function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  server.close();
  for (const socket of sockets) socket.destroy();
  await Promise.all([...activeParts].map(path => rm(path, { force: true }).catch(() => {})));
  process.exit(0);
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
