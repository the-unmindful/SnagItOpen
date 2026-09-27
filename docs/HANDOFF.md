# SnagItOpen hand-off

Read this first in any new session. Last updated 2026-09-27, at commit `add8f6e` (branch `master`, local only, nothing pushed).

## 1. What this is

A local, offline Windows screenshot capture and image editor, similar to Snagit. It is built with C# / .NET 10 / WPF, with no third-party runtime packages. The repository root is this folder (`workspace\Softwares\SnagItOpen`), which is its own git repo.

## 2. How to work here (do this before changing anything)

- **Shell is Windows PowerShell 5.1.** `&&` does not work; use `;` or `if ($?) { }`. `rg` is not installed; use the Grep tool.
- **SDK:** .NET 10.0.401, installed per user. If `dotnet --list-sdks` shows no 10.x, run
  `$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"`.
- **Verification gate (run after every change):**
  ```powershell
  dotnet build .\SnagItOpen.slnx -c Debug
  dotnet test .\tests\SnagItOpen.Core.Tests\SnagItOpen.Core.Tests.csproj -c Debug
  dotnet test .\tests\SnagItOpen.Windows.Tests\SnagItOpen.Windows.Tests.csproj -c Debug
  .\scripts\publish.ps1          # Release build + both suites + package in artifacts\
  ```
  Expected at hand-off: **0 warnings, 0 errors, 162 Core + 128 Windows = 290 tests passing.**
