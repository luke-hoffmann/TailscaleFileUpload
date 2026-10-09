// Scan Document: take a photo with the camera, let the PC find and flatten the page, review it, optionally
// adjust the edges / look / rotation, then scan the next page or finish. Every page is already in the PC's
// inbox by the time it is shown here; nothing is kept on the phone.
// Scan to PDF works the same way, except that the pages go into one PDF (rewritten on the PC after every
// change) instead of becoming a JPEG each.

import { api, connection, removeJob, render, sendFiles, setScanOpener, startScan, updateScan } from './transfers.js';
import { OutlineEditor } from './editor.js';

const $ = selector => document.querySelector(selector);
const app = $('#app');
const sheet = $('#sheet');
const camera = $('#camera');
const photos = $('#photos');
const views = { wait: $('#view-wait'), busy: $('#view-busy'), result: $('#view-result'), adjust: $('#view-adjust') };

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
let pdf = null;          // the PDF being made: { id, job, pages: [scan, ...], insertAt }; null for one-JPEG-per-page scans
                         // (insertAt: where the next photo goes, so a retaken page keeps its place; null = at the end)

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
  pdf = null;
  sheet.hidden = true;
  app.removeAttribute('inert');
  document.body.style.overflow = '';
}

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

/** Home screen: one JPEG per page. */
export function scanDocument() {
  pdf = null;
  takePhoto();
}

/** Home screen: all pages into one PDF. */
export function scanToPdf() {
  pdf = { id: null, job: null, pages: [], insertAt: null };
  takePhoto();
}

export function scanFromLibrary() {
  pdf = null;
  photos.click();
}

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

  const doc = pdf;
  const request = startScan(file, savedFilter(), {
    pdf: doc && { id: doc.id, index: doc.insertAt, job: doc.job },
    onProgress: percent => setBusy({ title: 'Sending photo…', progress: percent }),
    onPhase: () => setBusy({ title: 'Finding the page edges…', text: 'The computer is straightening the page.', progress: null })
  });
  pending = request;

  try {
    const scan = await request.promise;
    if (pending !== request) return;      // sheet was closed meanwhile
    pending = null;
    if (doc) {
      doc.id = scan.pdf.id;
      doc.job = request.job;
      doc.insertAt = null;
      doc.pages.splice(scan.pdf.page - 1, 0, scan);
      request.job.pdf = doc;
      updatePdfRow(doc);
    } else {
      pages += 1;
      request.job.pageNumber = pages;
    }
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
    // Offering the plain photo is pointless if the PC can't be reached at all, and a loose photo is no page of a PDF.
    $('#busyAsIs').hidden = error.kind === 'network' || error.kind === 'timeout' || !!doc;
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
  $('#resultTitle').textContent = pdf
    ? `Page ${pageIndex() + 1} of ${pdf.pages.length}`
    : job.pageNumber ? `Scan ${job.pageNumber}` : 'Scan';
  updatePager();
  markFilter(scan.filter);
  setStatus(savedText(), 'ok');
  show('result');
  setPreview(scan.previewUrl);
}

function savedText() {
  return pdf ? `Saved to your PC · ${pdf.pages.length}-page PDF` : 'Saved to your PC';
}

// ---------- PDF pages ----------

function pageIndex() {
  return pdf && current ? pdf.pages.findIndex(page => page.id === current.scan.id) : -1;
}

/** Previous / Next buttons, shown once a PDF has more than one page. */
function updatePager() {
  const count = pdf ? pdf.pages.length : 0;
  $('#pager').hidden = count < 2;
  if (count < 2) return;
  const index = pageIndex();
  const prev = $('#pagePrev');
  const next = $('#pageNext');
  prev.disabled = index <= 0;
  next.disabled = index >= count - 1;
  prev.querySelector('span').textContent = index > 0 ? `Page ${index}` : 'Previous';
  next.querySelector('span').textContent = index < count - 1 ? `Page ${index + 2}` : 'Next';
  prev.setAttribute('aria-label', index > 0 ? `Go to page ${index}` : 'Previous page');
  next.setAttribute('aria-label', index < count - 1 ? `Go to page ${index + 2}` : 'Next page');
}

function goToPage(offset) {
  if (!pdf || !current || current.busy) return;
  const target = pdf.pages[pageIndex() + offset];
  if (!target) return;
  current = { job: current.job, scan: target, busy: false };
  showResult();
}

/** Keeps the PDF's row in the "Sent to PC" list in step: page count, and the latest page as its thumbnail. */
function updatePdfRow(doc) {
  if (!doc.job) return;
  if (!doc.pages.length) {
    // The PC removed the PDF along with its last page; a new photo brings it (and its row) back.
    removeJob(doc.job);
    doc.job = null;
    return;
  }
  doc.job.pages = doc.pages.length;
  if (!doc.pages.some(page => page.id === doc.job.scan?.id)) doc.job.scan = doc.pages[doc.pages.length - 1];
  render(doc.job);
}

$('#pagePrev').addEventListener('click', () => goToPage(-1));
$('#pageNext').addEventListener('click', () => goToPage(1));

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
  for (const control of [...filterButtons, $('#adjust'), $('#rotate'), $('#nextPage'), $('#retake'), $('#done'), $('#pagePrev'), $('#pageNext')]) control.disabled = !enabled;
  if (enabled && current) updatePager();
}

/** Asks the PC to re-render the scan with a change, and refreshes the preview. Returns true on success. */
async function applyChange(update) {
  if (!current || current.busy) return false;
  current.busy = true;
  setControlsEnabled(false);
  resultBusy.hidden = false;
  try {
    const { data } = await api('PUT', `/api/scan/${current.scan.id}`, update);
    if (pdf) {
      const index = pdf.pages.findIndex(page => page.id === data.id);
      if (index >= 0) pdf.pages[index] = data;
    }
    current.scan = data;
    updateScan(current.job, data);
    markFilter(data.filter);
    setStatus(savedText(), 'ok');
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
$('#nextPage').addEventListener('click', () => {
  if (pdf) pdf.insertAt = null;     // "next" page: at the end of the PDF
  takePhoto();
});
$('#done').addEventListener('click', closeSheet);

$('#retake').addEventListener('click', () => {
  takePhoto();                       // first, while we still have the tap
  const discarded = current;
  current = null;
  $('#waitTitle').textContent = pdf ? 'Ready for the new page' : 'Ready for the next photo';
  show('wait');
  if (discarded && pdf) {
    // The page leaves the PDF; the new photo goes into its place.
    const index = pdf.pages.findIndex(page => page.id === discarded.scan.id);
    if (index >= 0) pdf.pages.splice(index, 1);
    pdf.insertAt = index >= 0 ? index : null;
    updatePdfRow(pdf);
    api('DELETE', `/api/scan/${discarded.scan.id}`).catch(() => { /* already gone */ });
  } else if (discarded) {
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
  pdf = job.pdf ?? null;
  current = { job, scan: job.scan, busy: false };
  showResult();
});

document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && !sheet.hidden && views.busy.hidden) {
    if (!views.adjust.hidden) show('result'); else closeSheet();
  }
});

/** Whether scanning can be offered at all (the PC reports if its image engine failed to load). */
export function scanAvailable() { return connection.scan; }
