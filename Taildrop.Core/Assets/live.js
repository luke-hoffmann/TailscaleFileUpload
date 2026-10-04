// Live scanning. The camera stays on; the PC finds the page as you point, takes a full-resolution frame once the
// page holds still, then builds a sharper page from close-ups: move the phone closer over the page and the amber
// areas on the little map fill in. When the page has enough detail it is saved to the PC's inbox and shown for a
// quick look. "Looks Good" goes straight back to the (still running) camera for the next page.
//
// One WebSocket (/api/live) carries it all. Every frame gets exactly one reply and only a frame or two is ever in
// flight, so every answer is about what the camera sees now. Messages: see TaildropServer.Live.cs.

import { addSavedScan, api, removeJob, updateScan } from './transfers.js';
import { closeSheet, onSheetClose, openSheetView, takePhoto } from './scan.js';

const $ = selector => document.querySelector(selector);
const view = $('#view-live');
const video = $('#liveVideo');
const overlay = $('#liveOverlay');
const outlineShape = $('#livePage');
const flash = $('#liveFlash');
const statusLine = $('#liveStatus');
const statusText = statusLine.firstElementChild;
const closeButton = $('#liveClose');
const countChip = $('#liveCount');
const stack = $('#liveStack');
const shutter = $('#liveShutter');
const steadyRing = $('#liveSteady');
const aimPanel = $('#liveAim');
const detailPanel = $('#liveDetail');
const map = $('#detailMap');
const mapImage = $('#detailMapImage');
const mapCells = $('#detailMapCells');
const percentText = $('#detailPercent');
const bar = $('#detailBar');
const finishButton = $('#liveFinish');
const review = $('#liveReview');
const reviewTitle = $('#liveReviewTitle');
const reviewImage = $('#liveReviewImage');
const reviewBusy = $('#liveReviewBusy');
const reviewStatus = $('#liveReviewStatus');
const reviewFilters = [...document.querySelectorAll('[data-live-filter]')];
const problem = $('#liveProblem');

const AIM_EDGE = 640;           // aim frames: plenty to find a page in, light on the network
const AIM_QUALITY = 0.7;
const FULL_QUALITY = 0.92;      // the page and its close-ups: as sharp as the camera gives
const STEADY_MS = 650;          // the outline holds still this long, then the page is taken
const STEADY_MOVE = 0.02;       // ...no corner moving more than this (fraction of the frame) meanwhile
const MIN_PAGE_AREA = 0.08;     // smaller than this in the frame: too far away to start from
const DETAIL_IN_FLIGHT = 2;     // close-ups: one being worked on by the PC, the next on its way
const REPLY_TIMEOUT_MS = 20000; // no answer this long: the connection is dead, whatever the socket says
const FILTER_KEY = 'taildrop.filter';
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');

// off | starting | connecting | aim | taking | detail | finishing | review | problem
let state = 'off';
let stream = null;
let socket = null;
let greeted = false;
let wakeLock = null;
let seq = 0;
const pending = new Map();      // seq -> 'aim' | 'start' | 'detail'
let lastTraffic = 0;
let grabbing = false;
let reconnectDelay = 500;
let reconnectTimer = 0;
let pingTimer = 0;
let page = null;                // latest answer: { corners, confident, same }
let drawn = null;               // the outline as drawn, easing toward page.corners
let steadyAnchor = null;
let steadySince = 0;
let pages = 0;                  // pages saved during this visit
let current = null;             // the page under review: { job, scan, busy }
let detail = null;              // { columns, rows, levels, footprint, progress }
let holdUntil = 0;              // a warning stays up at least until then

const aimCanvas = document.createElement('canvas');
const fullCanvas = document.createElement('canvas');

/** Live scanning needs the camera (secure pages only) and WebSockets. Over plain HTTP the photo flow is used. */
export function liveSupported() {
  return window.isSecureContext && !!navigator.mediaDevices?.getUserMedia && 'WebSocket' in window;
}

