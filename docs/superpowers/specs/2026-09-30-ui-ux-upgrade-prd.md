# SnagItOpen UI/UX upgrade: product requirements (PRD)

Date: 2026-09-30. Status: **approved by the user on 2026-10-01**, with the section 11 answers recorded there. Work happens on branch `ui-upgrade`, not `master`. Approving this PRD also approves the plan for Phase B (template / quick-style gallery, epic E4), which `docs/HANDOFF.md` says must be planned and approved before it is built.

Audience: a worker LLM (or developer) implementing one task at a time. Section 12 lists the tasks. Every task names its files, acceptance criteria and tests.

## 0. How to use this document (worker rules)

1. Read `docs/HANDOFF.md` in full first. Its working rules, verification gate and section 4 decisions override anything here. If this PRD conflicts with section 4, stop and ask.
2. Implement **one task (Uxx) per session**. Read the task, its dependencies, and the files it lists before editing.
3. Shell is Windows PowerShell 5.1: no `&&`, no `rg`. Stop a running `SnagItOpen` process before building.
4. After every task, run the gate: `dotnet build .\SnagItOpen.slnx -c Debug`, then both test projects. The result must be 0 warnings, 0 errors, and test counts at or above the numbers in HANDOFF (currently 162 Core + 128 Windows). Never run a check in the same parallel batch as the edit it verifies.
5. No third-party runtime packages. Use only WPF, .NET 10 and Windows system fonts.
6. Commit locally, one commit per task, message `Area: what changed` (e.g. `UI: theme tokens and ThemeService`). Never push.
7. Keep existing behaviour unless the task says it changes. Never weaken a test to make it pass.
8. At the end of a session, update HANDOFF section 5 (status, commit, test counts) and mark the task done in section 12 of this file (`[x]`).
9. Visual work cannot be proved by unit tests. Add a line for each visual check to `docs/ACCEPTANCE.md` under "UI/UX upgrade", and say in your report which checks are still manual.

## 1. Context: where the app is today

SnagItOpen 0.1.0 is functionally broad: every required row of `docs/CAPABILITIES.md` except OCR is implemented. It passes 290 tests. The UI was deliberately left as stock WPF: the design spec (§3) says "branding, custom themes, and polished visual styling are a later design task." This PRD is that task.

Findings from a code survey (file:line references are as of commit `19ccd38`):

**Visual system**
- There is no theme, resource dictionary or dark mode. Colours are hard-coded in XAML, code-built panels and `CanvasView` (accent `#0078D7`, brand red `#D3252B`, 15+ greys). `App.xaml` holds only a few margin styles.
- Stock Aero2 controls sit next to custom-styled start-card buttons. Toolbars are text-only (`Shell/MainWindow.xaml.cs:37-56` `ToolDefs`) with no icons.
- The lock badge is an emoji. Library pins use a 📌 emoji. Adorner colours (magenta snap lines, green bend handle, yellow tail) are unrelated to each other.

**Layout and information architecture**
- The window stacks a menu, a Band-0 toolbar (file/mode/undo/output), and a Band-1 toolbar holding 17 text tools plus a separate Arrange toolbar. Under that sit a canvas settings bar and the canvas, with a left TabControl (Images / Objects) and a long right-hand ScrollViewer. That ScrollViewer mixes Combine, Canvas, Selected image, image edges and annotation properties.
- Controls are duplicated. Arrange appears in four places (toolbar, properties, Objects list, context menu). Layout mode is in the toolbar and the Layout menu. Canvas size can be set three ways (canvas bar, Canvas section, Canvas size… prompt).
- The canvas background is a raw hex TextBox (`MainWindow.xaml:413`), even though a colour picker exists.

**Feedback**
- The only in-app feedback is one status-bar TextBlock. Long messages are truncated and never clear.
- After a capture there is no toast or preview. Copy-only captures give almost no confirmation.

**Dialogs and settings**
- Nine free-text `Dialogs.Prompt` calls pack several values into one string (e.g. canvas size "x, y, w, h", interval "5, 20", fixed size "640x480"). Parse errors appear only in the status bar.
- Keyboard shortcuts and About are plain MessageBoxes.
- Settings is one long page that shows raw enum names (`AppendBelow`). It does not expose OutsideCanvas, ShowCaptureGallery or DefaultBackground.

**Capture overlay** (`Capture/RegionOverlayWindow.cs`)
- There is no magnifier, pixel coordinates or colour readout.
- The selection commits on mouse-up and cannot be adjusted afterwards. There is no post-selection action bar.
- The hint label is fixed at the top-left, where it can cover the target. Ellipse and freehand selections stay dimmed inside. Mode can't be changed inside the overlay.

**Canvas** (`Editor/CanvasView.cs`)
- One `OnRender` redraws the whole document on every hover change (`:630`).
- Cursors don't match rotate or line-end handles.
- The canvas has no focus visual (`:86`), and Tab is captured to cycle annotations, which traps keyboard focus (`MainWindow.xaml.cs:573`).
- Zoom UI is only a percentage in the status bar.
- Snapping applies to images only.

**Secondary windows**
- Library: a plain list with no search, sort, grid or context menu.
- Pinned window: no zoom, copy or open-in-editor.
- Tray: a basic WinForms menu with no hotkey hints, presets or last region.

**Accessibility**
- There are no focus visuals on the canvas.
- The eye/lock buttons in the Objects list are not focusable (`Editor/ObjectsList.cs:194`).
- Seven tools have no keyboard shortcut.
- High-contrast support is inconsistent because some colours are hard-coded and some come from SystemColors.

**Code health that affects UI work**
- `AnnotationPropertiesPanel` and `ImageEdgePanel` each have their own Row/Slider/Color helpers.
- Dead code: `Dialogs.EditText`, `RelayCommand`/`AsyncCommand`, the `ToolbarButton` style, `BoolToVis`, `NotConverter`, `CanvasView.ResizeRectD`/`ResizeAnnotation`, `RotateDistanceDips`, VM `CycleAnnotation`/`SetLocked`/`ToggleImage`/`CancelImport`.
- The Capture button tooltip says "Ctrl+Shift+1", but the real default is PrintScreen (`MainWindow.xaml:169`).

## 2. Goals and non-goals

### Goals
- G1 **A coherent, modern Windows 11 look.** One design system with Light, Dark and High-contrast themes that follows the system setting and can switch live.
- G2 **Fewer steps from capture to shared result.** A post-selection action bar in the overlay, after-capture toasts, and one-click output actions.
- G3 **A calm, predictable editor layout.** Icon tool rail, a single contextual inspector, one home per control, and no duplicated controls.
- G4 **Precision capture.** Pixel loupe, coordinates, colour picking, adjustable selection handles, keyboard edge nudging and in-overlay mode switching.
- G5 **Discoverability without clutter.** Every command reachable from a searchable command palette (Ctrl+K), tooltips showing shortcuts, and a searchable shortcuts window.
- G6 **Full keyboard and screen-reader use**, including capture.
- G7 **Smooth interaction.** Hover and adorner changes must not re-render the document.
- G8 **The Phase B quick-style gallery**, with visual thumbnails.

### Non-goals (this PRD)
- New imaging features (OCR, AI, background removal, video), cloud or accounts, and the Windows.Graphics.Capture backend.
- Project schema changes. The `.sio` schema stays at version 2. Settings may move to version 3 (additive).
- Localisation (the UI stays English), but strings must not be baked into bitmaps.
- WPF's built-in `ThemeMode` / Fluent styles. Microsoft documents them as still in progress in .NET 10 (they were fixing HighContrast crashes), so we own our dictionaries.
- Mica or acrylic backdrops as requirements. They are optional polish in U33 and never a dependency.

### Success measures
- M1: a 3-screenshot combine-and-copy flow takes no more clicks than today, and one fewer when the action bar is used.
- M2: every command in the menus is reachable by keyboard and appears in the command palette. An automated test checks this (U27).
- M3: moving the pointer over the canvas with ten 1080p layers does not call `DocumentRenderer.Draw`. Measure with a render counter in Debug.
- M4: no hard-coded colour literals remain in `src/SnagItOpen.App` outside `Themes/*.xaml` and the documented exceptions (annotation defaults, colour-picker palette). A Grep check is in U05.
- M5: the manual acceptance lines added by this PRD pass in Light, Dark and High Contrast at 100% and 150% scale.

## 3. Better than Snagit: the differentiators

Snagit is the reference (TechSmith feature page, checked 2026-09-30). It offers capture presets, a library with search, tags and filters, quick styles and themes, templates, a magnifier and a step tool. SnagItOpen should match the everyday workflow and beat it where a local, focused tool can:

| # | Differentiator | Where |
|---|---|---|
| D1 | **Instant action bar** after selecting a region: Edit, Copy, Save, Pin, Append, Drag out, with no editor round-trip | E6 |
| D2 | **Pixel loupe** with coordinates, hex colour and one-key colour copy (`C`) | E6 |
| D3 | **Command palette (Ctrl+K)** listing every command, tool, preset and setting with its shortcut | E15 |
| D4 | **Combine-first**: vertical, horizontal and free layouts are one click, with live spacing guides | E1, E5 |
| D5 | **Measure and spacing guides**: distance labels to neighbours and equal-spacing hints while dragging images *and* annotations | E5 |
| D6 | **Honest privacy**: redaction is visibly labelled secure and blur/pixelate as not secure; fully offline, no account | E3, E7 |
| D7 | **Keyboard-complete capture**: adjust selection edges with arrows, switch mode with number keys, confirm with Enter | E6 |
| D8 | **Drag the result out** from the editor, the toast and the action bar | E1, E7 |
| D9 | **Quick-style gallery with live thumbnails** rendered by the real renderer, so a thumbnail always equals the output | E4 |
| D10 | **Fast start**: the tray menu shows real hotkeys, and the start card shows recent projects and captures | E12, E14 |

