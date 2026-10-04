// Home screen: the two ways to send things to the PC. Everything else lives in transfers.js (queue, list,
// connection), scan.js (the photo scanner) and live.js (the live camera scanner).

import { connection, onConnectionChange, sendFiles } from './transfers.js';
import { takePhoto } from './scan.js';
import { liveSupported, openLive } from './live.js';

const $ = selector => document.querySelector(selector);
const scan = $('#scan');
const scanHint = $('#scanHint');
// On a secure (HTTPS) page the camera runs live in the page; over plain HTTP a photo is taken instead.
const live = liveSupported();
if (live) scanHint.textContent = 'Live camera: finds the page and sharpens it';
const scanHintDefault = scanHint.textContent;
const library = $('#library');
const files = $('#files');

$('#choose').addEventListener('click', () => files.click());
files.addEventListener('change', () => {
  const chosen = [...files.files];
  files.value = '';
  sendFiles(chosen);
});

scan.addEventListener('click', () => (live ? openLive() : takePhoto()));
library.addEventListener('click', () => $('#photos').click());

// If the PC could not load its scanning engine, say so plainly and keep plain sending working.
onConnectionChange(() => {
  const unavailable = connection.online === true && connection.scan === false;
  scan.disabled = unavailable;
  library.hidden = unavailable;
  scanHint.textContent = unavailable ? 'Not available on this computer. You can still send files.' : scanHintDefault;
});
