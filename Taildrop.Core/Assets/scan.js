// Scan Document: take a photo with the camera, let the PC find and flatten the page, review it, optionally
// adjust the edges / look / rotation, then scan the next page or finish. Every page is already in the PC's
// inbox by the time it is shown here; nothing is kept on the phone.

import { api, connection, removeJob, sendFiles, setScanOpener, startScan, updateScan } from './transfers.js';
import { OutlineEditor } from './editor.js';

const $ = selector => document.querySelector(selector);
const app = $('#app');
const sheet = $('#sheet');
const camera = $('#camera');
const photos = $('#photos');
const views = { wait: $('#view-wait'), busy: $('#view-busy'), result: $('#view-result'), adjust: $('#view-adjust'), live: $('#view-live') };

const resultImage = $('#resultImage');
const resultBusy = $('#resultBusy');
const resultStatus = $('#resultStatus');
const filterButtons = [...document.querySelectorAll('.segmented [data-filter]')];

const FILTER_KEY = 'taildrop.filter';
const DEFAULT_HINT = 'Drag the round handles so the outline follows the paper’s edges, including any curls.';

let pages = 0;           // pages scanned in this visit (for the "Scan 2" title)
let current = null;      // { job, scan, busy } — the page on screen
let lastFile = null;     // the photo being scanned, kept for "Try Again" / "Send As-Is"
let pending = null;      // the in-flight scan request
const closeListeners = new Set();

const editor = new OutlineEditor({
  wrap: $('#photoWrap'),
  image: $('#adjustImage'),
  svg: $('#overlay'),
  loupe: $('#loupe')
});

// ---------- sheet and views ----------

function openSheet() {
  sheet.hidden = false;
  app.setAttribute('inert', '');
  document.body.style.overflow = 'hidden';
}

function closeSheet() {
  pending?.promise.abort();
  pending = null;
  current = null;
  sheet.hidden = true;
  app.removeAttribute('inert');
  document.body.style.overflow = '';
  closeListeners.forEach(listener => listener());
}

/** The live camera (live.js) shares this sheet: it opens its own view and hears when the sheet closes. */
export function openSheetView(name) {
  openSheet();
  show(name);
}
export function onSheetClose(listener) { closeListeners.add(listener); }
export { closeSheet };

function show(name) {
  for (const [key, view] of Object.entries(views)) view.hidden = key !== name;
  const title = views[name].querySelector('.nav-title');
  if (title) {
    title.tabIndex = -1;
    title.focus({ preventScroll: true });
  }
}

function savedFilter() {
  try { return localStorage.getItem(FILTER_KEY) || 'auto'; } catch { return 'auto'; }
}

function rememberFilter(filter) {
  try { localStorage.setItem(FILTER_KEY, filter); } catch { /* private mode etc. */ }
}

// ---------- taking and sending a photo ----------

function takePhoto() { camera.click(); }   // must run directly inside the tap, or iOS will not open the camera

function setBusy({ title, text = '', progress = null, spinner = true, error = false }) {
  $('#busyTitle').textContent = title;
  $('#busyText').textContent = text;
  $('#busySpinner').hidden = !spinner;
  const bar = $('#busyProgress');
  bar.hidden = progress === null;
  if (progress !== null) {
    bar.firstElementChild.style.width = `${progress}%`;
    bar.setAttribute('aria-valuenow', String(progress));
  }
  $('#busyActions').hidden = !error;
}

async function scanPhoto(file) {
  lastFile = file;
  show('busy');
  setBusy({ title: 'Sending photo…', progress: 0 });

  const request = startScan(file, savedFilter(), {
    onProgress: percent => setBusy({ title: 'Sending photo…', progress: percent }),
    onPhase: () => setBusy({ title: 'Finding the page edges…', text: 'The computer is straightening the page.', progress: null })
  });
  pending = request;

  try {
    const scan = await request.promise;
    if (pending !== request) return;      // sheet was closed meanwhile
    pending = null;
    pages += 1;
    request.job.pageNumber = pages;
    current = { job: request.job, scan, busy: false };
    showResult();
    if (!scan.confident) openAdjust({ automatic: true });
  } catch (error) {
    if (pending !== request) return;
    pending = null;
    if (error.kind === 'abort') return;
    setBusy({
      title: 'Couldn’t scan that photo',
      text: error.friendly,
      spinner: false,
      error: true
    });
    // Offering the plain photo is pointless if the PC can't be reached at all.
    $('#busyAsIs').hidden = error.kind === 'network' || error.kind === 'timeout';
  }
}

async function handlePhoto(file) {
  if (!file) return;
  openSheet();
  await scanPhoto(file);
}