## 4. Design principles

1. **Content first.** Chrome is quiet and neutral. The capture is the most colourful thing on screen.
2. **Selection colour must never be confused with annotation colour.** Annotations default to red, so selection chrome uses the blue selection accent and never the brand red. Brand red is used only for primary actions and the logo.
3. **One home per control.** A control may appear in the inspector *and* in a context menu or palette, but it has one visual home on screen.
4. **Progressive disclosure.** The inspector shows the common properties first. Advanced ones sit in collapsed sections that remember their state (existing behaviour).
5. **Never block.** Prefer inline validation, toasts and non-modal panels over modal dialogs. Keep modal dialogs only for data loss and for multi-field input.
6. **Consistent feedback.** Every command either changes the canvas visibly or shows a toast or status message within 100 ms.
7. **Respect the system.** Follow the light/dark setting, high contrast, text scaling, and the "show animations" setting (`SystemParameters.ClientAreaAnimation`).

## 5. Design system (epic E0 foundation)

### 5.1 Files
- `src/SnagItOpen.App/Themes/Tokens.Light.xaml`, `Tokens.Dark.xaml`, `Tokens.HighContrast.xaml`: colours and brushes only, all with the same keys.
- `src/SnagItOpen.App/Themes/Metrics.xaml`: spacing, radii, font sizes and control heights (shared by all themes).
- `src/SnagItOpen.App/Themes/Controls.xaml`: implicit and keyed styles for Button, ToggleButton, RadioButton, CheckBox, TextBox, ComboBox, Slider, ScrollBar, ListBox(Item), TabControl, Expander, Menu/MenuItem, ContextMenu, ToolTip, GridSplitter, StatusBar.
- `src/SnagItOpen.App/Themes/Icons.xaml`: `Geometry` resources (see 5.5).
- `src/SnagItOpen.App/Infrastructure/ThemeService.cs`: chooses and swaps the token dictionary at runtime and raises `ThemeChanged`.
- Every brush reference in XAML must be `{DynamicResource Key}`. Code-built UI must use `SetResourceReference(prop, key)`, never `new SolidColorBrush(...)` for chrome.

### 5.2 Colour tokens (Light / Dark)

| Key | Light | Dark | Use |
|---|---|---|---|
| `Bg.Window` | #F3F3F3 | #202020 | window background, rails |
| `Bg.Surface` | #FFFFFF | #2B2B2B | panels, inspector, cards |
| `Bg.SurfaceAlt` | #F9F9F9 | #323232 | section headers, list hover |
| `Bg.Canvas` | #E8E8E8 | #1A1A1A | area around the document |
| `Bg.Control` | #FFFFFF | #3A3A3A | inputs, buttons |
| `Bg.ControlHover` | #F0F0F0 | #454545 | |
| `Bg.ControlPressed` | #E5E5E5 | #505050 | |
| `Bg.Selected` | #E1ECF9 | #1F3A5C | selected list rows, active tool |
| `Stroke.Divider` | #E0E0E0 | #3D3D3D | splitters, separators |
| `Stroke.Control` | #C8C8C8 | #5A5A5A | input borders |
| `Stroke.Focus` | #1F1F1F | #FFFFFF | 2px focus ring (outer), with 1px `Bg.Surface` inner |
| `Text.Primary` | #1B1B1B | #F2F2F2 | |
| `Text.Secondary` | #5C5C5C | #BDBDBD | hints, captions (≥4.5:1 on Surface) |
| `Text.Disabled` | #A0A0A0 | #6E6E6E | |
| `Text.OnAccent` | #FFFFFF | #FFFFFF | |
| `Accent.Brand` | #C81E26 | #E0474E | primary buttons, logo |
| `Accent.BrandHover` | #A3141B | #F0666C | |
| `Accent.Select` | #0067C0 | #4CC2FF | selection outlines, handles, focus on canvas |
| `Accent.SelectSoft` | #330067C0 | #334CC2FF | selection fill, marquee fill |
| `Accent.Guide` | #E3008C | #FF4FB8 | snap and spacing guides |
| `Status.Success` | #0F7B0F | #6CCB5F | |
| `Status.Warning` | #9D5D00 | #FCE100 | |
| `Status.Error` | #C42B1C | #FF99A4 | |
| `Status.Info` | #0067C0 | #4CC2FF | |
| `Checker.A` / `Checker.B` | #FFFFFF / #D9D9D9 | #3A3A3A / #2E2E2E | transparency checkerboard |
| `Overlay.Dim` | #6E000000 | same | capture overlay shade (existing alpha 110) |
| `Handle.Fill` | #FFFFFF | #FFFFFF | handles on canvas (always white with Accent.Select stroke) |
| `Handle.Special` | #FFB900 | #FFB900 | bend/tail handles (replaces green and yellow; shape distinguishes them) |

High contrast: `Tokens.HighContrast.xaml` maps every key to `SystemColors.*` (e.g. `Bg.Surface` → `WindowBrush`, `Text.Primary` → `WindowTextBrush`, `Accent.Select` → `HighlightBrush`, `Stroke.Focus` → `WindowTextBrush`, `Accent.Brand` → `HighlightBrush`). Use `{x:Static SystemColors.WindowBrushKey}` style keys via `DynamicResource`, so a system theme change updates live.

### 5.3 Metrics
- Spacing scale: 2, 4, 8, 12, 16, 24, 32 (keys `Space.1` … `Space.7`, type `Thickness` and `System.Double` variants as needed).
- Radii: `Radius.Control` 4, `Radius.Card` 8, `Radius.Toast` 8.
- Heights: `Height.Control` 28 (32 for touch-friendly buttons), `Height.ToolButton` 36 (rail), `Height.TitleBar` 40.
- Type (Segoe UI Variable Text if installed, else Segoe UI; font family resource `Font.UI`): Caption 11, Body 12, BodyStrong 12 SemiBold, Subtitle 14 SemiBold, Title 20 SemiBold. Monospace `Font.Mono` = Cascadia Mono, then Consolas.
- Minimum hit target is 24×24 DIP. Rail buttons are 36×36.

### 5.4 Motion
- Durations: `Motion.Fast` 83 ms (hover), `Motion.Normal` 167 ms (toasts, flyouts), `Motion.Slow` 250 ms (panel collapse).
- Easing: CubicEase EaseOut for entering and EaseIn for leaving.
- If `SystemParameters.ClientAreaAnimation` is false, every animation is instant (`ThemeService.AnimationsEnabled`).
- Animations never move document content; they only fade or slide chrome.

### 5.5 Icons
- Monoline 16×16 geometries on a 1.5 px stroke grid, drawn as `Path` with `Stroke` = the foreground of the containing control (so they follow the theme and high contrast). Store them as `StreamGeometry` strings in `Icons.xaml` with keys `Icon.<Name>`.
- Required set: Select, Crop, Arrow, Line, Rectangle, Ellipse, Text, Callout, Highlight, Step, Pen, Redact, Blur, Pixelate, Magnify, Stamp, CutOut; Capture, CaptureWindow, CaptureScreen, CaptureScroll, Import, Paste, Undo, Redo, Copy, Export, Save, Pin, DragOut, Vertical, Horizontal, Free, ZoomIn, ZoomOut, Fit, Eye, EyeOff, Lock, Unlock, Front, Forward, Backward, Back, AlignLeft/CenterX/Right/Top/Middle/Bottom, DistributeH/V, Trash, Duplicate, More, Search, Settings, Help, Close, Check, Warning, Info, Error, Library, Palette, ChevronDown/Right, Plus, Minus, Reset.
- Author the icons yourself (simple geometry). Do not copy icon fonts or third-party SVG sets. If the Segoe Fluent Icons font is present on Windows 11 it may be used through `Font.Icons` with a fallback to the geometry, but the geometry set is required so Windows 10 works.
- A test (U04) loads every `Icon.*` key and checks that it parses and that its bounds fit inside 16×16.

### 5.6 Shared controls (`src/SnagItOpen.App/Controls/`)
Built once and used everywhere; they replace the duplicate helpers in `AnnotationPropertiesPanel` and `ImageEdgePanel`.

| Control | Contract |
|---|---|
| `IconButton` | `Icon` (Geometry), `Label` (optional), `Shortcut` (string); tooltip auto-composed "Label (Shortcut)"; AutomationName = Label |
| `ToolRailButton` | RadioButton-based `IconButton`, 36×36, selected state uses `Bg.Selected` plus a 3 px `Accent.Select` left indicator |
| `SplitButton` | primary action + chevron opening a menu (used for Capture and Copy/Export) |
| `NumberBox` | numeric TextBox with min/max/step, arrow keys ±step (Shift ×10), mouse wheel when focused, drag-scrub on the label, inline invalid state (red border + tooltip), commits on Enter/LostFocus, Esc reverts, supports `IsMixed` (shows "—") |
| `SliderRow` | label + Slider + NumberBox + unit + optional reset; preview while dragging, one undo step on release (move the existing logic here) |
| `ColorSwatchButton` | chip + hex; opens the existing `ColorPicker`; supports `None` and `Mixed` |
| `SegmentedControl` | ToggleButton group with icons or text; supports a mixed state (no segment checked, "Mixed" tooltip) |
| `InspectorSection` | Expander styled as a section header with persisted open state (move the static dictionary into `UiStateStore`, see 5.7) |
| `KeyCap` | the start-card keycap style moved to `Controls.xaml` |
| `InfoBar` | inline message bar (Info/Success/Warning/Error, title, message, optional action button, close) |
| `Toast` host | see E7 |

### 5.7 UI state persistence
Add `src/SnagItOpen.Storage/Settings/UiState.cs` (`ui-state.json`, a separate file from settings so that corruption cannot affect settings). It holds: window bounds and state, left/right panel widths and collapsed flags, inspector section open states, last used theme override, library view mode and sort, and "tips shown" flags. It uses `JsonFileStore` with sanitize (clamp sizes; ignore a window placement that is off-screen).

