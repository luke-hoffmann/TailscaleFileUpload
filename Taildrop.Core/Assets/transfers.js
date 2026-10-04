// Shared by "Send Photos or Files" and "Scan Document": the request helper, the upload queue,
// the "Sent to PC" list and the connection indicator.

const MAX_PARALLEL = 2;          // files in flight at once (Safari allows only 6 connections per host anyway)
const STALL_MS = 30_000;         // abort when no bytes have moved for this long...
const PROCESSING_MS = 90_000;    // ...or when the PC has not answered this long after receiving everything

const $ = selector => document.querySelector(selector);
const section = $('#transfers');
const list = $('#queue');
const summary = $('#summary');
const emptyNote = $('#empty');
const keepOpen = $('#keepOpen');
const pill = $('#connection');
const pillText = pill.querySelector('span');
const banner = $('#banner');

// ---------- requests ----------

export class RequestError extends Error {
  /** @param {'network'|'timeout'|'abort'|'http'} kind */
  constructor(kind, status = 0, message = '') {
    super(message || kind);
    this.kind = kind;
    this.status = status;
  }
  /** Something the user can read. */
  get friendly() {
    if (this.kind === 'http' && this.message) return this.message;
    if (this.kind === 'timeout') return 'The PC stopped responding';
    if (this.kind === 'network') return 'Lost connection to your PC';
    if (this.kind === 'abort') return 'Cancelled';
    return 'Something went wrong';
  }
}

/**
 * XMLHttpRequest wrapper (fetch cannot report upload progress). Resolves with { status, data } for 2xx,
 * rejects with a RequestError otherwise. The returned promise has an abort() method.
 * The watchdog restarts whenever bytes move, so slow-but-working transfers are never cut off.
 */
export function request(method, url, { body = null, headers = {}, onUpload, onSent } = {}) {
  const xhr = new XMLHttpRequest();
  let timer = 0;
  let timedOut = false;

  const promise = new Promise((resolve, reject) => {
    const arm = ms => {
      clearTimeout(timer);
      timer = setTimeout(() => { timedOut = true; xhr.abort(); }, ms);
    };
    xhr.open(method, url);
    for (const [name, value] of Object.entries(headers)) xhr.setRequestHeader(name, value);
    xhr.upload.onprogress = event => { arm(STALL_MS); onUpload?.(event); };
    xhr.upload.onload = () => { arm(PROCESSING_MS); onSent?.(); };
    xhr.onload = () => {
      clearTimeout(timer);
      let data = null;
      try { data = JSON.parse(xhr.responseText); } catch { /* not JSON */ }
      if (xhr.status >= 200 && xhr.status < 300) resolve({ status: xhr.status, data });
      else reject(new RequestError('http', xhr.status, data?.error || ''));
    };
    xhr.onerror = () => { clearTimeout(timer); reject(new RequestError('network')); };
    xhr.onabort = () => { clearTimeout(timer); reject(new RequestError(timedOut ? 'timeout' : 'abort')); };
    arm(STALL_MS);
    xhr.send(body);
  });
  promise.abort = () => xhr.abort();
  return promise;
}

/** JSON request to a state-changing scan endpoint. */
export function api(method, url, payload) {
  return request(method, url, {
    body: payload === undefined ? null : JSON.stringify(payload),
    headers: { 'X-Taildrop': '1', ...(payload === undefined ? {} : { 'Content-Type': 'application/json' }) }
  });
}

// ---------- connection ----------

export const connection = { online: null, scan: true };
const connectionListeners = new Set();
export function onConnectionChange(listener) { connectionListeners.add(listener); }

function setConnection(online, scan = connection.scan) {
  connection.online = online;
  connection.scan = scan;
  pill.dataset.state = online ? 'online' : 'offline';
  pillText.textContent = online ? 'Connected' : 'Not connected';
  banner.hidden = online;
  connectionListeners.forEach(listener => listener(connection));
}

