# SnagItOpen 0.1.0 (preview)

A local, offline Windows screenshot capture and image editor built around fast vertical/horizontal
combining and a free canvas. C# / .NET 10 / WPF. Not affiliated with TechSmith.

This is a **preview**: it builds, passes 205 automated tests and launches, but the manual acceptance
checklist has not been run yet. See [verification evidence](docs/evidence/verification.md).

## Run

- From the package: unzip `SnagItOpen-0.1.0-win-x64.zip` and run `SnagItOpen.exe`. No .NET install
  is needed. Delete the folder to uninstall; user data is in `%LOCALAPPDATA%\SnagItOpen`.
- From source: see [BUILD.md](docs/BUILD.md).

## What it does

- **Combine:** drop, import or paste images; Vertical / Horizontal / Free layout; gap, padding,
  alignment, match width/height; drag or Alt+Up/Down to reorder.
- **Edit:** move, resize, snap, crop, rotate/flip, z-order, duplicate, canvas fit/size, scale,
  undo/redo; arrows, lines, rectangles, ellipses, text, callouts, highlight, numbered steps, freehand,
  stamps, magnifier, opaque redaction, blur/pixelate, borders/shadows/rounded/torn edges, strip cut-out,
  manual seam join.
- **Capture:** region, window (visible area), monitor, all monitors, last region, ellipse/freehand,
  fixed size/aspect, multiple regions, delay, cursor, scrolling (guided/automatic), interval. Captures
  can start a new composition, append below/right, go on the free canvas, or copy only.
- **Output and files:** copy image, PNG/JPEG export, editable `.sio` projects, pin to screen, recent
  captures library, layout and capture presets, global hotkeys, tray icon, autosave recovery.

Default hotkeys: Ctrl+Shift+1 region, Ctrl+Shift+2 window, Ctrl+Shift+3 append region,
Ctrl+Shift+4 all monitors (Help → Keyboard shortcuts lists everything).

## Known limitations

- Capture uses GDI: window capture shows overlapping windows; minimized or protected content is not
  captured. Mixed-DPI setups are designed for but not yet tested.
- Scrolling capture is best effort and falls back to manual seam correction.
- Blur/pixelate are visual only. Use Redact for secure hiding. Saved projects keep the original pixels.
- No OCR. Clipboard transparency depends on the receiving app.

## Planning documents

[Research](docs/RESEARCH.md) · [Specification](docs/superpowers/specs/2026-09-27-snagitopen-design.md) ·
[Plan](docs/superpowers/plans/2026-09-27-snagitopen.md) · [Capabilities](docs/CAPABILITIES.md) ·
[Acceptance checklist](docs/ACCEPTANCE.md)
