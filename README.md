# Taildrop

A tiny Tailscale-only path from your phone to a temporary inbox on your PC, packaged as a single portable `.exe`.
Send photos and files, or **scan documents straight from the phone camera**: the page is found, flattened and
arrives on the PC as a clean, full-resolution JPEG. Nothing is saved on the phone.

## Use

1. Copy `Taildrop.exe` to the computer (see [Get the exe](#get-the-exe)) and connect both the computer and phone to Tailscale.
2. Double-click `Taildrop.exe`. The window shows a QR code; scan it with the phone (or **Copy link**).
3. On the phone, pick one of two actions:
   - **Scan Document**: take a photo of a page or receipt. It lands in the inbox about a second later.
   - **Send Photos or Files**: pick anything from the photo library or Files, exactly as before.
4. On the PC, select files in the inbox and drag them into any Explorer folder, or use **Save selected** / **Save all**.
   Double-click opens a file, **F2** renames it, **Delete** removes it.

Tip: in Safari use *Share → Add to Home Screen* so Taildrop is one tap away.

Everything lives in a temporary inbox until you drag or save copies somewhere permanent. Closing the desktop
window stops the receiver and permanently deletes the inbox.

## Scanning

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
- Photos taken from the page's camera button are not added to your photo library.

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
- Tailscale connected on both devices
- The Microsoft Visual C++ Redistributable (x64) for scanning; most PCs already have it. If the phone shows
  *"Not available on this computer"* under Scan Document, installing it is the fix; sending files keeps working either way.

If Windows Firewall prompts for Taildrop, allow the Tailscale/private connection only. Tailscale Funnel is neither
used nor needed.

## What it does

- Listens only on this computer's Tailscale IPv4 address, never on Wi-Fi or the public internet.
- Streams file bytes straight to disk without buffering whole files in memory; interrupted uploads never appear as
  finished files (temporary `.part` files).
- Never overwrites: same-named files get `(1)`, `(2)`, and so on.
- Runs one desktop instance at a time.
- Spawns a small watcher (the same exe in a hidden mode) that deletes the temporary inbox if the app is closed,
  force-killed or crashes.

## Project layout

| Folder | What it is |
|---|---|
| `Taildrop.Core/` | The receiver (Kestrel HTTP server), the phone page (`Assets/`, embedded into the exe) and the scanning engine (`Scanning/`: page detection with a trained page ranker, edge tracing, flattening, clean-up, enhancement). Plain .NET 8, builds and tests on any OS. |
| `TaildropApp/` | The Windows desktop window (WinForms) that hosts the receiver. |
| `Taildrop.Core.Tests/` | Unit and integration tests, including synthetic photos of tilted, bent and curled pages. |
| `tools/DevHost/` | Runs the real receiver and phone page on a laptop of any OS, for development. |
| `tools/e2e/` | Browser tests of the phone page in an iPhone-sized Chromium (Playwright). |
| `tools/ScanBench/` | Scanner benchmark (synthetic 3D-paper photos, SmartDoc 2015) and the page-ranker training script. |

### Developing

```bash
dotnet test Taildrop.Core.Tests                                      # engine + server tests (Linux/macOS/Windows)
dotnet run --project tools/DevHost                                     # phone page at http://127.0.0.1:8787

# browser tests: needs Chromium (set PLAYWRIGHT_BROWSERS_PATH or let Playwright download one)
TAILDROP_SAMPLES_DIR=/tmp/samples dotnet test Taildrop.Core.Tests --filter SamplePhotos
dotnet build tools/DevHost && (cd tools/e2e && npm install) && SAMPLES=/tmp/samples node tools/e2e/phone.e2e.mjs
```

Building the WinForms project itself needs Windows (or a Microsoft .NET SDK with the Windows Desktop workload).

## Third-party software

OpenCV and OpenCvSharp (Apache-2.0) for scanning, QRCoder (MIT) for the QR code. See `THIRD_PARTY_NOTICES.md`.