export async function checkConnection() {
  try {
    const response = await fetch('/api/health', { cache: 'no-store' });
    const data = await response.json();
    setConnection(response.ok, data.scan !== false);
  } catch {
    setConnection(false);
  }
}

pill.addEventListener('click', () => {
  pill.dataset.state = 'checking';
  pillText.textContent = 'Checking…';
  checkConnection();
});
document.addEventListener('visibilitychange', () => { if (!document.hidden) checkConnection(); });
window.addEventListener('online', checkConnection);
window.addEventListener('offline', () => setConnection(false));
setInterval(() => { if (!document.hidden) checkConnection(); }, 15_000);

// ---------- formatting ----------

export function formatBytes(value) {
  if (value >= 1024 ** 3) return `${(value / 1024 ** 3).toFixed(1)} GB`;
  if (value >= 1024 ** 2) return `${(value / 1024 ** 2).toFixed(1)} MB`;
  if (value >= 1024) return `${Math.round(value / 1024)} KB`;
  return `${value} B`;
}

function formatSpeed(bytesPerSecond) {
  return bytesPerSecond >= 1024 ** 2
    ? `${(bytesPerSecond / 1024 ** 2).toFixed(1)} MB/s`
    : `${Math.max(1, Math.round(bytesPerSecond / 1024))} KB/s`;
}

// ---------- jobs ----------

const FILE_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 3h8l5 5v11a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z"/><path d="M14 3v5h5"/></svg>';
const PHOTO_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="3"/><circle cx="9" cy="10" r="1.7"/><path d="m4 18 5-5 4 4 3-3 4 4"/></svg>';
const CHEVRON = '<svg class="chevron" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m9 5 7 7-7 7"/></svg>';

const jobs = [];
let active = 0;
let openScan = null;

/** scan.js registers how to reopen a finished scan when its row is tapped. */
export function setScanOpener(handler) { openScan = handler; }

function makeJob(fields) {
  const job = {
    kind: 'file', state: 'queued', phase: '', name: '', blob: null,
    loaded: 0, total: 0, size: 0, speed: '', message: '', scan: null, abort: null,
    ...fields
  };
  job.li = document.createElement('li');
  job.li.className = 'row';
  job.li.innerHTML = '<div class="thumb"></div><div class="meta"><div class="name"></div><div class="status"></div></div><div class="trailing"></div><div class="bar" hidden><span></span></div>';
  return job;
}

function addJobs(newJobs) {
  const anchor = list.firstChild; // newest batch on top, selection order kept within the batch
  for (const job of newJobs) {
    jobs.push(job);
    list.insertBefore(job.li, anchor);
    render(job);
  }
  refresh();
}

export function removeJob(job) {
  const index = jobs.indexOf(job);
  if (index >= 0) jobs.splice(index, 1);
  job.li.remove();
  refresh();
}

function statusText(job) {
  switch (job.state) {
    case 'queued': return 'Waiting…';
    case 'sending':
      if (job.kind === 'scan') {
        return job.phase === 'processing' ? 'Finding the page edges…' : `Uploading photo… ${percent(job)}%`;
      }
      return job.total ? `${percent(job)}% · ${formatBytes(job.loaded)} of ${formatBytes(job.total)}` : `${formatBytes(job.loaded)} sent`;
    case 'sent':
      if (job.kind === 'scan') return job.scan?.adjustable === false ? 'Sent · tap to review' : 'Sent · tap to adjust';
      return `Sent · ${formatBytes(job.size)}${job.speed ? ` · ${job.speed}` : ''}`;
    case 'failed': return job.message || 'Failed';
    default: return '';
  }
}

function percent(job) { return job.total ? Math.round((job.loaded / job.total) * 100) : 0; }

function button(label, className, handler) {
  const element = document.createElement('button');
  element.type = 'button';
  element.className = `row-btn ${className}`;
  element.textContent = label;
  element.addEventListener('click', event => { event.stopPropagation(); handler(); });
  return element;
}

