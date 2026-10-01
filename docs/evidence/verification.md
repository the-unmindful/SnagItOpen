# Verification evidence — SnagItOpen 0.2.0 preview

Recorded 2026-10-01 on Windows 11 x64, .NET SDK 10.0.401, local branch `ui-upgrade`.

## Final release gate

| Check | Result |
|---|---|
| Release build, `SnagItOpen.slnx` | 0 warnings, 0 errors |
| Core tests | 211 passed, 0 failed, 0 skipped |
| Windows tests | 226 passed, 0 failed, 0 skipped |
| Self-contained win-x64 publish | Passed |
| ZIP | `artifacts/SnagItOpen-0.2.0-win-x64.zip` (70,616,317 bytes) |
| SHA-256 | `49653f16ab19c0d8f93f3f05e13015994bdc8a65fff1b016dbc5457cf0d31c08` |
| Published isolated startup smoke | Running; first-run UI state saved; 0 unhandled log entries |

Run from the repository:

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
.\scripts\publish.ps1
```

The script builds serially (`/m:1 /nr:false`), runs both Release suites, publishes with `--self-contained true -r win-x64`, and generates the ZIP/checksum. See [Release log](upgrade-release.log) and [startup smoke log](upgrade-smoke.log). Debug gates passed during integration; final polish was verified by the complete Release gate.

The startup smoke used unique `SNAGITOPEN_DATA` under `E:\Misc\test\opencode-trial\temp\opencode`, with empty hotkeys, tray disabled and no recovery candidates. It inspected the package's own saved startup state and logs, then stopped only its recorded PID after checking the executable path. It did not automate or inspect native UI controls, change the installed copy or write real user data.

## Scope verified

Baseline rendering, layout, export, opaque redaction, magnifier privacy, project schema/ZIP validation, source-asset preservation, undo/redo, capture masks, monitor geometry, clipboard/hotkey services, scrolling/interval, recovery and retention regressions remain passing.

New tests cover settings v3/UI state; layered canvas invalidation/cursors/selection; spacing guides; shared controls/typed dialogs; staged settings/preset validation and rollback; annotation/effect galleries and one-step undo; library search; capture Adjust/notice lifecycle and physical sampling; command inventory/gestures/availability/bindings; toast lifetime; native nonactivation/capture affinity; pin recovery/zoom. No runtime dependency was added, and editable `.sio` schema remains 2.

Application-level renders cover Light, Dark and HighContrast at 1280/1000/800/640 DIP and 100/150/200% render DPI. See [UI/accessibility evidence](ui-accessibility.md) for representative images and precise limits.

## Acceptance still open

Live computer-use access was not approved for SnagItOpen. Hardware and external-app acceptance, Narrator, Windows 200% text size, arbitrary system High contrast palettes, mixed-DPI displays, actual drag/clipboard destinations, real scrolling capture and performance measurements remain unchecked in `docs/ACCEPTANCE.md`. Render DPI tests do not substitute for those checks.

Optional U39 (Mica/system accent), OCR and Windows.Graphics.Capture were excluded. GDI visible-window capture retains its documented occlusion/protected-content limitations. No push, merge or installation was performed.
