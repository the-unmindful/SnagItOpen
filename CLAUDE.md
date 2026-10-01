# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Token budget

The user is on a $20 subscription. Be token-frugal: Grep first and read by range, don't re-read, don't spawn subagents unless asked, keep replies short, focus on what matters.

## Read first (every new session)

1. `docs/HANDOFF.md` in full: status, test counts, user decisions (§4: don't reverse without asking), pitfalls (§2a).
2. The active plan in `docs/superpowers/plans/` (the newest file): task status and what is not done.
3. The "Change safety" section below, before touching code.

SnagItOpen: offline Windows screenshot capture and image editor (Snagit-like), C# / .NET 10 / WPF, no third-party runtime packages. Version 0.2.0 preview on branch `ui-upgrade`.

## Environment

- Windows PowerShell 5.1: no `&&` (use `;`), no `rg` (use Grep), single quotes for literals containing `$`. `R` is the `Invoke-History` alias: never name a helper `R`.
- PATH does not persist: prefix commands with `$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH";`. To launch a Debug exe, also set `$env:DOTNET_ROOT` to the same folder.

## Commands

```powershell
dotnet build .\SnagItOpen.slnx -c Debug /m:1 /nr:false
dotnet test .\tests\SnagItOpen.Core.Tests\SnagItOpen.Core.Tests.csproj -c Debug --no-build
dotnet test .\tests\SnagItOpen.Windows.Tests\SnagItOpen.Windows.Tests.csproj -c Debug --no-build
dotnet test <project> -c Debug --no-build --filter "FullyQualifiedName~ClassOrMethodName"   # single test
.\scripts\publish.ps1   # Release build + both suites + artifacts\win-x64 + zip + SHA-256
```

- If a running app locks `bin\Debug` (MSB3027), build and test with `-c Release` instead. Never stop the user's process. Only stop your own smoke process, after checking its PID and path.
- The app is single-instance: while the user runs any copy, a smoke launch only activates theirs. Check `Get-Process SnagItOpen` first.
- Smoke run: set `$env:SNAGITOPEN_DATA` to a folder under `E:\Misc\test\opencode-trial\temp\opencode`, launch, and expect the title "Untitled - SnagItOpen".
- Review renders: setting `$env:SNAGITOPEN_EVIDENCE=<folder>` makes `ShellIntegrationTests` write PNGs (empty shell, and content with the annotation inspector). Look at them after any UI change.

## Change safety (how not to break the app)

Work loop: write a short plan entry, with status, in the active plan file → edit → **separate** step: build and both suites (never in the same parallel batch as the edit) → for UI changes, renders or a smoke run → commit `Area: what changed` → update the HANDOFF status and test counts. Commit locally only; never push or run `scripts\install.ps1` unless asked. For large features outside the approved PRD, plan first and get the user's approval. **Never change the behaviour of something the user asked for (even to dedupe or "fix" it) without asking first.**

Edits by script: `.Replace()` fails **silently** when the file uses CRLF and the anchor uses LF. Normalise (`.Replace("`r`n","`n")`) and throw if the anchor is missing. Check `git diff --stat` after a batch.

**Ripple map: when you change X, also update Y.**

| Change | Also update |
|---|---|
| New field on an annotation (`Core/Documents/Annotations/Annotations.cs`) | Default must reproduce the old rendering, because old files lack the field (schema stays v2). `AnnotationStyle.Transfer`: keep target-owned fields (Id, Bounds, Text, GroupId, Sizing…). `AnnotationStyleStore.Strip`: drop per-instance fields from saved styles. `DocumentOps` duplicate/paste (remap ids such as GroupId). Renderer (`Imaging/Rendering`, shared by screen and export). `AnnotationPropertiesPanel`. `StyleThumbnailRenderer.Sample`. Tests. |
| Moving annotations | User moves use `Annotation.Translate`; document-wide transforms (scale, normalise) use `MapGeometry`/`Offset`. The magnifier overrides `Translate`. |
| Text box geometry or fonts | Always size boxes through `AnnotationRenderer.Fit`. The in-place editor (`MainWindow.PositionTextEditor`) must use `TextArea`/`GlyphOrigin`, the same as the renderer. |
| New setting | First grep `AppSettings` and the Preferences pages for an existing equivalent (a duplicate `CopyAfterCapture` slipped in once). `AppSettings`: default plus a line in `Sanitize` (no version bump for an optional field). Preferences page (`Shell/Settings/PreferencesPages.cs`). A menu item if it's a quick toggle. UI-only state goes in `UiState` (`ui-state.json`), not settings. |
| New command or shortcut | Menu XAML plus a handler, the key switch in `MainWindow.xaml.cs`, the context menu if it acts on the selection, `ToolCatalog` for tools. The palette and shortcut tests must pass. |
| Capture result | Deliver only through `EditorViewModel.AddCapturesAsync`, which handles destination, auto-copy and the library. Never add layers directly. |
| Capture sessions (scrolling, interval) | Clear the screen with `SessionPanel.HideAppWindowsAsync` (hide and wait for the compositor). Never minimize: the animation got the editor into frame 1. Show the editor again before any editor-owned dialog. |
| Scrolling stitching | `OverlapMatcher` overlap = full-frame rows (content + header + footer). `ScrollComposer.Compose` skips `overlap - footer` rows. Sticky bands live in `ScrollingSession`. |
| Any XAML or UI | Colours only via `DynamicResource`/`SetResourceReference`, with the key in all three `Tokens.*.xaml` files. Implicit styles do **not** reach subclasses (`SetResourceReference(StyleProperty, typeof(Base))`), controls inside a `ToolBar` (they use the `ToolBar.*StyleKey` styles), or `Label`'s colour (it has its own style). Icon-only buttons: `IconButton` with `ShowLabel=false`, guarded by the clip test. A keyed style must be defined before any `StaticResource` that uses it. Put UI text in XAML, not `CanvasView.OnRender`. |
| Bitmaps at DPI | A `RenderTargetBitmap` with DPI = 96×scale already scales the drawing: don't add a ScaleTransform as well. |

Invariants (HANDOFF §4): annotations are canvas objects above all images and independent of them (groups are annotation-only); redactions never rotate, are always opaque and always render; content outside a locked canvas is never exported, copied or pinned; themes never change document pixels or export output.

## Architecture

`Core` (pure logic, no WPF) ← `Storage`, `Imaging`, `Windows` ← `App` (WPF). Put logic in Core where possible; `Windows.Tests` references every project.

- **Core**: annotation model, `AnnotationGeometry` (handles, hit tests), `AnnotationStyle`, `Editing/DocumentOps.cs` (every document edit), layout, capture sessions (`Capture/Sessions.cs`), stitching.
- **Storage**: assets, `.sio` projects (schema v2), settings (v3), `ui-state.json`, built-in styles and the style gallery, library, autosave.
- **Imaging**: STA dispatcher, import, effects, export, `ScrollComposer`, and the renderer shared by preview and export.
- **Windows**: Win32 capture, monitors, clipboard, hotkeys, tray, single instance, scroll input.
- **App**: `Shell/MainWindow.xaml(.cs)` and `MainWindow.Upgrade.cs` (1,500+ lines: read by range and extract new classes). `Editor/CanvasView.cs` (rendering and every gesture). `Editor/EditorViewModel.cs` (every edit goes through `Commit`, one undo step). `Editor/InspectorPanel.cs` and `AnnotationPropertiesPanel.cs`. `Controls/` for shared controls, `Themes/` for tokens and `Controls.xaml`. `DESIGN.md` and the PRD (`docs/superpowers/specs/2026-09-30-ui-ux-upgrade-prd.md`) define the visual system.
