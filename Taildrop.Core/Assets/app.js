const chooseButton = document.querySelector('#choose');
const fileInput = document.querySelector('#files');
const transfers = document.querySelector('#transfers');
const queue = document.querySelector('#queue');
const summary = document.querySelector('#summary');
const connection = document.querySelector('#connection');
const connectionText = connection.querySelector('span');
let pending = 0;
let completed = 0;

chooseButton.addEventListener('click', () => fileInput.click());
fileInput.addEventListener('change', () => {
  const files = [...fileInput.files];
  fileInput.value = '';
  if (!files.length) return;
  transfers.hidden = false;
  pending += files.length;
  updateSummary();
  files.forEach(upload);
});

function upload(file) {
  const id = makeId();
  queue.insertAdjacentHTML('afterbegin', `<article class="transfer" id="${id}"><div class="name" title="${escapeHtml(file.name)}">${escapeHtml(file.name)}</div><div class="state">0% - 0 B / ${formatBytes(file.size)}</div><div class="track"><span></span></div></article>`);
  const row = document.getElementById(id);
  const state = row.querySelector('.state');
  const bar = row.querySelector('.track span');
  const started = performance.now();
  const xhr = new XMLHttpRequest();
  xhr.open('POST', '/api/upload');
  // Generous floor so small stuck transfers fail fast, without cutting off legitimate large/slow ones.
  xhr.timeout = Math.max(30000, (file.size / (256 * 1024)) * 1000 + 30000);
  xhr.setRequestHeader('X-File-Name', encodeURIComponent(file.name || `file-${Date.now()}`));
  xhr.setRequestHeader('Content-Type', file.type || 'application/octet-stream');
  xhr.upload.onprogress = event => {
    if (!event.lengthComputable) { state.textContent = `${formatBytes(event.loaded)} sent`; return; }
    const progress = Math.round(event.loaded / event.total * 100);
    bar.style.width = `${progress}%`;
    state.textContent = `${progress}% - ${formatBytes(event.loaded)} / ${formatBytes(event.total)}`;
  };
  xhr.onload = () => {
    if (xhr.status >= 200 && xhr.status < 300) {
      const seconds = Math.max((performance.now() - started) / 1000, .001);
      bar.style.width = '100%';
      state.textContent = `Sent - ${formatSpeed(file.size / seconds)}`;
      state.classList.add('sent');
      finish();
    } else {
      let message = 'Failed';
      try { message = JSON.parse(xhr.responseText).error || message; } catch {}
      fail(row, state, message);
    }
  };
  xhr.onerror = () => fail(row, state, 'Connection lost');
  xhr.onabort = () => fail(row, state, 'Cancelled');
  xhr.ontimeout = () => fail(row, state, 'Timed out');
  xhr.send(file);
}

function fail(row, state, message) {
  row.classList.add('failed');
  state.textContent = message;
  state.classList.add('failed');
  setConnection(false);
  finish();
}
function finish() { pending--; completed++; updateSummary(); }
function updateSummary() { summary.textContent = pending ? `${pending} sending` : `${completed} complete`; }
function formatSpeed(value) { return value >= 1024 ** 2 ? `${(value / 1024 ** 2).toFixed(1)} MB/s` : `${Math.round(value / 1024)} KB/s`; }
function formatBytes(value) {
  if (value >= 1024 ** 3) return `${(value / 1024 ** 3).toFixed(1)} GB`;
  if (value >= 1024 ** 2) return `${(value / 1024 ** 2).toFixed(1)} MB`;
  if (value >= 1024) return `${(value / 1024).toFixed(0)} KB`;
  return `${value} B`;
}
function setConnection(online) { connection.classList.toggle('offline', !online); connectionText.textContent = online ? 'Connected' : 'Disconnected'; }
function escapeHtml(value) { return String(value).replace(/[&<>"']/g, character => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' })[character]); }
let idCounter = 0;
function makeId() {
  idCounter += 1;
  return `upload-${Date.now().toString(36)}-${idCounter}-${Math.random().toString(36).slice(2, 8)}`;
}
fetch('/api/health', { cache:'no-store' }).then(response => setConnection(response.ok)).catch(() => setConnection(false));
