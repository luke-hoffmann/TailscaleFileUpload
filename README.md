# Taildrop

A tiny Tailscale-only path from your phone to a temporary inbox on your PC, packaged as a single portable `.exe`.
Send photos and files, or **scan documents with the live camera**: point at a page and it is found, taken and
sharpened from close-ups, and arrives on the PC as a clean, high-resolution JPEG. Nothing is saved on the phone.

## Use

1. Copy `Taildrop.exe` to the computer (see [Get the exe](#get-the-exe)) and connect both the computer and phone to Tailscale.
   For the live camera, turn on **HTTPS Certificates** once for your tailnet (Tailscale admin console → DNS).
2. Double-click `Taildrop.exe`. The window shows a QR code; scan it with the phone (or **Copy link**).
3. On the phone, pick one of two actions:
   - **Scan Document**: point the camera at a page (see [Live scanning](#live-scanning)). Without HTTPS it takes a
     photo instead, which lands in the inbox about a second later.
   - **Send Photos or Files**: pick anything from the photo library or Files, exactly as before.
4. On the PC, select files in the inbox and drag them into any Explorer folder, or use **Save selected** / **Save all**.
   Double-click opens a file, **F2** renames it, **Delete** removes it.

Tip: in Safari use *Share → Add to Home Screen* so Taildrop is one tap away.

Everything lives in a temporary inbox until you drag or save copies somewhere permanent. Closing the desktop
window stops the receiver and permanently deletes the inbox.

## Live scanning

1. Point the phone at the page. A green outline follows the paper; hold still for a moment and the page is taken.
2. The bottom panel shows a small map of the page and a **Detail** meter. Move the phone **closer** and sweep slowly
   over the page: the amber areas on the map are where the print still needs more pixels, and they clear as you go.
3. When every part of the page with print on it is sharp (about 300 dpi), the page finishes by itself, is saved to
   the PC's inbox and shown. Tap **Finish** to stop early with what you have.
4. Change the look (**Auto**, **Gray**, **B&W**, **Original**) or **Rotate** if you like, then **Looks Good**. The
   camera never closed, so you're straight back to scanning: turn the page and it starts again. **Retake** removes
   the page from the PC and lets you scan it again.

How it works: the phone shows its own camera, so the picture never lags. Over one WebSocket the PC answers, a few
times a second, where the page is in a small frame. Once the page is taken, every close-up is placed on the page by
matching it against the first frame, then aligned to a fraction of a pixel. Wherever it saw the paper with more pixels
(and not blurred by shake), it replaces what was there, matched to the page's brightness and feathered in so no seams
show. A page already lying a few inches from the camera may be done straight away. The PC recognises a page it has
just scanned, so it waits for you to turn to the next one.

The live camera needs a secure (HTTPS) page, which is what Tailscale's certificates provide: Taildrop runs
`tailscale serve` while its window is open, so the phone opens `https://<your-computer>.ts.net`. The receiver itself
only listens on this computer, and the link reaches your own tailnet only (never the internet). Without HTTPS
certificates Taildrop works as before over plain HTTP and **Scan Document** takes a photo; the window says so under
the QR code. The first visit after starting can take up to half a minute while Tailscale fetches the certificate;
Taildrop asks for it as soon as it starts, so usually it's ready before you are.

## Photo scanning

- The PC (not the phone) does the work: the phone uploads the original full-resolution photo, the PC finds the
  paper, removes perspective, evens out the lighting and saves the finished page. That is why it is fast, sharp and
  needs nothing installed on the phone.
- It follows the **whole outline** of the paper, not just four corners, so curled receipts, folded sheets and
  dog-eared corners come out complete and rectangular. A folded-over corner is unfolded to where the corner would
  be, and anything inside the page that isn't paper (the table showing where a corner is folded away, a sliver
  of desk at an edge) is filled with the paper's own color. Page proportions (A4, Letter, receipts) are recovered
  from the perspective, and output is never upscaled.
- Only the one sheet is scanned: other sheets of a packet, a folded-back tab against a corner, or a magazine next
  to the page are left out.
- After a scan you can switch the look (**Auto**, **Gray**, **B&W**, **Original**), **Rotate**, **Retake**, or
  **Adjust** the edges: drag the corners, or pull an edge to follow a bend. The same inbox file is replaced in place.
  If the page can't be found with confidence (for example a sheet buried under pens and other papers), the edge
  editor opens by itself.
- **Scan Next Page** keeps going; each page is its own JPEG, named `Scan 2026-10-02 at 14.31.05.jpg`.
- Photos taken from the page's camera button are not added to your photo library, and live camera frames are never
  stored anywhere except the finished pages in the PC's inbox.

Tips for the best result: good light, the whole page in the frame, any contrasting surface behind it.

### How well it works

Measured with `tools/ScanBench` (see its README): 352 synthetic 12 MP photos of real document pages on 3D
paper with exact ground truth, 44 photos of a page with a packet's folded-back sheet against a corner, and
2,142 real phone video frames from the SmartDoc 2015 benchmark. IoU is the outline's overlap with the true page
(1.0 = perfect). "Missing" is paper left out; MS-SSIM compares the scan with the perfect flat page (higher is
better).

| | before | now |
|---|---|---|
| Synthetic: IoU / missing paper / MS-SSIM | 0.894 / 8.1% / 0.51 | **0.969 / 2.2% / 0.67** |
| ... folded corner (dog-ear) | 0.932 | **0.988** |
| ... half / tri-fold creases | 0.879 | **0.981** |
| ... another sheet touching a corner | 0.803 | **0.990** |
| ... crumpled | 0.971 | **0.990** |
| Folded-back tab against a corner (IoU / foreign area) | n/a | **0.985 / 0.4%** |
| SmartDoc real frames: IoU / frames within 0.9 IoU | 0.732 / 60% | **0.849 / 76%** |
| ... white paper on a white desk | 0.355 | **0.704** |

Still hard: a sheet on a cluttered desk under pens and cables with other papers beneath it (SmartDoc
background 5), where the edge editor opens instead.

## Get the exe

Taildrop.exe is a self-contained build artifact and isn't committed to this repo. Build it yourself on Windows:

```powershell
cd TaildropApp
dotnet publish -c Release -r win-x64
```

The finished file is at `TaildropApp\bin\Release\net8.0-windows\win-x64\publish\Taildrop.exe`. Copy just that one
file anywhere: no Node.js, no PowerShell, no .NET install. It is larger than a plain file receiver because it
includes OpenCV for scanning.

## Requirements

- Windows 10 or 11, x64
- Tailscale connected on both devices; for the live camera, HTTPS Certificates on for the tailnet (and Tailscale
  Serve allowed, which it is unless an admin has turned it off)
- The Microsoft Visual C++ Redistributable (x64) for scanning; most PCs already have it. If the phone shows
  *"Not available on this computer"* under Scan Document, installing it is the fix; sending files keeps working either way.

If Windows Firewall prompts for Taildrop, allow the Tailscale/private connection only. Tailscale Funnel (which would
make a page public) is neither used nor needed.

## What it does

- With HTTPS: listens only on 127.0.0.1, published to your tailnet by `tailscale serve` for as long as the window is
  open (the serve process is tied to Taildrop, so it ends even if Taildrop crashes). Without: listens only on this
  computer's Tailscale IPv4 address. Never on Wi-Fi or the public internet.
- Streams file bytes straight to disk without buffering whole files in memory; interrupted uploads never appear as
  finished files (temporary `.part` files).
- Never overwrites: same-named files get `(1)`, `(2)`, and so on.
- Runs one desktop instance at a time.
- Spawns a small watcher (the same exe in a hidden mode) that deletes the temporary inbox if the app is closed,
  force-killed or crashes.

## Project layout

| Folder | What it is |
|---|---|
| `Taildrop.Core/` | The receiver (Kestrel HTTP server, the live scanner's WebSocket in `TaildropServer.Live.cs`, `tailscale serve` in `Tailscale.cs`), the phone page (`Assets/`, embedded into the exe) and the scanning engine (`Scanning/`: page detection with a trained page ranker, edge tracing, flattening, clean-up, enhancement, and `DetailCanvas` for building a page from close-ups). Plain .NET 8, builds and tests on any OS. |
| `TaildropApp/` | The Windows desktop window (WinForms) that hosts the receiver. |
| `Taildrop.Core.Tests/` | Unit and integration tests, including synthetic photos of tilted, bent and curled pages. |
| `tools/DevHost/` | Runs the real receiver and phone page on a laptop of any OS, for development. |
| `tools/e2e/` | Browser tests of the phone page in an iPhone-sized Chromium (Playwright). |
| `tools/ScanBench/` | Scanner benchmark (synthetic 3D-paper photos, SmartDoc 2015) and the page-ranker training script. |

### Developing

```bash
dotnet test Taildrop.Core.Tests                                      # engine + server tests (Linux/macOS/Windows)
dotnet run --project tools/DevHost                                     # phone page at http://127.0.0.1:8787
dotnet run --project tools/DevHost -- --serve                          # ...and at https://<this computer>.ts.net for a phone

# browser tests: needs Chromium (set PLAYWRIGHT_BROWSERS_PATH or let Playwright download one)
# SamplePhotos also writes live-camera.mjpeg, the fake camera the live scanner tests film
TAILDROP_SAMPLES_DIR=/tmp/samples dotnet test Taildrop.Core.Tests --filter SamplePhotos
dotnet build tools/DevHost && (cd tools/e2e && npm install) && SAMPLES=/tmp/samples node tools/e2e/phone.e2e.mjs
```

Building the WinForms project itself needs Windows (or a Microsoft .NET SDK with the Windows Desktop workload).
In a desktop browser, `http://127.0.0.1` counts as a secure page, so the live camera works with a webcam against
DevHost without any HTTPS setup.

## Third-party software

OpenCV and OpenCvSharp (Apache-2.0) for scanning, QRCoder (MIT) for the QR code. See `THIRD_PARTY_NOTICES.md`.