for (const input of [camera, photos]) {
  input.addEventListener('change', () => {
    const file = input.files?.[0];
    input.value = '';
    handlePhoto(file);
  });
}

// ---------- result ----------

function showResult() {
  const { scan, job } = current;
  $('#resultTitle').textContent = job.pageNumber ? `Scan ${job.pageNumber}` : 'Scan';
  // A live scan was built from many frames: there is no single photo whose edges could be adjusted.
  $('#adjust').hidden = scan.adjustable === false;
  markFilter(scan.filter);
  setStatus('Saved to your PC', 'ok');
  show('result');
  setPreview(scan.previewUrl);
}

async function setPreview(url) {
  resultBusy.hidden = false;
  resultImage.src = url;
  try { await resultImage.decode(); } catch { /* shown when loaded */ }
  resultBusy.hidden = true;
}

function setStatus(text, kind = '') {
  resultStatus.textContent = text;
  resultStatus.className = `status-line ${kind}`.trim();
}

function markFilter(filter) {
  for (const button of filterButtons) button.setAttribute('aria-checked', String(button.dataset.filter === filter));
}

function setControlsEnabled(enabled) {
  for (const control of [...filterButtons, $('#adjust'), $('#rotate'), $('#nextPage'), $('#retake'), $('#done')]) control.disabled = !enabled;
}

/** Asks the PC to re-render the scan with a change, and refreshes the preview. Returns true on success. */
async function applyChange(update) {
  if (!current || current.busy) return false;
  current.busy = true;
  setControlsEnabled(false);
  resultBusy.hidden = false;
  try {
    const { data } = await api('PUT', `/api/scan/${current.scan.id}`, update);
    current.scan = data;
    updateScan(current.job, data);
    markFilter(data.filter);
    setStatus('Saved to your PC', 'ok');
    await setPreview(data.previewUrl);
    return true;
  } catch (error) {
    markFilter(current.scan.filter);
    setStatus(error.friendly, 'error');
    resultBusy.hidden = true;
    return false;
  } finally {
    if (current) current.busy = false;
    setControlsEnabled(true);
  }
}

for (const button of filterButtons) {
  button.addEventListener('click', async () => {
    const filter = button.dataset.filter;
    if (!current || filter === current.scan.filter) return;
    if (await applyChange({ filter })) rememberFilter(filter);
  });
}

$('#rotate').addEventListener('click', () => applyChange({ rotate: (current.scan.rotate + 90) % 360 }));
$('#adjust').addEventListener('click', () => openAdjust());
$('#nextPage').addEventListener('click', () => { takePhoto(); });
$('#done').addEventListener('click', closeSheet);

$('#retake').addEventListener('click', () => {
  takePhoto();                       // first, while we still have the tap
  const discarded = current;
  current = null;
  show('wait');
  if (discarded) {
    pages = Math.max(0, pages - 1);
    removeJob(discarded.job);
    api('DELETE', `/api/scan/${discarded.scan.id}`).catch(() => { /* already gone */ });
  }
});

// ---------- waiting / busy actions ----------

$('#takePhoto').addEventListener('click', takePhoto);
document.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', closeSheet));
$('#busyCancel').addEventListener('click', closeSheet);
$('#busyRetry').addEventListener('click', () => lastFile && scanPhoto(lastFile));
$('#busyAsIs').addEventListener('click', () => {
  if (lastFile) sendFiles([lastFile]);
  closeSheet();
});

// ---------- edge editor ----------

async function openAdjust({ automatic = false } = {}) {
  if (!current) return;
  $('#adjustHint').textContent = automatic
    ? 'We couldn’t find the page edges for sure. Drag the handles onto the paper’s edges, then tap Done.'
    : DEFAULT_HINT;
  $('#adjustDone').disabled = true;
  show('adjust');
  await editor.load(current.scan.sourceUrl, current.scan.outline);
  $('#adjustDone').disabled = false;
}

$('#resetEdges').addEventListener('click', () => editor.reset());
$('#straightenEdges').addEventListener('click', () => editor.straighten());
$('#adjustCancel').addEventListener('click', () => { show('result'); });
$('#adjustDone').addEventListener('click', async () => {
  const outline = editor.current;
  const changed = editor.changed;
  show('result');
  if (changed) await applyChange({ outline });
});

// ---------- reopening a finished scan, keyboard ----------

setScanOpener(job => {
  if (!job.scan) return;
  openSheet();
  current = { job, scan: job.scan, busy: false };
  showResult();
});

document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && !sheet.hidden && views.busy.hidden && views.live.hidden) {
    if (!views.adjust.hidden) show('result'); else closeSheet();
  }
});

/** Whether scanning can be offered at all (the PC reports if its image engine failed to load). */
export function scanAvailable() { return connection.scan; }
export { takePhoto };