export async function openLive() {
  if (state !== 'off') return;
  pages = 0;
  updateCount();
  stack.hidden = true;
  review.hidden = true;
  problem.hidden = true;
  openSheetView('live');
  setState('starting');
  say('Starting camera…');
  connect();
  requestAnimationFrame(tick);
  keepAwake();
  await startCamera();
}

function stopLive() {
  if (state === 'off') return;
  setState('off');
  clearTimeout(reconnectTimer);
  clearInterval(pingTimer);
  if (socket) {
    const closing = socket;
    socket = null;
    closing.onclose = null;
    try { closing.close(1000, 'Closed'); } catch { /* already closed */ }
  }
  greeted = false;
  pending.clear();
  stopCamera();
  wakeLock?.release().catch(() => {});
  wakeLock = null;
  page = drawn = steadyAnchor = detail = current = null;
  review.hidden = true;
  problem.hidden = true;
}
onSheetClose(stopLive);

// ---------- state and messages ----------

function setState(next) {
  state = next;
  view.dataset.state = next;
  const building = next === 'detail' || next === 'finishing';
  aimPanel.hidden = building;
  detailPanel.hidden = !building;
  shutter.disabled = next !== 'aim';
  finishButton.disabled = next !== 'detail';
  map.classList.toggle('busy', next === 'finishing');
  closeButton.textContent = next === 'taking' || building ? 'Cancel' : pages ? 'Done' : 'Close';
  if (next !== 'aim') showSteady(0);
}

/** Status pill. A warning (hold > 0) is not replaced by routine hints until it has been read. */
function say(text, tone = '', hold = 0) {
  const now = performance.now();
  if (!hold && now < holdUntil) return;
  holdUntil = hold ? now + hold : 0;
  if (statusText.textContent !== text) statusText.textContent = text;
  statusLine.className = `live-status ${tone}`.trim();
}

function updateCount() {
  countChip.hidden = pages === 0;
  countChip.textContent = pages === 1 ? '1 page' : `${pages} pages`;
  if (state !== 'off') setState(state);
}

function savedFilter() {
  try { return localStorage.getItem(FILTER_KEY) || 'auto'; } catch { return 'auto'; }
}

// ---------- camera ----------

async function startCamera() {
  try {
    stream = await navigator.mediaDevices.getUserMedia({
      audio: false,
      video: { facingMode: { ideal: 'environment' }, width: { ideal: 3840 }, height: { ideal: 2160 }, frameRate: { ideal: 30, max: 30 } }
    });
  } catch (error) {
    if (state !== 'off') showProblem(error);
    return;
  }
  if (state === 'off') { stopCamera(); return; }
  video.srcObject = stream;
  const track = stream.getVideoTracks()[0];
  track.addEventListener('ended', () => { if (state !== 'off' && !document.hidden) restartCamera(); });
  // Continuous focus where the browser lets us ask (Safari already focuses continuously).
  try { await track.applyConstraints({ advanced: [{ focusMode: 'continuous' }] }); } catch { /* not supported */ }
  try { await video.play(); } catch { /* muted + playsinline: plays once the frames come */ }
  if (video.readyState < 2) await new Promise(resolve => video.addEventListener('loadeddata', resolve, { once: true }));
  if (state === 'starting') setState(greeted ? 'aim' : 'connecting');
  if (state === 'connecting') say('Connecting to your PC…');
  if (state === 'aim') say(pages ? 'Point the camera at the next page' : 'Point the camera at a page');
}

function stopCamera() {
  stream?.getTracks().forEach(track => track.stop());
  stream = null;
  video.srcObject = null;
}

async function restartCamera() {
  stopCamera();
  if (state === 'off' || state === 'problem') return;
  const resume = state;
  if (resume === 'taking' || resume === 'detail') {
    command('cancel');   // the camera went away mid-page: start that page over
    endPage();
  }
  setState('starting');
  await startCamera();
  if (state === 'aim' && resume === 'review') setState('review');
}

function showProblem(error) {
  setState('problem');
  const denied = error?.name === 'NotAllowedError' || error?.name === 'SecurityError';
  $('#liveProblemTitle').textContent = denied ? 'Camera access is off' : 'Camera unavailable';
  $('#liveProblemText').textContent = denied
    ? 'Allow camera access for this page (in Safari: tap aA in the address bar, then Website Settings), then try again.'
    : error?.name === 'NotFoundError'
      ? 'This device has no camera Taildrop can use. You can still take a photo.'
      : 'Another app may be using the camera. Close it, then try again.';
  problem.hidden = false;
}