export function render(job) {
  const li = job.li;
  li.dataset.state = job.state;
  const name = li.querySelector('.name');
  const shown = job.kind === 'scan' ? job.name.replace(/\.jpe?g$/i, '') : job.name; // scans are always JPEGs; the extension is noise here
  name.textContent = shown;
  name.title = job.name;
  li.querySelector('.status').textContent = statusText(job);

  // thumbnail
  const thumb = li.querySelector('.thumb');
  const wanted = job.scan ? job.scan.previewUrl : '';
  if (wanted) {
    let img = thumb.querySelector('img');
    if (!img) {
      thumb.textContent = '';
      img = document.createElement('img');
      img.alt = '';
      img.decoding = 'async';
      thumb.append(img);
    }
    if (img.dataset.src !== wanted) { img.dataset.src = wanted; img.src = wanted; }
  } else if (!thumb.firstChild) {
    thumb.innerHTML = job.blob && job.blob.type.startsWith('image/') ? PHOTO_ICON : FILE_ICON;
  }

  // progress bar
  const bar = li.querySelector('.bar');
  const showBar = job.state === 'sending' || job.state === 'queued';
  bar.hidden = !showBar;
  const processing = job.kind === 'scan' && job.phase === 'processing';
  bar.classList.toggle('indeterminate', processing);
  bar.firstElementChild.style.width = processing ? '' : `${job.state === 'queued' ? 0 : percent(job)}%`;
  li.setAttribute('aria-busy', String(showBar));

  // trailing controls
  const trailing = li.querySelector('.trailing');
  trailing.textContent = '';
  if (job.state === 'queued' || job.state === 'sending') {
    if (job.kind === 'file') trailing.append(button('Cancel', 'quiet', () => cancel(job)));
  } else if (job.state === 'failed') {
    trailing.append(button('Retry', '', () => retry(job)), button('Remove', 'quiet', () => removeJob(job)));
  } else if (job.scan) {
    trailing.insertAdjacentHTML('beforeend', CHEVRON);
  }

  // tapping a finished scan reopens it
  const openable = job.state === 'sent' && !!job.scan;
  if (openable) {
    li.dataset.openable = '';
    li.tabIndex = 0;
    li.setAttribute('role', 'button');
    li.setAttribute('aria-label', `${job.name}. ${job.scan.adjustable === false ? 'Tap to review.' : 'Tap to adjust.'}`);
    li.onclick = () => openScan?.(job);
    li.onkeydown = event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); openScan?.(job); } };
  } else {
    delete li.dataset.openable;
    li.removeAttribute('tabindex');
    li.removeAttribute('role');
    li.removeAttribute('aria-label');
    li.onclick = null;
    li.onkeydown = null;
  }
}

function refresh() {
  const sending = jobs.filter(job => job.state === 'queued' || job.state === 'sending').length;
  const sent = jobs.filter(job => job.state === 'sent').length;
  const failed = jobs.filter(job => job.state === 'failed').length;
  section.hidden = jobs.length === 0;
  emptyNote.hidden = jobs.length > 0;
  keepOpen.hidden = sending === 0;
  const parts = [];
  if (sending) parts.push(`${sending} sending`);
  if (sent) parts.push(`${sent} sent`);
  if (failed) parts.push(`${failed} failed`);
  summary.textContent = parts.join(' · ');
}

// ---------- files ----------

/** Queue files for upload (the same path for photos, videos and anything else). */
export function sendFiles(files) {
  const created = [...files].map(file => makeJob({
    kind: 'file', blob: file, size: file.size, total: file.size,
    name: file.name || `file-${Date.now()}`
  }));
  if (!created.length) return;
  addJobs(created);
  pump();
}

function pump() {
  for (const job of jobs) {
    if (active >= MAX_PARALLEL) break;
    if (job.kind === 'file' && job.state === 'queued') runFile(job);
  }
}