- **Before building, stop the running app.** The installed copy locks nothing in the repo, but a copy started from `bin\` does:
  `Get-Process SnagItOpen -ErrorAction SilentlyContinue | Stop-Process -Force`.
- **Smoke runs must use a temp data folder:** set `$env:SNAGITOPEN_DATA` to a folder under `E:\Misc\test\opencode-trial\temp\opencode`, so real user data is never touched.
- **Don't run a build/test in the same parallel batch as the file edit it should check.** It can run before the edit lands and report a stale pass. This happened several times in the first session.
- **Read a file before editing it**, and after a batch of edits check `git diff --stat` to confirm each edit actually landed.
- **Commits:** small, one per feature step, message style `Area: what changed`. Commit locally. Do not push unless the user asks. There is no remote configured.
- **Reinstall after changes the user wants to try:** `.\scripts\install.ps1` (options: `-StartWithWindows`, `-DesktopShortcut`, `-NoLaunch`). It installs to `%LOCALAPPDATA%\Programs\SnagItOpen` and needs no admin rights. Quit the running tray copy first.
- `LF will be replaced by CRLF` warnings from git are harmless.

## 3. Solution layout

| Project | Role |
|---|---|
| `src/SnagItOpen.Core` | Pure logic, no WPF: document model, annotations, geometry, layout, editing ops, history, validation, capture state machines, stitching. |
| `src/SnagItOpen.Storage` | Asset store, `.sio` projects, settings, presets, styles, capture library, autosave/recovery. |
| `src/SnagItOpen.Imaging` | STA imaging dispatcher, importer, renderer (shared by preview and export), effects, export. |
| `src/SnagItOpen.Windows` | Win32: monitors, GDI capture, window catalog, clipboard, hotkeys, tray, single instance, scroll input. |
| `src/SnagItOpen.App` | WPF app: main window, canvas, panels, capture overlay, dialogs. |
| `tests/SnagItOpen.Core.Tests` | xUnit, pure logic. |
| `tests/SnagItOpen.Windows.Tests` | xUnit, rendering/storage/Win32 (needs an interactive desktop; one real GDI leak test). |

Key App files:
- `Shell/MainWindow.xaml(.cs)`: menus, toolbars, shortcuts, tray, hotkeys, canvas bar, text editor, drag in/out.
- `Editor/CanvasView.cs`: rendering adorners and every pointer gesture.
- `Editor/EditorViewModel.cs`: document/history state and commands. Every edit goes through `Commit` (one undo step).
- `Editor/AnnotationPropertiesPanel.cs`, `Editor/ImageEdgePanel.cs`, `Editor/ObjectsList.cs`: right-hand and left-hand panels.
- `Infrastructure/ColorPicker.cs`, `Infrastructure/HotkeyBox.cs`: shared controls.
- Core: `Documents/Annotations/Annotations.cs` (model), `AnnotationGeometry.cs` (handles, hit-testing, drags), `AnnotationStyle.cs`, `Editing/DocumentOps.cs`.

## 4. Decisions already made with the user (don't reverse without asking)

- **Annotations are canvas objects, not parts of images.** They are in document pixels, always drawn above all images, never clipped, and unaffected by image crop/move/delete. Old image-linked annotations (`ImageLayerId`) are converted once on load (`AnnotationCanvas.Normalize`).
- **Stacking reorders annotations only among themselves;** they always stay above images.
- **Redactions never rotate, are always fully opaque, and always render**, even when "hidden", so export never exposes covered pixels. Blur/pixelate are image effects and are labelled as not secure.
- **Unfilled rectangles/ellipses are see-through for clicks** (only the border is hit). Alt+click cycles down a stack.
- **Text is edited in place on the canvas** (Ctrl+Enter or click away commits, Esc cancels).
- **Locked canvas:** anything outside it is not exported, copied or pinned. The editor shows outside content Dimmed by default (Show/Hide available, saved as a preference).
- **Project schema is version 2**; v1 opens and is upgraded. Newer files fail with a clear message.
- **Settings version 2:** close-to-tray is on by default; region capture defaults to PrintScreen, falling back to Ctrl+PrintScreen when Windows reserves PrintScreen.
- **Gestures:** Shift/Ctrl+click adds or removes from the selection; Ctrl+drag duplicates; Ctrl+Alt+C / Ctrl+Alt+V copy and paste style; "★ Set as default" makes a selected item the tool default.

## 5. Status

Done (all committed; see `git log --oneline`):
- Core app: capture modes, combine layouts, free canvas, export, projects, library, recovery, tray, hotkeys.
- Annotation editing plan phases 0–6 (`docs/superpowers/plans/2026-09-27-annotation-editing.md`).
- UX Phase A: click-through shapes, stacking controls, Objects list, colour picker, properties panel overhaul, locked canvas.
- Fixes and extras: colour picker crash, image edge panel, custom defaults, multi-select, drag-duplicate, copy/paste style, installer, close-to-tray, start with Windows, shortcut recorder, drag the result out.

Not yet done / next up:
1. **Phase B (user asked for it, not started): template / asset gallery.** A panel that drops down when a tool is picked, showing ready-made visual variants (e.g. arrow styles) to add with one click, plus add/delete/reorder of templates. Likely builds on `AnnotationStyleStore` quick styles and per-tool prototypes. **Plan first and get the user's approval before building.**
2. **Manual acceptance has never been run.** Nothing interactive has been verified by hand by the assistant. See `docs/ACCEPTANCE.md`, and the list in section 6.
3. Not implemented by design so far: OCR; Windows.Graphics.Capture backend (GDI only); mixed-DPI and multi-monitor setups are untested.

## 6. Things the user should confirm by hand (never verified on screen)

- PrintScreen / Ctrl+PrintScreen triggers region capture while the editor is closed to the tray.
- Shortcut recorder in Settings; Start with Windows starts hidden in the tray.
- Drag out handle into Explorer / chat apps.
- Rotated text box editing; arrow endpoint and bend handles; Ctrl+drag duplicate is one undo step.
- Colour picker eyedropper; slider drag is one undo step.
- Locked canvas handles and Outside Dim/Show/Hide.

## 7. Other documents

- `README.md`: user-facing overview.
- `docs/BUILD.md`: build details.
- `docs/superpowers/specs/2026-09-27-snagitopen-design.md`: original specification (note: its "linked annotations" section is superseded by section 4 above).
- `docs/superpowers/plans/`: original plan and annotation-editing plan.
- `docs/ACCEPTANCE.md`, `docs/CAPABILITIES.md`, `docs/RESEARCH.md`, `docs/evidence/verification.md`.