$('#liveRetry').addEventListener('click', async () => {
  problem.hidden = true;
  setState('starting');
  say('Starting camera…');
  await startCamera();
});
$('#livePhoto').addEventListener('click', () => {
  closeSheet();
  takePhoto();   // still inside the tap, so iOS opens the camera
});

async function keepAwake() {
  try { wakeLock = await navigator.wakeLock?.request('screen'); } catch { /* not supported or not allowed */ }
}

document.addEventListener('visibilitychange', () => {
  if (state === 'off' || document.hidden) return;
  keepAwake();
  const track = stream?.getVideoTracks()[0];
  if (state !== 'problem' && (!track || track.readyState === 'ended' || track.muted)) restartCamera();
  else video.play().catch(() => {});
  if (!socket) connect();
});

// ---------- connection ----------

function connect() {
  clearTimeout(reconnectTimer);
  if (state === 'off') return;
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/live`);
  ws.binaryType = 'arraybuffer';
  socket = ws;
  greeted = false;
  ws.onmessage = event => {
    if (ws !== socket || typeof event.data !== 'string') return;
    try { handle(JSON.parse(event.data)); } catch (error) { console.error(error); }
  };
  ws.onclose = () => { if (ws === socket) disconnected(); };
}

function connected() { return greeted && socket?.readyState === WebSocket.OPEN; }

function disconnected() {
  socket = null;
  greeted = false;
  pending.clear();
  clearInterval(pingTimer);
  if (state === 'off') return;
  if (state === 'taking' || state === 'detail' || state === 'finishing') {
    endPage();
    say('Lost the connection to your PC. That page will start over.', 'warn', 3500);
  }
  if (['aim', 'taking', 'detail', 'finishing'].includes(state)) setState('connecting');
  if (state === 'connecting') say('Connecting to your PC…');
  reconnectTimer = setTimeout(connect, reconnectDelay);
  reconnectDelay = Math.min(reconnectDelay * 2, 5000);
}

function send(header, jpeg) {
  const json = new TextEncoder().encode(JSON.stringify(header));
  const length = new Uint8Array([json.length & 0xff, json.length >> 8]);
  socket.send(new Blob([length, json, jpeg]));
  pending.set(header.seq, header.type);
  lastTraffic = performance.now();
}

function command(type) {
  if (connected()) socket.send(JSON.stringify({ type }));
}

const inFlight = kind => [...pending.values()].filter(value => value === kind).length;

function handle(message) {
  lastTraffic = performance.now();
  const kind = message.seq === undefined ? null : pending.get(message.seq);
  if (message.seq !== undefined) pending.delete(message.seq);

  switch (message.type) {
    case 'ready':
      greeted = true;
      reconnectDelay = 500;
      clearInterval(pingTimer);
      pingTimer = setInterval(() => command('ping'), 15000);
      if (state === 'connecting') { setState('aim'); say(pages ? 'Point the camera at the next page' : 'Point the camera at a page'); }
      break;
    case 'aim':
      if (state === 'aim') onAim(message);
      break;
    case 'busy':
      break;     // a frame the PC had no use for (between pages, or busy): the next one goes out by itself
    case 'started':
      if (state === 'taking') onStarted(message);
      else command('cancel');
      break;
    case 'detail':
      if (state === 'detail') onDetail(message);
      break;
    case 'finishing':
      if (state === 'detail') setState('finishing');
      if (state === 'finishing') say('Finishing the page…', 'ok');
      break;
    case 'done':
      onDone(message.scan);
      break;
    case 'error':
      if (kind === 'start' && state === 'taking') {
        endPage();
        setState('aim');
        say(message.message || 'Couldn’t take that page. Try again.', 'warn', 2500);
      } else if (!kind && message.message) {
        console.warn('Live scanner:', message.message);
      }
      break;
  }
}

// ---------- the frame loop ----------

function tick(now) {
  if (state === 'off') return;
  requestAnimationFrame(tick);
  drawOutline();
  if (!connected() || grabbing || !video.videoWidth) return;

  if (pending.size && now - lastTraffic > REPLY_TIMEOUT_MS) {
    socket.close();      // silent connection: start a fresh one
    return;
  }
  if (state === 'aim') {
    updateSteady(now);
    if (state === 'aim' && !inFlight('aim')) sendFrame('aim');
  } else if (state === 'detail' && inFlight('detail') < DETAIL_IN_FLIGHT) {
    sendFrame('detail');
  }
}

/** Grabs the current camera picture as a JPEG: small for aiming, full resolution otherwise. */
async function grab(full) {
  const width = video.videoWidth;
  const height = video.videoHeight;
  if (!width || !height) return null;
  const canvas = full ? fullCanvas : aimCanvas;
  const scale = full ? 1 : Math.min(1, AIM_EDGE / Math.max(width, height));
  canvas.width = Math.round(width * scale);
  canvas.height = Math.round(height * scale);
  canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
  return new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', full ? FULL_QUALITY : AIM_QUALITY));
}

async function sendFrame(type, extra = {}) {
  const wanted = state;
  grabbing = true;
  try {
    const jpeg = await grab(type !== 'aim');
    if (jpeg && state === wanted && connected()) send({ type, seq: ++seq, ...extra }, jpeg);
    return !!jpeg && state === wanted;
  } finally {
    grabbing = false;
  }
}

// ---------- aiming ----------

function onAim({ page: found, same }) {
  page = found ? { corners: found.corners, confident: found.confident, same } : null;
  if (!found) {
    steadyAnchor = null;
    say(pages ? 'Point the camera at the next page' : 'Point the camera at a page');
  } else if (same) {
    steadyAnchor = null;
    say('Turn to the next page');
  } else if (!found.confident) {
    steadyAnchor = null;
    say('Fit the whole page in view');
  } else if (area(found.corners) < MIN_PAGE_AREA) {
    steadyAnchor = null;
    say('Move closer to the page');
  } else {
    if (!steadyAnchor || largestMove(steadyAnchor, found.corners) > STEADY_MOVE) {
      steadyAnchor = found.corners;
      steadySince = performance.now();
    }
    say('Hold steady…', 'ok');
  }
}

function updateSteady(now) {
  if (!steadyAnchor) { showSteady(0); return; }
  const progress = (now - steadySince) / STEADY_MS;
  showSteady(Math.min(1, progress));
  if (progress >= 1) takePage();
}

function showSteady(fraction) {
  steadyRing.style.strokeDasharray = `${(fraction * 100).toFixed(1)} 100`;
}

/** The page is in view and still (or the shutter was tapped): send the whole page at full resolution. */
async function takePage() {
  if (state !== 'aim' || !connected()) return;
  const corners = page?.corners;
  steadyAnchor = null;
  holdUntil = 0;
  setState('taking');
  if (!reducedMotion.matches) {
    flash.classList.remove('go');
    void flash.offsetWidth;     // restart the animation
    flash.classList.add('go');
  }
  say('Hold steady…', 'ok');
  const sent = await sendFrame('start', { filter: savedFilter(), corners });
  if (!sent && state === 'taking') setState('aim');
}

shutter.addEventListener('click', () => takePage());

// ---------- building the page ----------

function onStarted(message) {
  detail = { columns: message.columns, rows: message.rows, levels: message.levels, footprint: null, progress: message.progress };
  map.style.aspectRatio = String(message.aspect);
  mapImage.src = message.map;
  setState('detail');
  holdUntil = 0;
  say(detail.progress >= 0.97 ? 'This page is sharp already' : 'Move closer and sweep slowly over the page');
  renderDetail();
}

function onDetail(message) {
  if (message.levels) detail.levels = message.levels;
  if (message.map) mapImage.src = message.map;
  detail.footprint = message.footprint ?? null;
  detail.progress = message.progress;
  renderDetail();
  if (!message.accepted) {
    say(message.problem === 'blurry' ? 'Hold steadier' : 'Can’t place this view. Keep some print in it and move slower', 'warn', 1600);
  } else {
    const left = detail.levels.filter(level => level < 90).length;
    say(left > 2 ? 'Move closer over the amber areas' : 'Almost there');
  }
}

function renderDetail() {
  const percent = Math.round(Math.min(1, detail.progress) * 100);
  percentText.textContent = `${percent}%`;
  bar.style.width = `${percent}%`;
  bar.parentElement.setAttribute('aria-valuenow', String(percent));
  drawMap();
}

/** The little page map: amber where detail is still missing, a white box where the camera is looking now. */
function drawMap() {
  if (!detail) return;
  const ratio = devicePixelRatio || 1;
  const width = Math.round(map.clientWidth * ratio);
  const height = Math.round(map.clientHeight * ratio);
  if (!width || !height) return;
  if (mapCells.width !== width || mapCells.height !== height) { mapCells.width = width; mapCells.height = height; }
  const context = mapCells.getContext('2d');
  context.clearRect(0, 0, width, height);
  const { columns, rows, levels } = detail;
  for (let row = 0; row < rows; row++) {
    for (let column = 0; column < columns; column++) {
      const need = 1 - levels[row * columns + column] / 100;
      if (need < 0.05) continue;
      context.fillStyle = `rgba(255, 179, 64, ${(0.18 + 0.5 * need).toFixed(2)})`;
      const x0 = Math.round(column * width / columns);
      const y0 = Math.round(row * height / rows);
      context.fillRect(x0, y0, Math.round((column + 1) * width / columns) - x0, Math.round((row + 1) * height / rows) - y0);
    }
  }
  if (detail.footprint) {
    context.beginPath();
    detail.footprint.forEach(([x, y], index) => context[index ? 'lineTo' : 'moveTo'](x * width, y * height));
    context.closePath();
    context.lineJoin = 'round';
    context.strokeStyle = 'rgba(0, 0, 0, 0.55)';
    context.lineWidth = 4 * ratio;
    context.stroke();
    context.strokeStyle = '#fff';
    context.lineWidth = 2 * ratio;
    context.stroke();
  }
}

function endPage() {
  detail = null;
  map.classList.remove('busy');
  mapImage.removeAttribute('src');
}

finishButton.addEventListener('click', () => {
  if (state !== 'detail') return;
  finishButton.disabled = true;
  command('finish');
});

// ---------- the finished page ----------

function onDone(scan) {
  if (!scan) return;
  endPage();
  pages += 1;
  const job = addSavedScan(scan);
  job.pageNumber = pages;
  current = { job, scan, busy: false };
  stack.hidden = false;
  stack.firstElementChild.src = scan.previewUrl;
  updateCount();
  openReview();
}

async function openReview() {
  setState('review');
  reviewTitle.textContent = `Page ${current.job.pageNumber}`;
  markFilter(current.scan.filter);
  setReviewStatus('Saved to your PC', 'ok');
  review.hidden = false;
  reviewTitle.focus({ preventScroll: true });
  await setReviewImage(current.scan.previewUrl);
}

async function setReviewImage(url) {
  reviewBusy.hidden = false;
  reviewImage.src = url;
  try { await reviewImage.decode(); } catch { /* shown once loaded */ }
  reviewBusy.hidden = true;
}

function setReviewStatus(text, kind = '') {
  reviewStatus.textContent = text;
  reviewStatus.className = `status-line ${kind}`.trim();
}

function markFilter(filter) {
  for (const button of reviewFilters) button.setAttribute('aria-checked', String(button.dataset.liveFilter === filter));
}

function setReviewEnabled(enabled) {
  for (const control of [...reviewFilters, $('#liveRotate'), $('#liveRetake'), $('#liveApprove')]) control.disabled = !enabled;
}

/** Re-renders the page on the PC (look, rotation); the inbox file is replaced in place. */
async function change(update) {
  if (!current || current.busy) return false;
  current.busy = true;
  setReviewEnabled(false);
  reviewBusy.hidden = false;
  try {
    const { data } = await api('PUT', `/api/scan/${current.scan.id}`, update);
    current.scan = data;
    updateScan(current.job, data);
    stack.firstElementChild.src = data.previewUrl;
    markFilter(data.filter);
    setReviewStatus('Saved to your PC', 'ok');
    await setReviewImage(data.previewUrl);
    return true;
  } catch (error) {
    markFilter(current.scan.filter);
    setReviewStatus(error.friendly, 'error');
    reviewBusy.hidden = true;
    return false;
  } finally {
    if (current) current.busy = false;
    setReviewEnabled(true);
  }
}

for (const button of reviewFilters) {
  button.addEventListener('click', async () => {
    const filter = button.dataset.liveFilter;
    if (!current || filter === current.scan.filter) return;
    if (await change({ filter })) {
      try { localStorage.setItem(FILTER_KEY, filter); } catch { /* private mode */ }
    }
  });
}

$('#liveRotate').addEventListener('click', () => current && change({ rotate: (current.scan.rotate + 90) % 360 }));

function backToCamera(message) {
  review.hidden = true;
  current = null;
  holdUntil = 0;
  setState(greeted ? 'aim' : 'connecting');
  say(message, '', 1500);
  shutter.focus({ preventScroll: true });
}

$('#liveApprove').addEventListener('click', () => {
  if (current?.busy) return;
  backToCamera('Page saved. Turn to the next one');
});

$('#liveRetake').addEventListener('click', () => {
  if (!current || current.busy) return;
  const discarded = current;
  pages = Math.max(0, pages - 1);
  removeJob(discarded.job);
  api('DELETE', `/api/scan/${discarded.scan.id}`).catch(() => { /* already gone */ });
  command('forget');   // the same page may be scanned again
  stack.hidden = pages === 0;
  updateCount();
  backToCamera('Scan the page again');
});

// ---------- leaving ----------

closeButton.addEventListener('click', () => {
  if (state === 'taking' || state === 'detail' || state === 'finishing') {
    command('cancel');
    endPage();
    setState('aim');
    say('Page cancelled', '', 1500);
  } else {
    closeSheet();
  }
});

document.addEventListener('keydown', event => {
  if (event.key !== 'Escape' || state === 'off') return;
  if (state === 'review') $('#liveApprove').click();
  else closeButton.click();
});

// ---------- drawing the outline over the camera ----------

/** Normalized frame coordinates to screen pixels, for a video that fills the screen (object-fit: cover). */
function toScreen([x, y]) {
  const width = overlay.clientWidth;
  const height = overlay.clientHeight;
  const scale = Math.max(width / video.videoWidth, height / video.videoHeight);
  return [(width - video.videoWidth * scale) / 2 + x * video.videoWidth * scale, (height - video.videoHeight * scale) / 2 + y * video.videoHeight * scale];
}

function drawOutline() {
  const visible = (state === 'aim' || state === 'taking') && page && video.videoWidth;
  outlineShape.classList.toggle('shown', !!visible);
  if (!visible) { drawn = null; return; }
  const target = page.corners;
  if (!drawn || reducedMotion.matches) drawn = target.map(corner => [...corner]);
  else drawn.forEach((corner, i) => {
    corner[0] += (target[i][0] - corner[0]) * 0.35;
    corner[1] += (target[i][1] - corner[1]) * 0.35;
  });
  outlineShape.setAttribute('points', drawn.map(toScreen).map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' '));
  outlineShape.classList.toggle('unsure', !page.confident && !page.same);
  outlineShape.classList.toggle('same', !!page.same);
  outlineShape.classList.toggle('locked', state === 'taking');
}

addEventListener('resize', () => drawMap());

// ---------- geometry ----------

function area(corners) {
  let sum = 0;
  for (let i = 0; i < 4; i++) {
    const [x1, y1] = corners[i];
    const [x2, y2] = corners[(i + 1) % 4];
    sum += x1 * y2 - x2 * y1;
  }
  return Math.abs(sum) / 2;
}

function largestMove(a, b) {
  return Math.max(...a.map((corner, i) => Math.hypot(corner[0] - b[i][0], corner[1] - b[i][1])));
}
