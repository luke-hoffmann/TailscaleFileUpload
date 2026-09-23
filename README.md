# Taildrop

A tiny Tailscale-only path from your phone's file picker to a temporary desktop inbox — packaged as a single portable `.exe`.

## Use

1. Copy `Taildrop.exe` to the computer (see [Get the exe](#get-the-exe)) and connect both the computer and phone to Tailscale.
2. Double-click `Taildrop.exe`.
3. Click **Show QR code** and scan it with the phone.
4. Tap **Select files** on the phone.
5. In the desktop inbox, select one or more files and drag them into any File Explorer folder. You can also use **Save selected** or **Save all**.

Files exist only in that Taildrop session until you drag or save copies somewhere permanent. Closing the desktop window stops the receiver and permanently deletes the temporary inbox.

## Get the exe

Taildrop.exe is a self-contained build artifact and isn't committed to this repo. Build it yourself:

```powershell
cd TaildropApp
dotnet publish -c Release -r win-x64
```

The finished file is at `TaildropApp\bin\Release\net8.0-windows\win-x64\publish\Taildrop.exe` (~75 MB). Copy just that one file anywhere — no Node.js, no PowerShell, no .NET install, no other files needed on the target machine.

## What it does

- Listens only on this computer's Tailscale IPv4 address, never on Wi-Fi or the public internet.
- Streams file bytes directly to disk without multipart encoding or buffering the full file in memory.
- Shows received files in a multi-select, Explorer-style desktop list.
- Supports dragging one or many files directly into File Explorer.
- Saves selected files or the complete inbox without overwriting same-named destination files.
- Preserves existing files by adding `(1)`, `(2)`, and so on to duplicate names.
- Uses temporary `.part` files so interrupted uploads never appear as completed files.
- Runs one desktop instance at a time (a second launch just shows "already open").
- Spawns a small watcher (the same exe, in a hidden mode) that deletes the temporary inbox if the app is closed normally, force-killed, or crashes. Since the HTTP server lives in the same process as the GUI, the OS releases port 8787 immediately either way.

## Requirements

- Windows 10 or 11, x64
- Tailscale connected on both devices

If Windows Firewall prompts for Taildrop, allow the Tailscale/private connection only. Tailscale Funnel is neither used nor needed.

## Project layout

`TaildropApp/` is a C#/.NET 8 WinForms app that hosts its own HTTP server (Kestrel) in-process — there's no external Node.js runtime or PowerShell script to ship. The phone-facing web page lives in `TaildropApp/Assets/` and is embedded directly into the exe.
