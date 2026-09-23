# Taildrop

A tiny Tailscale-only path from your phone's file picker to a temporary desktop inbox.

## Use

1. Connect the computer and phone to Tailscale.
2. Double-click `Taildrop.cmd`.
3. Click **Show QR code** and scan it with the phone.
4. Tap **Select files** on the phone.
5. In the desktop inbox, select one or more files and drag them into any File Explorer folder. You can also use **Save selected** or **Save all**.

Files exist only in that Taildrop session until you drag or save copies somewhere permanent. Closing the desktop window stops the receiver and permanently deletes the temporary inbox.

## What it does

- Listens only on this computer's Tailscale IPv4 address, never on Wi-Fi or the public internet.
- Streams file bytes directly to disk without multipart encoding or buffering the full file in memory.
- Shows received files in a multi-select, Explorer-style desktop list.
- Supports dragging one or many files directly into File Explorer.
- Saves selected files or the complete inbox without overwriting same-named destination files.
- Preserves existing files by adding `(1)`, `(2)`, and so on to duplicate names.
- Uses temporary `.part` files so interrupted uploads never appear as completed files.
- Runs one desktop instance at a time.
- Places the receiver in a Windows kill-on-close job. Normal close, forced close, and GUI crashes all release port `8787`.
- Uses a session cleanup watcher so forced closes and GUI crashes also delete the temporary files.
- Removes stale receiver processes and partial uploads on the next start.

## Requirements

- Windows 10 or 11
- Tailscale connected on both devices
- Node.js 18 or newer

If Windows Firewall prompts for Node.js, allow the Tailscale/private connection only. Tailscale Funnel is neither used nor needed.

## Verify

```powershell
npm test
```

The test suite starts an isolated receiver, checks the QR and minimal phone UI, streams a 10 MB file, verifies its bytes, checks collision/path safety, and confirms clean shutdown.