## 6. Target layout

```text
┌ Menu: File  Edit  Layout  Capture  View  Help ──────────────────────────────────────────┐
│ [● Capture ▾] [Import] [Paste] │ [Vertical|Horizontal|Free] │ [↶][↷] │ ··· │ [⇱ Drag] [Copy ▾] [Export] [Save] │  ← command bar (E1)
├────┬──────────────┬───────────────────────────────────────────────┬────────────────────┤
│Rail│ Layers       │  InfoBar (only when needed)                   │ Inspector (E3)     │
│ ▢  │ [Images|Obj] │                                               │  header: icon+kind │
│ ↗  │  thumbnails  │            canvas (E2)                        │  Style gallery (E4)│
│ ▭  │              │                                               │  sections…         │
│ T  │              │                                   toasts (E7) │                    │
│ …  ├──────────────┴───────────────────────────────────────────────┤                    │
│    │ Recent captures strip (collapsible, E10)                     │                    │
├────┴──────────────────────────────────────────────────────────────┴────────────────────┤
│ status message (auto-clears)        2 images selected · 1280 × 2140 px · [− 100% ▾ +] │  ← status bar (E1)
└────────────────────────────────────────────────────────────────────────────────────────┘
```

- The classic menu stays, for Alt-key access and discoverability, but restyled with icons and shortcuts.
- The Band-0 and Band-1 toolbars, the Arrange toolbar and the canvas settings bar are removed. Their commands move as follows:

| Today | New home |
|---|---|
| Capture / Import / Paste buttons | Command bar (Capture becomes a split button listing every capture mode) |
| Vertical / Horizontal / Free radios | Command bar segmented control |
| Undo / Redo | Command bar icon buttons (tooltip = `UndoLabel` / `RedoLabel`) |
| Copy image / Export / Save project | Command bar, right side; Copy is the primary (brand) button |
| 17 tool radio buttons | Left tool rail (E1) |
| Arrange toolbar | Inspector "Arrange" row plus context menu (removed from the top) |
| Canvas bar (Auto/Locked, W×H, preset, Outside, Drag out, warning) | Inspector "Canvas" section when nothing is selected; Drag out moves to the command bar; the content-outside warning becomes an InfoBar over the canvas |
| Right ScrollViewer (Combine, Canvas, Selected image, edges, annotation props, snap, redaction note) | Contextual inspector (E3) |
| Zoom % text | Zoom control in the status bar |

Responsive rules (window width in DIP): at 1100 or more, everything is shown. Below 1100 the Layers panel collapses to a 36 px strip with an expand button. Below 860 the inspector becomes a flyout opened from a command-bar "Properties" button (F4). The canvas always keeps at least 320 DIP.

## 7. Epics and requirements

Requirement IDs (`R-Ex.n`) are referenced by the tasks in section 12.

### E1 Editor shell: command bar, tool rail, status bar

- **R-E1.1 Command bar.** One 44 DIP row, built in XAML from shared controls (5.6). Groups are separated by 1 px `Stroke.Divider` lines. Buttons are `IconButton`s with labels shown at 1100 DIP or wider and icons only below that. Tooltips show the shortcut from one source of truth: the command registry (R-E15.1).
- **R-E1.2 Capture split button.** The main part runs region capture. The chevron lists Region, Window, Monitor under cursor, All monitors, Last region, then Ellipse, Freehand, Fixed size…, Fixed aspect…, Multiple regions, Scrolling…, Interval…, then Capture presets, Delay, Include cursor and Destination. Each item shows its live global hotkey (from `ActiveHotkeys`), which fixes the wrong "Ctrl+Shift+1" tooltip.
- **R-E1.3 Output group.** Copy (primary; Ctrl+Shift+C), with a chevron for "Copy image", "Copy as file" (a temporary PNG put on the clipboard as a file drop) and "Pin to screen"; Export (Ctrl+E); Save (Ctrl+S). Drag out moves here as an `IconButton` that keeps today's drag and Enter behaviour (`OnDragOutDown/Move/Key`). All are disabled when `HasContent` is false, except Save.
- **R-E1.4 Tool rail.** A vertical 48 DIP rail at the far left made of `ToolRailButton`s grouped by purpose, with dividers:
  1. Select (V), Crop (C), Cut out (U)
  2. Arrow (A), Line (L), Pen (P), Highlight (H)
  3. Rectangle (R), Ellipse (E)
  4. Text (T), Callout (K), Step (N), Stamp (S)
  5. Redact (D), Blur (B), Pixelate (X), Magnify (M)

  New shortcuts: Cut out U, Callout K, Stamp S, Redact D, Blur B, Pixelate X, Magnify M. They are unmodified keys that fire only while the canvas has focus (same rule as today). The rail scrolls when the window is shorter than the rail.
