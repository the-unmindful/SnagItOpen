# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Token budget

The user is on a $20 subscription. Be token-frugal: read files by range (Grep first), avoid re-reading, don't spawn subagents unless asked, keep replies short, and focus on what matters most.

## Read first

`AGENTS.md` and `docs/HANDOFF.md` are authoritative. Read `docs/HANDOFF.md` in full before changing anything: it holds the current status, expected test counts, the user's agreed design decisions (section 4, don't reverse without asking) and past mistakes (section 2a). Update it at the end of each work session (status, commit, test counts).

SnagItOpen is an offline Windows screenshot capture and image editor (Snagit-like), C# / .NET 10 / WPF, no third-party runtime packages. Version 0.2.0 preview; UI/UX upgrade work lives on branch `ui-upgrade`.

## Environment

- Shell is Windows PowerShell 5.1: no `&&` (use `;` or `if ($?) { }`), no `rg` (use the Grep tool). Use single quotes for literals containing `$`.
- .NET SDK 10.0.401 is installed per user and PATH does not persist between shell calls. Prefix commands with:
  `$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH";`
  To launch the Debug exe, also set `$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"`.

## Commands

```powershell
dotnet build .\SnagItOpen.slnx -c Debug /m:1 /nr:false
dotnet test .\tests\SnagItOpen.Core.Tests\SnagItOpen.Core.Tests.csproj -c Debug --no-build
dotnet test .\tests\SnagItOpen.Windows.Tests\SnagItOpen.Windows.Tests.csproj -c Debug --no-build
# single test / class:
dotnet test .\tests\SnagItOpen.Core.Tests\SnagItOpen.Core.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName~ClassOrMethodName"
dotnet run --project .\src\SnagItOpen.App\SnagItOpen.App.csproj
.\scripts\publish.ps1    # Release build + both suites + self-contained win-x64 zip and SHA-256 in artifacts\
```

- Gate: build plus both suites after each coherent batch (not after every minor edit); full `publish.ps1` before packaging. Expect 0 warnings, 0 errors and test counts at or above those in `docs/HANDOFF.md`. Fix nullable warnings rather than suppressing them.
- Never run a build/test in the same parallel tool batch as the edit it verifies (stale passes have happened). After several edits, check `git diff --stat`.
- A running `SnagItOpen.exe` from `bin\` locks output (MSB3027/MSB3021). Stop only your own smoke process (check PID and path), never the user's installed app.
- Smoke runs must set `$env:SNAGITOPEN_DATA` to a temp folder under `E:\Misc\test\opencode-trial\temp\opencode`. After XAML/theme changes, launch the app and confirm the window title is "Untitled - SnagItOpen": missing resource keys only fail at runtime.
- `SnagItOpen.Windows.Tests` needs an interactive desktop (real GDI capture leak test); WPF-object tests must run on STA (`ThemeTokenTests.RunSta`).
- `scripts\install.ps1` replaces the user's installed copy: only run it when asked. Commit locally in small steps (`Area: what changed`); never push unless asked.

## Architecture

Dependency direction: `Core` (net10.0, pure logic, no WPF) ← `Storage` (net10.0), `Imaging`, `Windows` (net10.0-windows) ← `App` (WPF). Put logic in Core where possible so it is testable in `Core.Tests`; `Windows.Tests` references every project including App.

- **Core**: document model and annotations (`Documents/Annotations/`: model, `AnnotationGeometry` for handles/hit-testing/drags, `AnnotationStyle`), `Editing/DocumentOps.cs`, history, layout (vertical/horizontal/free combine), geometry, capture state machines, stitching (scrolling capture).
- **Storage**: asset store, `.sio` projects (schema v2; v1 upgraded on load, newer rejected), settings (v3 with migrations) plus separate `ui-state.json`, presets, styles, capture library, autosave/recovery.
- **Imaging**: STA imaging dispatcher, importer, effects, export, and the single renderer shared by on-screen preview and export (don't diverge them).
- **Windows**: Win32 interop: monitors, GDI capture, window catalog, clipboard, global hotkeys, tray, single instance, scroll input.
- **App**: `Shell/MainWindow.xaml(.cs)` (menus, commands, shortcuts, tray, hotkeys; 1,500+ lines, so read by range and extract new classes instead of growing it), `Editor/CanvasView.cs` (rendering and all pointer gestures), `Editor/EditorViewModel.cs` (document/history state; every edit goes through `Commit` = one undo step), plus `Capture/`, `Controls/`, `Library/`, `Infrastructure/`, `Themes/`.

Key invariants (details in HANDOFF section 4):
- Annotations are canvas objects in document pixels, always above all images, independent of image crop/move/delete; stacking reorders annotations only among themselves.
- Redactions never rotate, are always opaque and always render (even when hidden) so export never leaks covered pixels.
- Content outside a locked canvas is never exported, copied or pinned.

## UI and theming

`DESIGN.md` indexes the visual system; the approved PRD is `docs/superpowers/specs/2026-09-30-ui-ux-upgrade-prd.md`. Plan and get user approval for large features outside it.
- Theme tokens live in `Themes/Tokens.{Light,Dark,HighContrast}.xaml` and metrics in `Themes/Metrics.xaml`. Use `DynamicResource` (or `SetResourceReference` in code) for anything theme-dependent; `StaticResource` won't follow a theme swap. Every key must exist in all token dictionaries (guarded by the token-parity test).
- Put UI text in XAML, not in `CanvasView.OnRender` (drawn text sits under XAML children).
- A WPF element can have only one parent: detach (`Child = null`) before re-parenting.
- Themes never recolor document pixels or change export output.
