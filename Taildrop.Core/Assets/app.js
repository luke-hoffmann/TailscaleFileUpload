// Home screen: the ways to send things to the PC. Everything else lives in transfers.js (queue, list,
// connection) and scan.js (the scanner).

import { connection, onConnectionChange, sendFiles } from './transfers.js';
import { scanDocument, scanFromLibrary, scanToPdf } from './scan.js';

const $ = selector => document.querySelector(selector);
const scan = $('#scan');
const scanHint = $('#scanHint');
const scanHintDefault = scanHint.textContent;
const scanPdf = $('#scanPdf');
const scanPdfHint = $('#scanPdfHint');
const scanPdfHintDefault = scanPdfHint.textContent;
const library = $('#library');
const files = $('#files');

$('#choose').addEventListener('click', () => files.click());
files.addEventListener('change', () => {
  const chosen = [...files.files];
  files.value = '';
  sendFiles(chosen);
});

scan.addEventListener('click', scanDocument);
scanPdf.addEventListener('click', scanToPdf);
library.addEventListener('click', scanFromLibrary);

// If the PC could not load its scanning engine, say so plainly and keep plain sending working.
onConnectionChange(() => {
  const unavailable = connection.online === true && connection.scan === false;
  scan.disabled = unavailable;
  scanPdf.disabled = unavailable;
  library.hidden = unavailable;
  scanHint.textContent = unavailable ? 'Not available on this computer. You can still send files.' : scanHintDefault;
  scanPdfHint.textContent = unavailable ? 'Not available on this computer' : scanPdfHintDefault;
});