- **R-E1.5 Tool identity.** Each tool has one definition record (`ToolDescriptor`: Kind, Name, Icon key, Shortcut, Description, Group) in `src/SnagItOpen.App/Editor/ToolCatalog.cs`. It replaces `ToolDefs` and the `ToolShortcut` switch. The rail, the Tools menu section, help, the start card and the palette all read it. A test (Windows.Tests) checks unique shortcuts, and that every `ToolKind` has exactly one descriptor and every icon key exists.
- **R-E1.6 Secure/not-secure labelling.** The Redact tooltip and description say "Secure: solid fill in exported images". Blur and Pixelate carry a small `Warning` glyph in the rail tooltip and the text "Visual only, not secure".
- **R-E1.7 Status bar.** 28 DIP high. Left: the status message with an icon (Info/Success/Warning/Error). A message clears after 8 s unless it is an error; hovering shows the full text in a tooltip. Right: selection info, canvas size (click opens the inspector's Canvas section), and the zoom control `[−] [100% ▾] [+]`. The zoom dropdown offers Fit (Ctrl+0), Fit width, 50/100 (Ctrl+1)/200/400%, and Zoom to selection (Ctrl+2). The percentage is editable (NumberBox, 10–800).
- **R-E1.8 Panels.** The Layers panel (the renamed Images/Objects tabs) and the inspector can each collapse to a strip (View menu, and F4 for the inspector). Widths and collapsed state persist in `UiState`. The GridSplitters use `Stroke.Divider`, grow to 4 DIP of hit area, and show a hover highlight.
- **R-E1.9 Title.** The window title stays `{Title}` (project name plus `•` when dirty). ThemeService sets the dark title bar with `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE = 20)` when the Dark theme is active, falling back silently on older builds.

### E2 Canvas: rendering, adorners, focus

- **R-E2.1 Layered rendering (performance).** `CanvasView` gets two child visuals: `DocumentLayer` (a `DrawingVisual` redrawn only when the displayed document, the preview state, the viewport or the theme's checker/backdrop changes) and `AdornerLayer` (redrawn for hover, selection, handles, guides, drafts and labels). A hover change must not call `DocumentRenderer.Draw`. Add an internal `DocumentRenderCount` counter for tests (M3). Behaviour and pixels drawn must not change.
- **R-E2.2 Adorner visuals.** Every adorner colour comes from tokens, cached as frozen brushes and pens that are rebuilt on `ThemeChanged`:
  - Selection outline: 1.5 px `Accent.Select` (images) or 1 px dashed (annotations).
  - Handles: 8 DIP squares for resize, a 10 DIP circle with a rotate glyph for rotate, circles for line ends, diamonds for bends, triangles for callout tails. All use `Handle.Fill` with an `Accent.Select` stroke; bend and tail use `Handle.Special`.
  - Snap and spacing guides: `Accent.Guide`.
  - Size and angle labels: a rounded pill with `Bg.Surface` at 90%, `Text.Primary`, and one cached brush (fixes the per-render allocation).
  - The lock badge becomes the `Icon.Lock` geometry in a small pill, drawn DPI-aware.
  - High contrast uses `SystemColors.Highlight`/`HighlightText`.
- **R-E2.3 Cursors.** A cursor for every handle: rotate (custom `Assets/Cursors/rotate.cur`, generated by a new `scripts/make-cursors.ps1` in the style of `make-icon.ps1`), line end/bend (Cross), tail (Hand), locked-canvas edge (SizeAll) and handles (resize arrows), and not-allowed over locked annotations when dragging. Cursor hit-testing uses the real annotation handles from `AnnotationGeometry`, not the bounding box.
- **R-E2.4 Focus visual.** When the canvas has keyboard focus (not mouse focus), draw a 2 px `Stroke.Focus` ring inset by 1 DIP around the viewport.
- **R-E2.5 Keyboard focus model (fixes the Tab trap).** Tab and Shift+Tab cycle canvas objects only while the canvas has focus *and* something on it is selected. With no selection, Tab leaves the canvas. Esc clears the selection first, so Esc then Tab always escapes. F6 and Shift+F6 cycle focus between the regions: command bar → tool rail → Layers → canvas → inspector → status bar. Each region is a `FocusScope`-like group with `KeyboardNavigation.TabNavigation="Once"` on the rail and command bar, where arrows move within the group.
- **R-E2.6 Backdrop and checkerboard.** The backdrop uses `Bg.Canvas`, and the checkerboard uses `Checker.A`/`B`. The export area casts a soft 1-step shadow (drawn, not an Effect) so the document reads as a sheet. Auto canvas keeps a dashed border; the locked canvas keeps its dotted `Accent.Select` border and handles.
- **R-E2.7 Text editor fidelity.** The in-place TextBox matches the renderer: padding from the annotation's padding, `TextBlock.LineHeight` equal to the renderer's line height, and the caret colour set to the text colour (or `Text.Primary` when the text colour is transparent). Its border is `Accent.Select`.
- **R-E2.8 Empty-but-hidden text.** Replace the drawn grey text with an InfoBar ("All content is hidden. Show all") whose action unhides every image and annotation in one undo step.

### E3 Contextual inspector

- **R-E3.1 One inspector, driven by context.** Replace the right ScrollViewer with `Editor/InspectorPanel.cs` (a code-built `UserControl` using shared controls). Mode is chosen in this order:
  1. **Annotations selected** → the existing annotation panel (R-E3.3).
  2. **Images selected** → **Image**: Position & size (X, Y, W, H NumberBoxes plus a keep-aspect lock toggle), Crop (L, T, R, B NumberBoxes, "Reset crop", and a "Crop tool" button), Orientation (icon buttons for rotate left/right and flip H/V), Edges (the migrated `ImageEdgePanel`), Arrange.
  3. **A drawing tool is active with nothing selected** → **Tool defaults** for that tool (existing prototype editing), with the gallery on top (E4).
  4. **Nothing selected** → **Document**: Layout (only in Vertical/Horizontal: gap, padding, alignment as a 3-icon SegmentedControl, match width/height, target size, allow enlarging); Canvas (Auto/Locked segmented, W × H NumberBoxes, size preset combo, Fit to content, Outside Dim/Show/Hide, Background: a Transparent toggle plus a `ColorSwatchButton` replacing the hex box); Editing (Snap while moving).
- **R-E3.2 Header.** 40 DIP: the kind icon, a title ("Arrow", "3 annotations", "Image 2 of 4", "Document"), and a `More` button (Copy style, Paste style, Set as default, Reset default). The "★" text glyph is dropped for an icon.
- **R-E3.3 Annotation panel migration.** `AnnotationPropertiesPanel` keeps its sections and behaviour but builds everything from the shared controls. Segmented and Combo controls show a mixed state (today they show the first item's value). `Check` becomes three-state for display, and one click sets "on" for all. The **Quick styles** section is replaced by the gallery (E4).
- **R-E3.4 No full rebuild on every change.** The panel rebuilds its control tree only when the *shape* of the context changes (mode, set of selected kinds, or active tool). Otherwise it updates values in place through each row's `Update()` method. This keeps focus, scroll position and slider drags stable during undo/redo and removes the "skip rebuild while focused" workaround in `RefreshPropsSoon`.
- **R-E3.5 Privacy note.** The redaction and blur text at the bottom of today's panel becomes context-specific. A Redact selection shows a Success InfoBar ("Solid fill. Exported images never show what is underneath."). A Blur/Pixelate tool or effect shows a Warning InfoBar ("Visual effect only. Use Redact to hide information securely."). A one-time Info tip on first save explains that projects keep original pixels. These notes appear nowhere else.
- **R-E3.6 Arrange row.** Icon buttons: to front, forward, backward, to back; align (6, enabled for 2 or more); distribute H/V (enabled for 3 or more); and the lock toggle. The Objects list keeps its per-row eye/lock toggles but drops duplicate arrange buttons, if it has any; the worker must read `ObjectsList.cs` to confirm.

### E4 Quick-style gallery (Phase B)

- **R-E4.1 Placement.** When a drawing tool is picked, the top of the inspector shows a gallery: a wrap grid of 56×40 DIP tiles, 3 to 5 per row depending on width. Clicking a tile makes it the tool's prototype (the same effect as editing tool defaults today). With annotations selected, the gallery also appears under the header, and clicking a tile applies that style to the selection in one undo step (existing `AnnotationStyle.Transfer`).
- **R-E4.2 Live thumbnails.** Each tile is rendered by the real `AnnotationRenderer` from a sample annotation of that tool (an arrow from bottom-left to top-right, a 40×24 rectangle, "Aa" for text, "1" for a step, and so on), laid over `Checker.A/B`. Thumbnails are cached per style hash plus theme, and render on the imaging dispatcher or the UI thread under 2 ms per tile. Tiles for blur, pixelate and magnify show a static icon instead (they need image pixels).
- **R-E4.3 Built-in styles.** Each tool ships 6 to 8 built-in styles that can't be deleted, only hidden: e.g. Arrow = red 4 px, red 8 px, black 3 px, white-outlined red, yellow thick, dashed blue, double-headed, curved; Rectangle = red outline, yellow highlight fill, blue rounded, black thick, dashed, shadowed; Text = red bold, black on white box, white on black box, yellow on dark, outlined; Step = red circle, blue circle, black square, letter (A, B, C); Callout = 4 bubble/tail variants; Highlight = yellow, green, pink, blue. They are defined in code in `Storage/Settings/BuiltInStyles.cs` as prototype `Annotation`s.
- **R-E4.4 User styles.** A "+" tile saves the current prototype or selection as a user style (name prompt with a default name like "Arrow 3"). A tile's context menu offers Apply, Set as tool default, Rename, Duplicate, Move left/right, Hide (built-ins) / Delete (user), and Restore built-ins. Tiles can be reordered by drag, and by Alt+Left/Right when focused. The gallery is keyboard navigable (arrows move, Enter applies) and every tile has an AutomationName (the style name plus a short description).
- **R-E4.5 Storage.** Extend `AnnotationStyleFile` additively with `Gallery: Dictionary<string tool, GalleryEntry[]>`, where `GalleryEntry(string Id, string Name, Annotation Style, bool BuiltIn, bool Hidden)`. Migrate the existing `Quick` styles into the gallery of their tool kind once, keeping the old field readable. Keep the 40-style cap per tool. Add Windows.Tests for round trip, migration, corrupt-file fallback, the cap, and hide/restore of built-ins.

### E5 Measure and spacing guides

- **R-E5.1 Annotation snapping.** Snapping extends to annotations (bounds edges and centres) against images, other annotations and the export area, using the existing `SnapEngine`, the existing 6 DIP tolerance and Alt to disable.
- **R-E5.2 Distance labels.** While dragging with snapping on, show the gap in document pixels to the nearest neighbour on each axis (for example "24 px"), in an `Accent.Guide` pill.
- **R-E5.3 Equal spacing.** When the dragged object's gap to one neighbour equals the gap between two other neighbours on the same axis (±1 px), snap to it and draw paired spacing markers. Implement the detection as a pure function in Core (`Layout/SpacingGuides.cs`) with Core tests.
- **R-E5.4 Hold to measure.** Holding Ctrl while hovering (not dragging) with one object selected shows the distances from the selection to the hovered object. This is adorner-only.

### E6 Capture overlay

- **R-E6.1 Two-phase selection.** Releasing the mouse no longer commits in Region, Ellipse and Aspect modes. It enters an **Adjust** phase with 8 resize handles and a move-by-drag body. Enter, a double-click inside, or an action-bar button commits. Esc returns to Idle (a second Esc cancels). A setting, "Capture immediately on release" (default **on**, per the user's answer in section 11), keeps today's one-step behaviour; turning it off enables the Adjust phase. Window, fixed-size and multi-region modes keep their current commit rules. Extend the `RegionSelection` state machine in Core with an `Adjusting` state and add tests for handle drags, clamping to the virtual desktop, aspect lock during adjust, and Esc semantics.
- **R-E6.2 Action bar.** In the Adjust phase, a small toolbar appears beside the selection (below it, flipping above or inside when there is no room; never off-monitor). Buttons: **Edit** (default; the current destination), **Copy**, **Save…** (Save As with the last export folder), **Pin**, **Append** (below/right toggle), **Drag** (drag the result straight out), and **Cancel**. Each shows its key: Enter, Ctrl+C, Ctrl+S, P, A, (drag), Esc. It is excluded from capture like the countdown badge, and the actions map to the existing `CaptureDestination` routes plus `PinnedImageWindow`.
- **R-E6.3 Pixel loupe.** A 120×120 DIP loupe near the cursor shows the frozen snapshot at 8× with a pixel grid and a centre-pixel box. Under it: physical coordinates `x, y`, the selection size, and the hex colour of the centre pixel. `C` copies the hex, and Shift+C copies `rgb(r, g, b)`. The loupe flips side at screen edges, hides while dragging the body, and can be toggled with `M` (the preference is remembered).
- **R-E6.4 Keyboard precision.** In Idle, arrow keys move the cursor as today. In Adjust, arrows move the selection 1 px (Shift: 10); Ctrl+arrows grow or shrink the right/bottom edge; Ctrl+Shift+arrows move the left/top edge; Tab cycles which edge is active (with a highlighted edge). All values are physical pixels. This is Core-testable.
- **R-E6.5 Mode switching inside the overlay.** Number keys switch mode without leaving: 1 Region, 2 Window, 3 Monitor, 4 Ellipse, 5 Freehand, 6 Multi-region. A compact mode strip is shown top-centre on the cursor's monitor. It fades to 30% opacity when the cursor approaches, and is clickable.
- **R-E6.6 Hint placement.** The hint label moves into the mode strip and never covers the selection. It shows at most 3 key hints for the current phase.
- **R-E6.7 Shape fidelity.** Ellipse and freehand cut a clear hole in the dim layer that matches the shape (geometry combine with the even-odd rule). Freehand shows a closing dashed segment from the cursor to the start point.
- **R-E6.8 Window hover.** The hover outline uses `Accent.Select` 2 px plus a label with the window title (trimmed to 40 characters) and its size.
- **R-E6.9 Presets list.** Fixed size and Fixed aspect open a small non-modal picker in the overlay, listing common sizes (640×480, 800×600, 1280×720, 1920×1080, and the last custom size) and aspects (1:1, 4:3, 16:9, 3:2, 9:16, custom), instead of the text prompts.
- **R-E6.10 Countdown.** The countdown badge becomes a 72 DIP ring that animates down (instant when animations are off), with "Esc to cancel" underneath.

### E7 Feedback: toasts and InfoBars

- **R-E7.1 Toast host.** An in-editor toast stack sits at the bottom-right of the canvas, at most 3 visible. A toast has an icon, a title, an optional thumbnail (48 DIP), up to 2 action buttons and a close button. It stays 4 s (8 s with actions), pauses on hover or focus, and is announced through `AutomationProperties.LiveSetting`.
- **R-E7.2 Capture toast (editor hidden).** When a capture completes while the editor is hidden (Copy-only, or presets that save or copy), show a **desktop toast**: a small topmost, capture-excluded, non-activating window at the bottom-right of the work area of the monitor where the capture happened. It shows the thumbnail, "Copied to clipboard" / "Saved to …", and the actions **Edit**, **Pin**, **Show in folder**, plus a drag-out from the thumbnail. It auto-dismisses after 6 s and never steals focus (`WS_EX_NOACTIVATE`). It replaces the tray balloon for capture results; balloons stay only for hotkey problems when no window exists.
- **R-E7.3 Routing rules.** Success → a toast. A recoverable problem (hotkey conflict, content outside the locked canvas, missing font) → an InfoBar at the top of the canvas with an action. An error that loses no data → an error toast with "Details" (opens the full message). Data-loss questions (unsaved changes, overwrite) → a modal dialog. `Dialogs.Error` stays for real failures; `EditorViewModel.Status` stays for the status bar.
- **R-E7.4 Standard confirmations.** "Copied", "Exported to <file>" (action: Show in folder), "Saved project", "Pinned", "Style applied", "Undo: <label>" (only for undo triggered from a toast action).

### E8 Dialogs

- **R-E8.1 Themed dialog base.** Add `Infrastructure/DialogWindow.cs`: a themed modal window (title, content, a right-aligned button row with the primary button styled `Accent.Brand`, Esc = Cancel, Enter = primary, owner-centred, `ShowInTaskbar=false`). `Dialogs.Prompt`, `Dialogs.Info` and `Dialogs.Error` are re-implemented on it with the same signatures, so callers don't change. `Dialogs.Confirm(owner, title, message, primary, secondary, cancel)` replaces the raw `MessageBox` calls in `MainWindow.xaml.cs` (save changes, flattened cut-out) and `App.xaml.cs` (recovery, data-folder error, unhandled error).
- **R-E8.2 Structured input instead of packed strings.** Replace each multi-value prompt with a small typed dialog using `NumberBox`es and inline validation (the OK button is disabled while any field is invalid, and the reason shows under the field):

| Today (`MainWindow.xaml.cs`) | New dialog |
|---|---|
| Canvas size "x, y, w, h" (`:1089`) | Removed: the inspector Canvas section covers it (R-E3.1). The menu item focuses that section |
| Scale document "%" (`:1196`) | `ScaleDialog`: percent or target width/height with a lock, "Scale content and canvas" vs "Canvas only" |
| Join seam overlap (`:1226`) | `SeamDialog`: overlap rows NumberBox with a live before/after preview of the seam |
| Fixed size "640x480" (`:1348`) | In-overlay picker (R-E6.9) |
| Fixed aspect "16:9" (`:1358`) | In-overlay picker (R-E6.9) |
| Interval "5, 20" (`:1487`) | `IntervalDialog`: interval seconds (1–60), maximum frames (1–200), destination |
| Rename image, save layout preset, save capture preset, save quick style | Keep `Dialogs.Prompt` (single value), themed |

- **R-E8.3 Keyboard shortcuts window.** Replaces the MessageBox. A non-modal themed window with a search box, grouped lists (Capture global, File, Edit, Tools, View, Canvas, Capture overlay), keycaps rendered with `KeyCap`, and live global hotkeys. It is generated from the command registry (R-E15.1) and `ToolCatalog`, so it can never drift. A "Change global shortcuts…" link opens Settings at the Shortcuts page. F1 opens it.
- **R-E8.4 About window.** Themed: logo, version (from the assembly), "Local and offline", licence (MIT), a link to `THIRD-PARTY-NOTICES.md`, and "Open data folder".

### E9 Settings

- **R-E9.1 Navigation.** Rebuild `Shell/SettingsWindow.cs` as a 760×560 themed window with a left navigation list and pages: **General**, **Capture**, **Shortcuts**, **Output & library**, **Appearance**, **Advanced**. The Save/Cancel buttons stay at the bottom. Settings apply on Save only (today's behaviour). A search box at the top filters settings by label across pages.
- **R-E9.2 Human labels.** Enum values are shown with friendly names through one `DisplayNames` helper: `AppendBelow` → "Add below current image", `AppendRight` → "Add to the right", `AddToCanvas` → "Place on the free canvas", `NewDocument` → "Start a new composition", `CopyOnly` → "Copy to clipboard only". The same helper is used by the Capture split button and the palette.
- **R-E9.3 Expose missing settings.** Add to the pages: Outside-canvas display (Appearance), Show recent captures strip (General), Default background (Output: Transparent toggle + `ColorSwatchButton`), Default layout (Output: mode, gap, padding), "Capture immediately on release" (Capture, R-E6.1), "Show loupe" (Capture, R-E6.3), "After a capture while the editor is hidden: show a desktop toast" (Capture, default on, R-E7.2).
- **R-E9.4 Appearance.** Theme: System (default) / Light / Dark. High contrast always wins when Windows has it on, and the page says so. Accent for selection: System accent / Blue (default). A live preview swatch of the canvas chrome. Changing theme applies immediately in the settings window as a preview and is reverted on Cancel.
- **R-E9.5 Shortcuts page.** The existing `HotkeyBox` recorders in a two-column table (Action, Shortcut, Reset icon), the duplicate warnings shown as InfoBars, and a read-only list of in-editor shortcuts with a link to the shortcuts window. Capture preset hotkeys are listed here too (edited in the preset editor, R-E9.6).
- **R-E9.6 Capture preset editor.** The Capture page gets a presets list (add, duplicate, rename, delete, reorder) with a form for every `CapturePreset` field (mode, shape, delay, cursor, destination, fixed size/aspect, hotkey, output folder with a Browse button, file-name template with a live example such as `Capture 2026-09-30 1405 (1).png`, format, copy). This replaces the "save capture preset" prompt as the only way to manage presets. The presets file schema is unchanged.
- **R-E9.7 Settings version 3.** `AppSettings` gets the new fields additively (`ThemeMode`, `SelectionAccent`, `CaptureOnRelease`, `ShowLoupe`, `DesktopToasts`), `CurrentVersion = 3`, and a migration 2 → 3 that only fills defaults. Extend `SettingsMigrationTests` (v1 → v3 and v2 → v3, unknown enum values fall back).

### E10 Library and recent-captures strip

- **R-E10.1 Library window.** Rebuild `Shell/LibraryWindow.cs` as a themed 960×640 window: a toolbar with a search box (name, dimensions, date text), sort (Newest, Oldest, Name, Size), filter chips (All, Pinned, Today, This week), and a Grid/List view toggle (persisted in `UiState`). Grid tiles are 160×110 thumbnails with name and size under them, and pinned tiles show an `Icon.Pin` badge (replacing the 📌 emoji).
- **R-E10.2 Preview pane.** Selecting one item shows a right-hand preview (fit), its details (size, date, file size) and the actions Add to composition, Open as new, Copy, Pin to screen, Pin in library, Rename, Show in folder, Delete.
- **R-E10.3 Keyboard and menus.** Enter adds, Delete deletes (with the themed confirm, "Don't ask again" stored in `UiState`), F2 renames, Ctrl+C copies, Space toggles a large preview. Right-click shows the same actions. Multi-select and drag out keep working.
- **R-E10.4 Storage line.** Show usage as a small bar ("312 MB of 500 MB · 142 of 200 captures") with a link to the Output & library settings page.
- **R-E10.5 Strip polish.** `Library/CaptureGallery.cs`: the header's instruction sentence is replaced by the title "Recent captures", a count, and "Open library" (Ctrl+L). Tiles get hover and selected states, a context menu (Add, Open as new, Copy, Pin to screen, Delete) and a keyboard focus ring. The strip collapses to a 28 DIP header bar (state persisted) instead of only hiding from the View menu.
- **R-E10.6 Search index.** Search and filters run on the existing `CaptureHistoryStore` entries in memory. No new metadata is captured in this PRD (window title or app name capture is a possible future item and is out of scope).

### E11 Pinned image window

- **R-E11.1 Chrome.** A 1 px `Accent.Select` border that shows only on hover or focus, plus a soft shadow. A small hover toolbar at the top-right: Copy, Open in editor, Opacity, Click-through, Close.
- **R-E11.2 Zoom.** Ctrl+wheel zooms 10%–800% about the cursor (it currently changes opacity). Opacity moves to Alt+wheel and the toolbar. Ctrl+0 fits to the original size, and the zoom is shown briefly in a pill.
- **R-E11.3 Click-through.** A toggle makes the window ignore the mouse (`WS_EX_TRANSPARENT`). While it is on, a small non-click-through tab at one corner turns it off again, and the tray menu gets "Pinned images → Disable click-through for all".
- **R-E11.4 Actions.** Ctrl+C copies, Ctrl+S saves as PNG, `E` opens it in the editor (as the configured destination), and the context menu lists every action. The keyboard also works (Esc closes, arrows move 1 px/10 px).

### E12 Tray menu

- **R-E12.1 Themed menu.** Replace the WinForms `ContextMenuStrip` look with a renderer that uses the current theme colours (a `ToolStripProfessionalRenderer` subclass fed by `ThemeService` colour values, since the tray menu stays WinForms). Items get 16 px icons from the same geometry set, rasterised at the menu's DPI.
- **R-E12.2 Content.** Order: Open editor; separator; Capture region, Capture window, Capture all monitors, Last region, Scrolling capture, each showing its live hotkey right-aligned; a Capture presets submenu (hidden when there are none); separator; Recent captures submenu (last 5, with thumbnails; click opens in the editor); Pinned images submenu (count, "Close all", "Disable click-through for all"); separator; Settings…, Keyboard shortcuts…; separator; Quit SnagItOpen.
- **R-E12.3 Tooltip.** The tray tooltip reads "SnagItOpen: PrintScreen to capture" using the live region hotkey.

### E13 Accessibility

- **R-E13.1 Focus visuals everywhere.** Every focusable control shows a `Stroke.Focus` 2 px ring (with a 1 px inner `Bg.Surface` gap) for keyboard focus only, via `FocusVisualStyle` in `Controls.xaml`. This includes the canvas (R-E2.4), gallery tiles, library tiles, strip tiles and rail buttons.
- **R-E13.2 Reachability.** The Objects-list eye and lock buttons become focusable (`ObjectsList.cs:194`), with AutomationNames "Hide Arrow 3" / "Lock Arrow 3" and toggle state exposed. Rows support Space (toggle visibility) and Ctrl+L (toggle lock) when focused.
- **R-E13.3 Names.** Every `IconButton` gets its AutomationName from `Label`, and every command-bar or rail control has a name, a help text (its description) and an `AcceleratorKey` (its shortcut). A test walks `MainWindow`'s visual tree after load and fails on any focusable element without a name (Windows.Tests, STA).
- **R-E13.4 High contrast.** With Windows high contrast on, `ThemeService` loads `Tokens.HighContrast.xaml`. Nothing relies on colour alone: selected tools also have the left indicator, errors have icons, and the secure/not-secure labels are text. Canvas content is not recoloured, but adorners use `SystemColors.Highlight`.
- **R-E13.5 Text scaling.** The UI must remain usable at 200% text scaling (`Settings > Accessibility > Text size`). Layouts use `Auto` sizes for text rows. The rail and command bar may scroll or overflow into a "More" menu, and nothing is clipped without a way to reach it.
- **R-E13.6 Capture overlay access.** The overlay announces the current mode and phase through a hidden live-region element ("Region capture. Drag to select, or press Enter for the window under the cursor."). The action bar is keyboard reachable with Tab in the Adjust phase.
- **R-E13.7 Reduced motion.** Honour `ThemeService.AnimationsEnabled` everywhere (5.4).

### E14 Start card and first run

- **R-E14.1 Start card.** Restyle the existing card with tokens and add two columns under the three action buttons: **Recent projects** (last 5 `.sio` paths from a new `UiState.RecentProjects` list, missing files greyed with "Not found") and **Recent captures** (last 6 thumbnails from the history store; click adds). The shortcuts block stays, generated from `ToolCatalog` and the command registry. The card still hides when `IsEmpty` is false.
- **R-E14.2 Recent projects.** `File → Open recent` (last 10) plus the start-card list. Paths are added on successful open or save and removed via the item's context menu ("Remove from list").
- **R-E14.3 First run.** On the first launch (no `ui-state.json`), show a dismissible InfoBar on the start card: "SnagItOpen keeps running in the tray. Press PrintScreen to capture from anywhere." with the live key and "Change shortcut". There is no multi-page tour.
- **R-E14.4 Coach marks.** Show at most one one-time tip per feature, stored in `UiState.TipsShown`: the first time the action bar appears ("Enter edits, Ctrl+C copies"), the first image in Free mode ("Hold Alt to turn snapping off"), and the first redaction ("Redaction is secure; blur is not"). Each tip is a small toast and never modal.

### E15 Command registry and command palette

- **R-E15.1 Command registry.** Add `Shell/Commands.cs`: a `CommandRegistry` of `AppCommand` records (`Id`, `Title`, `Category`, `Icon`, `Gesture` (display string), `Keywords`, `CanExecute: Func<bool>`, `Execute: Action`). Register every menu item, toolbar action, tool (from `ToolCatalog`), layout preset, capture preset and settings page. Menus, the command bar, the shortcuts window and the palette read titles and gestures from it. The existing `OnPreviewKeyDown` switch stays as the key dispatcher in this PRD (moving key handling into the registry is a later refactor). A test checks that every gesture listed in the registry is handled by `OnPreviewKeyDown` or is a global hotkey.
- **R-E15.2 Palette.** Ctrl+K (and `View → Command palette`) opens a centred popup over the editor: a search box and a result list showing icon, title, category and keycap. Fuzzy matching covers title and keywords (subsequence scoring in a pure Core function, `Core/Search/FuzzyMatcher.cs`, with Core tests). Enter runs, Esc closes, arrows move, and results that can't execute are shown disabled with the reason as a tooltip. Recently used commands appear first when the query is empty (stored in `UiState`).
- **R-E15.3 Palette coverage test.** A Windows.Tests test builds the menus and the registry and fails if any menu item has no registry entry (M2).

## 8. Data and settings changes (summary)

| Store | Change | Compatibility |
|---|---|---|
| `settings.json` (`AppSettings`) | Version 3: `ThemeMode`, `SelectionAccent`, `CaptureOnRelease`, `ShowLoupe`, `DesktopToasts` | Additive; v1/v2 migrate with defaults |
| `ui-state.json` (new, `UiState`) | Window placement, panel widths/collapsed, inspector sections, library view/sort, recent projects, recent commands, tips shown, "don't ask again" flags | New file; missing or corrupt → defaults, never blocks startup |
| Annotation styles file | `Gallery` per tool (`GalleryEntry`), old `Quick` migrated once | Additive; old files open, old field kept readable |
| `.sio` project schema | **None** | Stays at version 2 |
| Capture presets file | None (only a new editor UI) | Unchanged |

## 9. Non-functional requirements

- **N1 Performance.** Hover and adorner changes don't re-render the document (M3). Theme switching completes in under 300 ms with a 10-layer document. Gallery thumbnails render within one frame budget per 8 tiles. The inspector update on selection change (without a rebuild) takes under 16 ms.
- **N2 No new dependencies.** WPF and .NET 10 only, no NuGet runtime packages, and no icon fonts or third-party art.
- **N3 Offline.** Nothing added here uses the network.
- **N4 DPI.** Every new window is PerMonitorV2-correct. Overlay chrome (loupe, action bar, mode strip) sizes in DIPs for the monitor it is on, and the loupe samples physical pixels.
- **N5 Capture exclusion.** The action bar, loupe, mode strip, desktop toasts and countdown are excluded from captures (`SetWindowDisplayAffinity`, via the existing `AppNative` helper) or live inside the overlay itself.
- **N6 Stability.** Theme switching, panel collapse and inspector changes never change document state or history. No UI change may alter export pixels: the existing render and export tests must pass unchanged.
- **N7 Code boundaries.** Core stays free of WPF/Win32. New pure logic (spacing guides, fuzzy matcher, overlay adjust state) goes in Core with tests. Theme and controls live in App.

## 10. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Restyling every control breaks keyboard or automation behaviour | Keep control types; restyle via templates only; the automation-name test (R-E13.3) and M2 test catch regressions |
| Big-bang shell rewrite leaves the app unbuildable | The shell migration is split into U10–U14; the old toolbars are removed only in U14 after the new ones work |
| Layered canvas changes pixels | U07 must keep all render/export tests green and add a hover test using `DocumentRenderCount` |
| Two-phase capture annoys users who want one-step capture | "Capture immediately on release" setting, plus Enter works during drag-release |
| Themed WinForms tray menu at mixed DPI | Rasterise icons per DPI; fall back to the default renderer if theming throws |
| Scope creep (OCR, AI) | Non-goals in section 2; any new feature needs a separate approval |
| Worker models lose context in large files (`MainWindow.xaml.cs` is 1,500+ lines) | Tasks name the exact methods to move; extract new classes rather than growing `MainWindow` |

## 11. Open questions for the user

1. Default capture behaviour: two-phase Adjust with the action bar (proposed default) or immediate capture on release?
2. Should Copy be the brand-red primary button, or should Export or Save be?
3. Is removing the top toolbars in favour of a tool rail acceptable, or should an optional "classic toolbar" view stay?
4. Selection accent: fixed blue (proposed) or follow the Windows accent colour by default?

**Answers (user, 2026-10-01). These override the proposals above and anywhere else in this PRD:**

1. **Both behaviours, immediate capture is the default.** `CaptureOnRelease` defaults to **true** (today's one-step behaviour). The Adjust phase with the action bar is available by turning the setting off (Settings → Capture, "Capture immediately on release"). R-E6.1 and U28/U29 still build the Adjust phase in full; only the default changes.
2. **Copy is the brand-red primary button** (as proposed).
3. **Keep both layouts.** The tool rail is the default. A "Classic toolbar" option (View menu toggle and Settings → Appearance, persisted as `UiState.ClassicToolbar`, default off) shows the tools as a horizontal icon+text toolbar under the command bar instead of the rail. Both are built from `ToolCatalog`, so they never drift. U14 therefore keeps a tools toolbar (restyled, catalog-driven) behind the toggle instead of deleting it; the Band-0 toolbar, Arrange toolbar and canvas bar are still removed.
4. **Fixed blue selection accent** (as proposed). "System accent" stays optional polish in U39.

## 12. Tasks

Each task is one session and one commit. "Deps" must be done first. "Done when" lists the checks. Every task also runs the gate in section 0 and adds its manual lines to `docs/ACCEPTANCE.md` → "UI/UX upgrade".

### Milestone 1: foundation (no visible layout change)

- [ ] **U01 Theme tokens and ThemeService.** Deps: none. Req: 5.1, 5.2, R-E1.9, R-E13.4.
  Files: `App/Themes/Tokens.{Light,Dark,HighContrast}.xaml`, `App/Infrastructure/ThemeService.cs`, `App.xaml` (merge dictionaries), `App.xaml.cs` (init).
  Do: load tokens by `System | Light | Dark` (read `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`), switch to high contrast when `SystemParameters.HighContrast`, listen to `SystemEvents.UserPreferenceChanged`, raise `ThemeChanged`, and set the dark title bar for every window. The theme mode is a constructor argument for now (U03 adds the setting).
  Done when: a Windows.Tests test loads each token dictionary and asserts that all three have the same key set and every value is a `Brush` or `Color`; the app starts unchanged in Light.

- [ ] **U02 Metrics and control styles.** Deps: U01. Req: 5.3, 5.4, R-E13.1.
  Files: `App/Themes/Metrics.xaml`, `App/Themes/Controls.xaml`, `App.xaml`.
  Do: implicit styles for the controls listed in 5.1 using `DynamicResource` tokens only; a shared `FocusVisualStyle`; `ThemeService.AnimationsEnabled`. Move `StartButton`, `StartPrimaryButton`, `KeyCap` from `MainWindow.xaml` into `Controls.xaml` with the same keys. Delete the unused `ToolbarButton` style and `BoolToVis`.
  Done when: the app builds and runs with restyled stock controls in Light and Dark (manual); keyboard focus shows rings on buttons, checkboxes and text boxes (manual).

- [ ] **U03 UiState store and settings v3.** Deps: none. Req: 5.7, R-E9.7, section 8.
  Files: `Storage/Settings/UiState.cs`, `Storage/Settings/AppSettings.cs`, `Shell/AppServices.cs`, `tests/SnagItOpen.Windows.Tests/SettingsMigrationTests.cs`, new `UiStateTests.cs`.
  Do: `UiState` record plus `UiStateStore` on `JsonFileStore` with sanitize; the new `AppSettings` fields and 2 → 3 migration; wire `ThemeService` to `ThemeMode`. Move the inspector section open-state dictionary (`AnnotationPropertiesPanel.cs:25-28`) into `UiState`. Save window placement on close and restore it on start (ignore off-screen).
  Done when: tests for v1→v3, v2→v3, unknown enums, corrupt `ui-state.json` → defaults, and off-screen bounds rejection pass.

- [ ] **U04 Icon geometry set.** Deps: U01. Req: 5.5.
  Files: `App/Themes/Icons.xaml`, test `IconResourceTests.cs`.
  Done when: every icon in the 5.5 list exists; the test parses each `Icon.*` and asserts bounds within 0–16; a scratch preview (not committed) was checked at 100% and 200% in both themes.

- [ ] **U05 Remove hard-coded chrome colours.** Deps: U01, U02. Req: M4, R-E2.6 (backdrop, checker only).
  Files: `MainWindow.xaml`, `SettingsWindow.cs`, `LibraryWindow.cs`, `CaptureGallery.cs`, `PinnedImageWindow.cs`, `HotkeyBox.cs`, `ColorPicker.cs` (chrome only), `CanvasView.cs` (backdrop and checker only).
  Do: replace literals with `DynamicResource` / `SetResourceReference`. Documented exceptions: annotation default colours (`MainWindow.xaml.cs:416-428`), the picker palette (`ColorPicker.cs:23-28`), and overlay colours (done in U25/U28).
  Done when: a Grep for `#FF[0-9A-F]{6}|Color.FromRgb|Color.FromArgb|new SolidColorBrush` in `src/SnagItOpen.App` returns only the exceptions (list them in the commit message); Dark theme shows no white panels (manual).

- [ ] **U06 Shared controls.** Deps: U02, U04. Req: 5.6.
  Files: `App/Controls/*.cs` (IconButton, ToolRailButton, SplitButton, NumberBox, SliderRow, ColorSwatchButton, SegmentedControl, InspectorSection, InfoBar), `Controls.xaml` templates.
  Do: implement the contracts. `NumberBox` parsing and clamping sit in a small pure helper with tests. Do not yet migrate the panels.
  Done when: `NumberBoxTests` cover min/max clamp, invalid text, Esc revert, mixed display, Shift step; `IconButton` composes its tooltip and AutomationName (STA test).

### Milestone 2: canvas

- [ ] **U07 Layered canvas rendering.** Deps: U05. Req: R-E2.1, M3, N6.
  Files: `Editor/CanvasView.cs`, test `CanvasLayerTests.cs`.
  Do: split `OnRender` into a document `DrawingVisual` and an adorner `DrawingVisual` (override `VisualChildrenCount`/`GetVisualChild`); invalidate them separately; add `DocumentRenderCount`.
  Done when: a test with 10 layers moves the hover point 50 times and asserts `DocumentRenderCount` is unchanged; all existing render/export tests pass unchanged; no visual difference at 25/100/400% (manual).

- [ ] **U08 Adorners and cursors.** Deps: U07, U04. Req: R-E2.2, R-E2.3, R-E2.6.
  Files: `Editor/CanvasView.cs`, `scripts/make-cursors.ps1`, `App/Assets/Cursors/rotate.cur`, `SnagItOpen.App.csproj` (resource).
  Do: token-based frozen pens and brushes rebuilt on `ThemeChanged`; new handle shapes; lock badge from `Icon.Lock`; sheet shadow; cursor hit-testing via `AnnotationGeometry` handles. Delete dead `RotateDistanceDips`, `ResizeRectD`, `ResizeAnnotation` after confirming no test uses them.
  Done when: a test asserts the cursor at a rotate-handle point is the rotate cursor and at a line end is Cross; manual checks in Light, Dark, High contrast.

- [ ] **U09 Keyboard focus model.** Deps: U07. Req: R-E2.4, R-E2.5, R-E13.2.
  Files: `Editor/CanvasView.cs`, `Shell/MainWindow.xaml.cs` (`OnPreviewKeyDown` Tab case `:573`), `Editor/ObjectsList.cs`.
  Do: keyboard-only focus ring; the Tab rule; F6/Shift+F6 region cycling (a list of region roots, update in U14); focusable eye/lock buttons with names and Space / Ctrl+L.
  Done when: an STA test sends Tab to a focused canvas with no selection and asserts focus leaves it; manual keyboard walk-through.

### Milestone 3: shell

- [ ] **U10 ToolCatalog and CommandRegistry.** Deps: U04. Req: R-E1.5, R-E15.1, R-E9.2.
  Files: `Editor/ToolCatalog.cs`, `Shell/Commands.cs`, `Infrastructure/DisplayNames.cs`, `MainWindow.xaml.cs` (replace `ToolDefs` and `ToolShortcut` with the catalog; add the new tool keys).
  Do: register every menu item's action. Menus keep their XAML but read `InputGestureText` from the registry (set in code after `InitializeComponent`). Fix the Capture tooltip.
  Done when: tests for unique tool shortcuts, one descriptor per `ToolKind`, icon keys exist, and every registry gesture is handled (R-E15.1).

- [ ] **U11 Command bar.** Deps: U06, U10. Req: R-E1.1, R-E1.2, R-E1.3.
  Files: `MainWindow.xaml` (new `Border` row under the menu), `MainWindow.xaml.cs`.
  Do: add the command bar *alongside* the old toolbars (they are removed in U14). Implement "Copy as file" (temp PNG in `%LOCALAPPDATA%\SnagItOpen\cache\clip`, cleaned on start).
  Done when: every command-bar action works and matches its old toolbar twin (manual); Copy as file pastes into Explorer (manual).

- [ ] **U12 Tool rail.** Deps: U06, U10. Req: R-E1.4, R-E1.6.
  Files: `MainWindow.xaml` (new first grid column), `MainWindow.xaml.cs` (`BuildToolBar` builds the rail from `ToolCatalog`).
  Done when: all 17 tools selectable by click and key; `ToolsBar` still present until U14; secure/not-secure tooltips correct.

- [ ] **U13 Status bar and zoom control.** Deps: U06, U10. Req: R-E1.7.
  Files: `MainWindow.xaml`, `MainWindow.xaml.cs`, `EditorViewModel.cs` (`StatusKind`, auto-clear timer), `CanvasView.cs` (`ZoomToSelection`, `FitWidth`).
  Done when: a VM test checks that a non-error status clears after the timeout (inject the clock/timer); Ctrl+2 zooms to selection.

- [ ] **U14 Remove old toolbars; panels and responsive layout.** Deps: U11, U12, U13, U15 (the canvas bar controls must already live in the inspector). Req: section 6 table, R-E1.8.
  Files: `MainWindow.xaml`, `MainWindow.xaml.cs`.
  Do: delete the Band-0 toolbar, the Arrange toolbar and the canvas bar; replace the Band-1 tools toolbar with a catalog-driven "Classic toolbar" shown instead of the rail when `UiState.ClassicToolbar` is on (View menu toggle, section 11 answer 3); move the content-outside warning into an InfoBar; add collapse/flyout behaviour and persist panel state; finish F6 regions.
  Done when: the window works at 1280, 1000 and 800 DIP widths (manual); no handler is left orphaned (build has no unused-handler XAML errors; Grep each deleted `Click=` name).

### Milestone 4: inspector

- [ ] **U15 Inspector: Document and Image modes.** Deps: U06, U03. Req: R-E3.1 (modes 2 and 4), R-E3.2, R-E3.4.
  Files: `Editor/InspectorPanel.cs`, `Editor/ImageEdgePanel.cs` (use shared controls), `MainWindow.xaml` (host), `MainWindow.xaml.cs` (canvas-bar handlers move into the panel or call VM directly).
  Done when: all Combine, Canvas and Selected-image fields work as before; background uses the swatch; STA test: selecting another image updates values without rebuilding (`RebuildCount` stays 1).

- [ ] **U16 Annotation panel migration.** Deps: U15. Req: R-E3.3, R-E3.4.
  Files: `Editor/AnnotationPropertiesPanel.cs`, `MainWindow.xaml.cs` (`RefreshPropsSoon` simplified).
  Done when: mixed state shown for segmented, combo and check rows (STA test with two differing arrows); undo during a focused field updates it in place; slider drag is still one undo step.

- [ ] **U17 Privacy notes and Arrange row.** Deps: U16, U18. Req: R-E3.5, R-E3.6.
  Done when: the bottom disclaimer is gone; the right InfoBar appears for Redact vs Blur/Pixelate; Arrange row enables by count (STA test).

### Milestone 5: feedback and dialogs

- [ ] **U18 InfoBar and toast host; routing.** Deps: U06. Req: R-E7.1, R-E7.3, R-E7.4, R-E2.8.
  Files: `App/Controls/ToastHost.cs`, `MainWindow.xaml`, `EditorViewModel.cs` (a `Notify(kind, title, message, actions)` event beside `Status`), callers in `MainWindow.xaml.cs` for copy/export/save/pin.
  Done when: STA test: 4 toasts → 3 visible, oldest dropped; hover pauses dismissal; confirmations from R-E7.4 appear (manual).

- [ ] **U19 Themed dialog base.** Deps: U02. Req: R-E8.1.
  Files: `Infrastructure/DialogWindow.cs`, `Infrastructure/Dialogs.cs`, `MainWindow.xaml.cs`, `App.xaml.cs`. Delete unused `Dialogs.EditText`.
  Done when: Grep finds no `MessageBox.Show` in `src/SnagItOpen.App` except inside `Dialogs.cs` fallback for failures before the theme loads.

- [ ] **U20 Structured dialogs.** Deps: U19, U06. Req: R-E8.2.
  Files: `Shell/Dialogs/ScaleDialog.cs`, `SeamDialog.cs`, `IntervalDialog.cs`, `MainWindow.xaml.cs`. Parsing/validation logic stays out of the window (pure helper with tests).
  Done when: validation tests pass; each dialog replaces its prompt call; the Canvas size menu item focuses the inspector section.

- [ ] **U21 Shortcuts and About windows.** Deps: U10, U19. Req: R-E8.3, R-E8.4.
  Done when: every registry gesture and tool key appears in the shortcuts window (STA test counts rows against the registry); F1 opens it; search filters.

### Milestone 6: gallery, guides, settings

- [ ] **U22 Gallery storage and built-in styles.** Deps: U03. Req: R-E4.3, R-E4.5.
  Files: `Storage/Settings/AnnotationStyleStore.cs`, `Storage/Settings/BuiltInStyles.cs`, tests in `AnnotationStyleStoreTests.cs`.
  Done when: round trip, `Quick` → `Gallery` migration (once), corrupt-file fallback, 40 cap, hide/restore built-ins tests pass.

- [ ] **U23 Gallery UI.** Deps: U22, U16. Req: R-E4.1, R-E4.2, R-E4.4, D9.
  Files: `Editor/StyleGallery.cs`, `Editor/StyleThumbnailRenderer.cs`, `AnnotationPropertiesPanel.cs` (remove the Quick styles section).
  Done when: STA test renders a tile for each built-in and asserts non-empty pixels and cache reuse on a second call; applying a tile to a selection is one undo step (test); keyboard navigation works (manual).

- [ ] **U24 Annotation snapping and spacing guides.** Deps: U08. Req: R-E5.1–R-E5.4, D5.
  Files: `Core/Layout/SpacingGuides.cs` (+ Core tests), `Core/Layout/SnapEngine.cs` (if needed), `Editor/CanvasView.cs` (`:752` snapping limited to images).
  Done when: Core tests for equal-gap detection (±1 px, both axes, no false match with 2 objects); annotations snap; Alt disables (manual).

- [ ] **U25 Settings window rebuild.** Deps: U19, U06, U03. Req: R-E9.1–R-E9.5, R-E9.7.
  Files: `Shell/SettingsWindow.cs` (split into `Shell/Settings/*.cs` page classes).
  Done when: every `AppSettings` field except `Version` and the `Last*Directory` fields is editable; Cancel reverts a previewed theme; search filters labels (STA test).

- [ ] **U26 Capture preset editor.** Deps: U25. Req: R-E9.6.
  Files: `Shell/Settings/CapturePresetsPage.cs`, `Storage/Settings/PresetStores.cs` (reorder helper only).
  Done when: add/duplicate/rename/delete/reorder persist (Windows test on the store); file-name example updates live; preset hotkeys re-register after Save (manual).

- [ ] **U27 Command palette.** Deps: U10, U06. Req: R-E15.2, R-E15.3, M2, D3.
  Files: `Core/Search/FuzzyMatcher.cs` (+ Core tests), `Shell/CommandPalette.cs`, `MainWindow.xaml.cs` (Ctrl+K).
  Done when: fuzzy tests (prefix beats subsequence, case-insensitive, keyword match, empty query); the M2 coverage test passes; recent commands first.

### Milestone 7: capture

- [ ] **U28 Overlay Adjust phase and keyboard precision.** Deps: U03. Req: R-E6.1, R-E6.4, D7.
  Files: `Core/Capture/RegionSelection.cs` (+ `RegionSelectionTests.cs`), `Capture/RegionOverlayWindow.cs`, `Capture/CaptureCoordinator.cs`.
  Do: `Adjusting` state, handles, move, edge keys, Esc semantics, `CaptureOnRelease` setting. Overlay colours from tokens.
  Done when: Core tests for each handle, clamping to virtual desktop bounds, aspect lock in adjust, Esc twice cancels, keyboard edge moves; manual on one monitor at 100% and 150%.

- [ ] **U29 Action bar.** Deps: U28, U18. Req: R-E6.2, D1, D8.
  Done when: each button produces the same result as the matching destination/pin/save path (manual); bar placement flips at screen edges (pure placement function + Core test); the bar is not in the captured pixels (manual).

- [ ] **U30 Loupe, mode strip, hints, shapes, window labels, countdown.** Deps: U28. Req: R-E6.3, R-E6.5–R-E6.8, R-E6.10, R-E13.6, D2.
  Done when: loupe placement pure function tested; `C` copies the correct hex from a known fixture snapshot (STA test using the overlay's sampling helper); ellipse hole visible (manual); number keys switch mode (manual).

- [ ] **U31 In-overlay size/aspect picker.** Deps: U28. Req: R-E6.9.
  Done when: the two text prompts are removed; picking 1280×720 captures exactly 1280×720 physical pixels (manual, and an existing fixed-size test still passes).

- [ ] **U32 Desktop capture toast.** Deps: U18, U29. Req: R-E7.2, N5.
  Files: `Shell/DesktopToastWindow.cs`, `MainWindow.xaml.cs` (`RunCaptureAsync` and presets routing, `:1367-1421`).
  Done when: Copy-only capture with the editor hidden shows the toast without taking focus (manual: typing in Notepad continues); balloons remain only for hotkey problems.

### Milestone 8: secondary windows and polish

- [ ] **U33 Library window.** Deps: U19, U06, U03. Req: R-E10.1–R-E10.4, R-E10.6.
  Done when: search/sort/filter logic is a pure function with tests; keyboard shortcuts work (manual); pin badge is an icon.

- [ ] **U34 Recent-captures strip.** Deps: U33. Req: R-E10.5.

- [ ] **U35 Pinned window.** Deps: U04, U02. Req: R-E11.1–R-E11.4.
  Done when: click-through toggles on and off from the tab and tray (manual); zoom range clamps 10–800% (test on the helper); existing pin tests pass.

- [ ] **U36 Tray menu.** Deps: U01, U04, U10. Req: R-E12.1–R-E12.3.
  Files: `SnagItOpen.Windows/Shell/TrayService.cs` (accepts renderer colours and icon bitmaps from App; it must not reference App), `MainWindow.xaml.cs` (`CreateTray`).
  Done when: hotkeys show beside items; the menu rebuilds after hotkey or preset changes; fallback to default rendering on failure.

- [ ] **U37 Start card, recent projects, first run, coach marks.** Deps: U03, U10, U18. Req: R-E14.1–R-E14.4.
  Done when: recent list add/remove/missing tests on `UiState`; first-run InfoBar appears only once (test on the flag logic).

- [ ] **U38 Accessibility sweep.** Deps: all shell tasks (U11–U21). Req: R-E13.3, R-E13.5, R-E13.7, M5.
  Done when: the automation-name tree test passes; 200% text scaling and High contrast walk-throughs recorded in `docs/evidence/ui-accessibility.md`.

- [ ] **U39 Optional polish (only if approved).** Mica backdrop on Windows 11 via `DWMWA_SYSTEMBACKDROP_TYPE`, silently skipped elsewhere; "System accent" for selection.

- [ ] **U40 Final cleanup and docs.** Deps: all. Delete the remaining dead code listed in section 1 (`RelayCommand`/`AsyncCommand` if still unused, `NotConverter`, VM `CycleAnnotation`/`SetLocked`/`ToggleImage`/`CancelImport`, the identity `Mixed` overload); update `README.md` screenshots text, `docs/HANDOFF.md`, and run the full UI/UX acceptance section.

### Dependency order (suggested)

U01 → U02 → U04 → U03 → U05 → U06 → U07 → U08 → U09 → U10 → U11 → U12 → U13 → U15 → U14 → U16 → U18 → U17 → U19 → U20 → U21 → U22 → U23 → U24 → U25 → U26 → U27 → U28 → U29 → U30 → U31 → U32 → U33 → U34 → U35 → U36 → U37 → U38 → U40 (U39 optional).

Stop points for the user to try the app: after U09 (themes and canvas), U14 (new shell), U23 (gallery), U32 (capture), U40 (release).

## 13. Worker prompt template

```text
Implement task Uxx from docs/superpowers/specs/2026-09-30-ui-ux-upgrade-prd.md only.
First read docs/HANDOFF.md in full, then PRD sections 0, 4, 5 and the requirements the task cites,
then every file the task lists. Check git status.
Before editing, state inputs, outputs, invariants and the tests you will add.
Write the tests first where the task names them. Keep Core free of WPF/Win32.
Do not add packages. Do not change the .sio schema. Do not reverse HANDOFF section 4 decisions.
Run the gate (build, both test suites) in a separate step after the edits.
Report: files changed, commands and exact results, test counts, manual checks still open.
Commit locally as "UI: <what changed>", tick the task in PRD section 12, update HANDOFF section 5.
```
