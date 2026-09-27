# Verification evidence — SnagItOpen 0.1.0 preview

Recorded 2026-09-27 on the development machine: Windows NT 10.0.26200, x64, .NET SDK 10.0.401.

## Automated

| Check | Result |
|---|---|
| Release build of `SnagItOpen.slnx` | 0 errors, 0 warnings |
| `SnagItOpen.Core.Tests` | 103 passed, 0 failed |
| `SnagItOpen.Windows.Tests` | 102 passed, 0 failed |
| Self-contained publish + zip | `artifacts\SnagItOpen-0.1.0-win-x64.zip` |
| SHA-256 | `1f93461c00441b9a728947f13858c83f274fc998af04d967791e5f8de7374a87` |

The tests cover the spec oracles: layout (110×100, 200×60, 110×108), crop `(30,30,160,80)`, fit
`(-25,-15,210,70)`, seam 100×520, resize 200×100, snapping tolerance across zoom, undo/redo and
gesture history, document validation, EXIF orientations, alpha preservation, corrupt imports,
dispatcher fault isolation, render placement/crop/order/negative origins, PNG/JPEG export with atomic
replace, opaque redaction across formats, blur/pixelate region bounds, strip cut-out, capture masks,
project round-trip and hostile ZIPs (traversal, duplicates, future schema, unknown annotation type),
capture buffer conversion and monitor gaps, window filtering, clipboard retry, hotkey transactional
rebinding, single-instance message validation, selection state machine, scrolling/interval sessions
with fake clocks, settings/presets/history retention/recovery/asset retention.

Real-desktop check: 2×100 GDI captures with the process GDI object count stable between batches.

## Manual smoke runs

- Debug and published (self-contained) executables launched with an isolated `SNAGITOPEN_DATA`
  folder, showed the editor window ("Untitled - SnagItOpen"), created their data folders, and exited.

## Not yet verified (must be done by a person at the machine)

Everything in `docs/ACCEPTANCE.md` that needs eyes and hands is **untested**, including:

- Interactive editor workflows (import/reorder/crop/annotate/save/reopen) and keyboard-only use.
- Region/window/monitor capture accuracy, cursor placement, delayed menu capture.
- Mixed-DPI, negative-origin, and portrait multi-monitor setups (not available in this session).
- Clipboard paste from Snipping Tool and copy into Paint and other apps.
- Scrolling capture on real browsers and nested panes; interval capture over time.
- Narrator names, high contrast, 200% text scale.
- Crash-recovery drill, clean-machine/standard-user portable run, performance targets.

## Feasibility decisions

- **OCR (T45): deferred.** Not included. The Windows OCR API needs WinRT projection and has
  packaging considerations for an unpackaged WPF app that were not evaluated here; bundling an
  offline engine adds size and licensing review. The app works fully without it.
- **Modern capture backend (T46): deferred.** GDI BitBlt remains the only backend. Visible-window
  mode captures what is on screen, so overlapping windows appear in the result; the menu says so.
  Minimized, offscreen and DRM-protected content is not captured. Windows.Graphics.Capture was not
  prototyped.