function runFile(job) {
  active++;
  job.state = 'sending';
  job.loaded = 0;
  render(job);
  refresh();
  const started = performance.now();

  const upload = request('POST', '/api/upload', {
    body: job.blob,
    headers: { 'X-File-Name': encodeURIComponent(job.name), 'Content-Type': job.blob.type || 'application/octet-stream' },
    onUpload: event => {
      job.loaded = event.loaded;
      if (event.lengthComputable) job.total = event.total;
      render(job);
    }
  });
  job.abort = upload.abort;

  upload
    .then(() => {
      const seconds = Math.max((performance.now() - started) / 1000, 0.001);
      job.state = 'sent';
      job.loaded = job.total = job.size;
      job.speed = formatSpeed(job.size / seconds);
    })
    .catch(error => fail(job, error))
    .finally(() => {
      active--;
      job.abort = null;
      render(job);
      refresh();
      pump();
    });
}

function fail(job, error) {
  if (error.kind === 'abort') { removeJob(job); return; }
  job.state = 'failed';
  job.message = error.friendly;
  // Only a network-level failure says anything about the PC; a 4xx/5xx means it is there and said no.
  if (error.kind === 'network' || error.kind === 'timeout') checkConnection();
}

function cancel(job) {
  if (job.abort) job.abort(); else removeJob(job);
}

function retry(job) {
  job.state = 'queued';
  job.message = '';
  job.loaded = 0;
  render(job);
  refresh();
  pump();
}

// ---------- scans ----------

/**
 * Sends a photo to be scanned. Returns { job, promise }; the promise resolves with the scan the PC made
 * ({ id, name, outline, previewUrl, ... }) or rejects with a RequestError. While in flight the job shows in
 * the list; on failure the job is removed again (the scanner screen owns the error and the retry).
 */
export function startScan(file, filter = 'auto', { onProgress, onPhase } = {}) {
  const job = makeJob({ kind: 'scan', blob: file, name: 'Scanning…', size: file.size, total: file.size, phase: 'upload' });
  addJobs([job]);
  job.state = 'sending';
  render(job);
  refresh();

  const upload = request('POST', '/api/scan', {
    body: file,
    headers: { 'X-Taildrop': '1', 'X-Scan-Filter': filter, 'Content-Type': file.type || 'image/jpeg' },
    onUpload: event => {
      job.loaded = event.loaded;
      if (event.lengthComputable) job.total = event.total;
      render(job);
      onProgress?.(percent(job));
    },
    onSent: () => {
      // Everything is on the PC; from here it is finding the page.
      job.phase = 'processing';
      render(job);
      onPhase?.('processing');
    }
  });
  job.abort = upload.abort;

  const promise = upload
    .then(({ data }) => {
      finishScan(job, data);
      return data;
    })
    .catch(error => {
      removeJob(job);
      if (error.kind === 'network' || error.kind === 'timeout') checkConnection();
      throw error;
    });
  promise.abort = upload.abort;
  return { job, promise };
}

/** A page the live scanner has already saved on the PC: it joins the list as sent. */
export function addSavedScan(scan) {
  const job = makeJob({ kind: 'scan', name: scan.name, size: scan.size, total: scan.size });
  addJobs([job]);
  finishScan(job, scan);
  return job;
}

/** Marks the scan's row as finished and shows its (new) preview. */
export function finishScan(job, scan) {
  job.scan = scan;
  job.name = scan.name;
  job.size = scan.size;
  job.state = 'sent';
  job.phase = '';
  render(job);
  refresh();
}

/** Called by the scanner when the PC re-rendered a scan (new filter, edges, rotation). */
export function updateScan(job, scan) {
  job.scan = scan;
  job.name = scan.name;
  job.size = scan.size;
  render(job);
}

export function setPhase(job, phase) {
  job.phase = phase;
  render(job);
}

checkConnection();
