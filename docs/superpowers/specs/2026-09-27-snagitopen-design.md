# SnagItOpen product and architecture specification

Date: 2026-09-27. Status: recommended design for implementation planning, not a claim of a built or tested product.

## 1. Confirmed requirements and assumptions

Confirmed: Windows 11; local Snagit-like capture and editor; **quick vertical/horizontal combining plus free placement on a canvas** takes priority; implementation will be delegated in small pieces to smaller LLMs. The user subsequently confirmed that both capture and editor need broad Snagit-like capabilities. The [capability matrix](../../CAPABILITIES.md) is the full feature boundary; early releases are incremental subsets.

Planning assumptions: personal desktop use, Windows x64 initially, mouse and keyboard, offline at runtime, one document per editor window and one editor window initially. UI language English. Source imports PNG/JPEG/BMP; output PNG/JPEG and an editable project. No account, network service, automatic uploading, or runtime LLM needed. Windows ARM64 packaging is a separate future target. These assumptions can be changed before their affected task starts.

Success scenario: paste or drop three screenshots, click Vertical, reorder one thumbnail, crop one edge, set gap to 12 pixels, copy the result; reopen an editable project later and move the screenshots independently.

## 2. Release scope and requirements

| ID | Requirement | Release |
|---|---|---|
| F01 | Open/drop ordered image files and paste an image | A |
| F02 | Vertical/horizontal combine, reorder, gap, padding, alignment | A |
| F03 | Keep original size or match width/height while preserving aspect ratio | A |
| F04 | PNG export and clipboard copy with a useful preview | A |
| F05 | Free placement, selection, move, resize, layer order, snapping | B |
| F06 | Per-image nondestructive crop, canvas fit/crop, undo/redo | B |
| F07 | Editable local projects and reusable layout presets | B |
| F08 | Region, monitor, all monitors, visible window, repeat region | C |
| F09 | Delayed capture, cursor option, hotkeys, tray menu | C |
| F10 | Capture directly into the current composition | C |
| F11 | Arrow, rectangle, text, highlight, numbered steps, solid redaction | D |
| F12 | JPEG export, recent captures, crash recovery, portable packaging | D |
| F13 | Manual overlap join and assisted vertical scrolling | E |
| F14 | Fixed-size/aspect, ellipse/freehand, timed menu capture | F |
| F15 | Interval capture and multi-region sessions | F |
| F16 | Rotate/flip/resize, strip cut-out, callouts, freehand, ellipse | F |
| F17 | Blur/pixelate, magnify, stamps, borders/shadows/edge effects | F |
| F18 | Capture/tool presets, pin-to-screen, quick output | G |
| F19 | Local OCR feasibility and optional integration | G, optional |

Out of scope: Snagit project compatibility, cloned branding, video/audio recording, animated GIF editing, automatic text replacement, cloud sync, photograph panoramas, universal full-page capture of arbitrary apps, arbitrary-angle rotation, and full Photoshop-style blend modes. Quarter-turn rotation and flips are required in F; local OCR is an optional feasibility-gated extension.

## 3. Editor workflow

The editor has a conventional command bar, image list at left, central canvas, context properties at right, and status bar. Main commands: Capture, Import, Paste, Vertical, Horizontal, Free, Undo, Redo, Copy image, Export, Save project. Use system font and native focus/menu behavior. The empty canvas says “Drop images here or paste a screenshot” with an Import button. Advanced settings belong in the context panel.

The functional layout is specified here; branding, custom themes, and polished visual styling are a later design task. Keep standard Windows controls until the core workflow passes acceptance.

### Quick combine

1. Import multiple files in file-picker order. Explorer drops preserve the order received; show numbers so users can correct it. Do not silently sort filenames.
2. Newly imported images form one ordered image group. Choose Vertical or Horizontal once; additional images append to that group using the active layout.
3. Default gap 0, outer padding 0, start alignment, original pixel sizes, transparent canvas background. Provide white and custom background choices.
4. Offer Match width in Vertical or Match height in Horizontal. Default target is the largest relevant cropped dimension. Upscaling is off; show smaller images at their capped size with selected alignment. A checkbox enables enlargement.
5. Drag a thumbnail or use Move up/down to reorder. Crop in the context panel or with handles. Preview updates without committing dozens of undo steps.
6. Copy image is a primary command. Export prompts only for destination/format and relevant options. Save project is separate and explicitly editable.

### Free canvas

- Switching to Free preserves every image's current rectangle. It disables automatic relayout.
- Drag to move; corner handles resize with aspect ratio locked by default. Shift temporarily unlocks it. Integer document-pixel positions/sizes commit on pointer release.
- Ctrl-click multi-selects; drag moves a selected group. Delete removes selected layers; Ctrl+D duplicates with a 16-pixel offset. Copy image copies the whole export area, not a private layer format.
- Arrow keys nudge one document pixel; Shift+arrow nudges ten. Inputs offer exact X, Y, width, height.
- Snap edges/centers to other images and canvas edges within 6 screen DIPs. Alt temporarily disables snapping. Zoom must not change the perceived snap tolerance.
- Fit canvas adds optional padding around the union of visible image and annotation bounds. It does not move the layers. Negative document coordinates are valid.
- Return to Vertical/Horizontal deliberately rearranges the image group and is one undoable command. Layer-linked annotations follow their image; document annotations retain their document positions.

### Capture-to-append

From the current document choose Capture > Append below, Append right, or Add to free canvas. The editor hides while selecting. A canceled capture restores the editor and changes nothing. A completed capture becomes a new asset and image layer in one undoable action. In a layout group it appends to the order and reflows; in Free it is placed next to the current selection or at the viewport center. No extra file-save dialog intervenes.

### Keyboard behavior

Use standard Ctrl+O import/open chooser, Ctrl+V paste, Ctrl+S save project, Ctrl+Shift+S save project as, Ctrl+Z undo, Ctrl+Y redo, Ctrl+Shift+C copy flattened image, Ctrl+E export, Delete, and Escape cancel tool. Space+drag pans. Ctrl+wheel zooms 10%–800% around pointer. Shortcuts must not intercept typing inside text fields. Proposed global hotkeys are configurable suggestions, registered only if available: Ctrl+Shift+1 region, Ctrl+Shift+2 visible window, Ctrl+Shift+3 append region.

## 4. Architecture and project boundaries

```text
SnagItOpen.App (WPF, composition root, view models)
    |-- SnagItOpen.Core (plain .NET, document, geometry, commands)
    |-- SnagItOpen.Imaging (WPF codecs/render/export, references Core)
    |-- SnagItOpen.Windows (Win32 capture/clipboard/hotkeys, references Core)
    `-- SnagItOpen.Storage (JSON/ZIP/assets/settings, references Core)

Core.Tests (no Windows dependency)
Windows.Tests (STA imaging/storage/native integration on Windows)
```

Core targets `net10.0`; App, Imaging, Windows and Windows.Tests target `net10.0-windows` (App/Imaging/tests enable WPF); Storage targets `net10.0`. Windows enables WPF for clipboard and Windows Forms only for the tray icon wrapper. Qualify conflicting type names rather than importing both UI namespaces broadly. No circular project references. App owns wiring, storage never calls UI, capture never changes the document directly.

Start with manual constructor injection and small `INotifyPropertyChanged` helpers. A DI framework, event bus, repository abstraction per entity, and generic plugin architecture are unnecessary.

### Core contracts to freeze in T02

Each type below gets its own file under `Core/Geometry` or `Core/Documents`. Names in later tasks refer to these contracts. Use namespace `SnagItOpen.Core` subnamespaces matching folders.

```csharp
public readonly record struct PixelSize(int Width, int Height);
public readonly record struct PixelPoint(int X, int Y);
public readonly record struct PixelRect(int X, int Y, int Width, int Height);
public readonly record struct Rgba32(byte R, byte G, byte B, byte A);
public enum LayoutMode { Vertical, Horizontal, Free }
public enum CrossAlignment { Start, Center, End }
public enum ScaleMode { Original, MatchCrossAxis }
public sealed record LayoutOptions(LayoutMode Mode, int Gap, int Padding,
    CrossAlignment Alignment, ScaleMode Scale, int? TargetCrossPixels,
    bool AllowUpscale);
public sealed record ImageAsset(string Id, int Width, int Height,
    string RelativePngPath);
public sealed record ImageLayer(Guid Id, string AssetId, PixelRect SourceCrop,
    PixelRect Bounds, bool Visible, int QuarterTurns = 0,
    bool FlipHorizontal = false, bool FlipVertical = false);
public sealed record DocumentState(int SchemaVersion, Guid Id, string Name,
    PixelRect ExportArea, Rgba32 Background, ImageAsset[] Assets,
    ImageLayer[] Images, Guid[] LayoutOrder, LayoutOptions Layout,
    Annotation[] Annotations);
```

Arrays are serialized snapshots and treated as immutable: reducers return fresh arrays, never edit a prior state's arrays. `Images` order is bottom-to-top drawing order; `LayoutOrder` is independent and contains every image ID once. Invisible images remain in the document but are excluded from layout and export union. SourceCrop is an integer rectangle in the normalized source image, never a destination-space crop. Images are not destructively resampled when moved or resized. Orientation fields are reserved from the beginning: QuarterTurns is 0–3 clockwise. Transform order is crop, flip in cropped source axes, quarter-turn, scale into Bounds, translate. Odd quarter-turns swap effective crop width and height for layout. The initial UI creates only identity orientations; T39 enables them and extends transform tests.

`Annotation` is an explicit JSON-polymorphic hierarchy introduced in T02 as base + reserved type discriminators, with derived records implemented in T27/T28. Initial base contract: `Id`, optional `ImageLayerId`, `Bounds`, `Color`. Types: `Rectangle`, `Arrow`, `Text`, `Highlight`, `Step`, `Redaction`. Layer-linked annotation coordinates are normalized source-image pixels (before crop/scale); document-linked coordinates are document pixels. Linked annotations are clipped to the image crop, transformed with it, and rendered just above their image. Document annotations render above all images. Redactions render last in their applicable coordinate space. T27 adds shape-specific properties using discriminators, not runtime type names. Document v1 permits an empty annotation array before T27.

### Service contracts

| Contract | Inputs → outputs | Owner |
|---|---|---|
| `LayoutEngine.Arrange` | ordered visible ImageLayer values + LayoutOptions → new bounds and ExportArea | Core |
| `DocumentReducer.Apply` | DocumentState + EditorAction → validated new DocumentState | Core |
| `History.Execute/Undo/Redo` | immutable states, command description → current state | Core |
| `IAssetStore.PutAsync/OpenReadAsync` | normalized PNG stream / asset ID → descriptor / fresh readable stream | Core interface, Storage implementation |
| `IImageImporter.ImportAsync` | input stream + name + cancellation → normalized image descriptor and stored PNG | Imaging, uses IAssetStore |
| `DocumentRenderer.Draw` | immutable state + resolved bitmap assets + DrawingContext → draw commands | Imaging |
| `IExportService.ExportAsync` | state + format/background options + path + cancellation → success/failure | Core contract, Imaging implementation |
| `ICaptureService.CaptureAsync` | CaptureRequest + cancellation → CaptureResult with encoded PNG or typed failure | Core contract, Windows implementation |
| `IProjectStore.SaveAsync/LoadAsync` | document + asset streams / project path → persisted project / validated state | Core contract, Storage implementation |

Service result records use explicit success, canceled, and failure alternatives. Failure codes: UnsupportedFormat, InvalidImage, ImageTooLarge, InvalidProject, UnsupportedSchema, AccessDenied, DiskFull, ClipboardBusy, HotkeyUnavailable, TargetGone, CaptureUnavailable. Preserve original exceptions in local diagnostics without displaying stack traces or pixel data. Unexpected exceptions remain faults, not false successes.

## 5. Coordinate and layout rules

Three spaces must have separate conversion functions: physical desktop pixels (negative monitor origins allowed), document pixels (asset and export geometry), and WPF DIPs (window and pointer layout). Never store zoom, DPI, or HWND positions in image bounds.

Let visible ordered cropped images have source sizes `(wi, hi)`. Vertical layout's cross dimension is width. Horizontal swaps width/height and x/y. Validate gap 0–512, padding 0–2048, positive target dimension, and total dimensions before allocation.

```text
if scale == Original: si = 1
else:
    target = explicit target or max(source cross dimensions)
    si = target / sourceCross_i
    if !allowUpscale: si = min(si, 1)
destCross_i = max(1, RoundAwayFromZero(sourceCross_i * si))
destMain_i  = max(1, RoundAwayFromZero(sourceMain_i * si))
C = max(destCross_i)
alignOffset = 0 (Start), floor((C-destCross_i)/2) (Center),
              C-destCross_i (End)
cross_i = padding + alignOffset
main_i = padding + sum(previous destMain) + gap * i
exportCross = 2*padding + C
exportMain = 2*padding + sum(destMain) + gap*(count-1)
```

Empty document: 1×1 transparent export area, export/copy disabled. One image has no gaps. All arithmetic uses checked 64-bit intermediates before int conversion. Example: 100×50 and 80×30, Vertical, gap 10, padding 5, Original, Center → output 110×100, placements `(5,5,100,50)` and `(15,65,80,30)`. Match width 100 with upscaling true makes image 2 be 100×38; output 110×108.

Free-mode crop preserves scale and the world position of surviving pixels: if old crop starts at `(cx,cy)` and new crop starts at `(nx,ny)`, shift destination origin by `(nx-cx)*sx, (ny-cy)*sy` and resize by new cropped dimensions times existing scale, rounded once. Crop changes in an automatic layout trigger full layout. Fit canvas uses floor(min bounds) and ceil(max bounds) plus padding. Export translates by negative ExportArea origin, so off-origin compositions export correctly.

Overlap is not a negative gap in A/B. In E, a pairwise seam removes specified repeated rows/columns from the second image; images remain independently editable.

## 6. Imaging, rendering, and memory

Import validates header dimensions before full decode, applies EXIF orientation once, normalizes color to sRGB when a valid embedded profile is available, and stores normalized PNG. Invalid profiles produce a clear warning and documented sRGB fallback; original input files stay unchanged. Assets identify normalized PNG content with SHA-256. Crop, scale, and layer changes reference assets, so undo never duplicates full bitmaps.

One scene renderer draws images, linked annotations, document annotations, and applicable redactions. Preview and export call it. Selection handles, checkerboard, snap lines, and crop handles are editor adorners and never part of export. Export uses 96-DPI document rendering into Pbgra32 so width/height are exact pixel dimensions. PNG preserves alpha. JPEG flattens onto the selected opaque background and strips source metadata. Clipboard offers bitmap plus PNG data when supported by the receiving app; disclose that transparency support varies by destination.

Use an STA dispatcher worker for decode/render/export. Background work receives immutable document snapshots, not live controls. Freeze resources when supported before crossing thread boundaries. Cancel between stages and before file replacement; a native encoding call may finish before cancellation is observed.

Initial guardrails, adjustable after profiling:

- Maximum decoded individual asset: 40 megapixels; maximum dimension 32,767 pixels.
- Maximum export: 40 megapixels and 32,767 pixels on either axis.
- Maximum assets/document: 100; maximum decoded cache: 256 MiB LRU.
- Maximum estimated active image working set: 1 GiB (sources needed + render target + scratch + encoding reserve). Preflight export conservatively with at least two output buffers and a 128 MiB reserve.
- Maximum undo entries: 100; history stores metadata, not raster copies.
- Use thumbnails up to 512 px longest side in the image list. Preview can use smaller proxies, but export always resolves full-resolution sources.
- Reject over-limit work before allocation with exact dimensions and an action to resize/split. Limits apply to scrolling sessions too.

These budgets do not guarantee every WPF render allocation will succeed. Catch allocation/render failure, keep the editable document, and suggest a smaller export. Do not silently downsample. T04/T07 contain a large-image compatibility check before canvas features expand.

## 7. Capture design

CaptureRequest contains mode (Region, Monitor, AllMonitors, VisibleWindow, LastRegion), optional physical rectangle/monitor ID/HWND, delay, and IncludeCursor. CaptureResult contains a PNG stream/bytes, physical bounds, and metadata. It does not contain WPF controls.

Sequence: prevent a second concurrent capture; record foreground target; hide app-owned windows; await their compositor update; honor countdown; capture the unobscured desktop into a frozen backing image; show per-monitor selection overlays using that image; collect physical-pixel rectangle; close overlays; crop the frozen image; optionally composite captured cursor using hotspot/physical coordinates; return result; restore editor in `finally`. The cursor snapshot belongs to the same acquisition moment as the pixels. Delay occurs before the freeze. Cursor is excluded by default.

An overlay per monitor prevents one giant WPF window from imposing a single DPI on mixed-scale monitors. Global pointer geometry is in physical screen coordinates; selection highlights are clipped and converted for each overlay. Keyboard Escape cancels. Arrow-key selection adjustment is exposed for precision. Regions can cross displays; blank gaps between displays become transparent pixels. AllMonitors returns the bounding union, preserving physical arrangement.

VisibleWindow enumerates eligible top-level windows, excludes this process's own overlays, filters invisible/cloaked/minimized targets, uses DWM extended frame bounds where available, clips to available desktop, and crops the visible snapshot. Occlusion remains in the result; the menu label and help must say so. Do not silently substitute PrintWindow or claim capture of offscreen content.

LastRegion is stored in physical coordinates plus monitor topology fingerprint. If topology changes, ask for reselection in the capture UI; do not capture a stale region without telling the user. Display changes mid-selection cancel and refresh topology.

GDI ownership must be explicit: restore selected object, delete bitmap/DC, release screen DC in all paths. Convert its opaque screen pixels correctly rather than trusting undefined alpha. Protected/black capture is not reliably detectable from pixels alone; an all-black result can be legitimate and must remain inspectable. No attempts to bypass secure desktop or protection.

Windows.Graphics.Capture is a post-release-D upgrade gate, not a hidden dependency. A new backend must satisfy the same service contract and demonstrate better window/accelerated-app capture on real devices before replacing GDI.

## 8. Persistence and recovery

Editable `.sio` project = ZIP containing `manifest.json`, normalized `assets/<sha256>.png`, and optional `preview.png`. Manifest includes schemaVersion 1 and all editable state, including source crop rectangles. No absolute source paths required. Project save does not change source images. Flattened PNG/JPEG does not include hidden source pixels or undo metadata; editable projects do contain original normalized images, including pixels hidden by crop/redaction.

Write to a temporary sibling file, validate/close/flush, then replace destination atomically where supported. On first save use rename; retain previous file if any stage fails. Open validates schema, enum ranges, dimensions, bounds, asset hashes, unique IDs, all references, finite/valid geometry, and missing assets before switching the active document. Reject newer schemas with a readable message. ZIP limits: 102 entries, 256 MiB per entry, 1 GiB total expanded bytes, no rooted/traversal names or external extraction. Stream the allowed entries, do not extract arbitrary paths. A preview is optional; T18 defines the exact entry-count accounting so 100 assets plus manifest and preview is valid.

Runtime data lives under `%LOCALAPPDATA%\SnagItOpen\`: settings.json, history/index.json, history/assets/, recovery/, cache/, logs/. History is capped at 200 captures or 500 MiB (whichever first); pinned entries are exempt from automatic deletion, with visible storage usage. Cache and history are distinct from project assets. Do not delete assets referenced by an open document, undo history, saved project in progress, or recovery snapshot.

Autosave after a two-second quiet period, no more often than every ten seconds; use monotonically increasing document revision numbers so an older completion cannot replace a newer snapshot. On startup offer to recover or discard found drafts. Saving explicitly updates the baseline and removes only obsolete recovery snapshots after successful save. No database needed initially.

## 9. Error states and usability

Bad file import reports filename and cause, keeps previously imported successes, and reports how many succeeded. A failed export/save never marks a document saved. Clipboard retry is bounded (e.g. five attempts over at most one second) on STA, then reports “Clipboard is busy. Try Copy again.” Hotkey conflict preserves the old working binding until a replacement registers. Capture errors restore the editor and keep the composition intact.

Use visible focus indicators, labels/tooltips for icons, accessible names on commands, keyboard reorder and numeric geometry fields, and text plus icon for statuses. Do not rely on color alone. Respect system high contrast and text scaling; an editor cannot be accepted based on pointer-only use. At small window sizes the properties panel collapses; the canvas remains reachable. Confirmation is reserved for unsaved document loss or overwriting an existing file.

## 10. Quality targets and completion gates

Targets, to measure on a documented Windows 11 reference machine: ordinary 3-image 1080p combine preview within 300 ms after decode, interactive drag/zoom p95 frame time below 33 ms for ten 1080p layers using proxies, export ten 1080p images within five seconds, cancellation visibly acknowledged within 250 ms except noninterruptible native stages. These are tuning goals, not existing measurements or universal hardware promises.

Release A must be useful without capture: import → combine → reorder → copy/export. Release B must save/reopen an editable composition with crop, order, and geometry intact. Release C must pass mixed-DPI capture checks. Release D must pass crash recovery, resource cleanup, and redaction-export checks. Release E must preserve manual correction when automatic matching is uncertain. Release F supplies the broader capture/editor toolset; release G integrates workflow shortcuts and verifies the complete required matrix.

## 11. Expanded editor and capture behavior

Capture shape masks are applied after the rectangular physical-pixel acquisition. Ellipse/freehand output uses transparent pixels outside the mask. Fixed-size capture uses physical pixel sizes; fixed-aspect constrains drag geometry. Delayed menu capture freezes only after countdown; the user opens the menu during the delay. Interval capture is an explicitly started session with a visible Stop command, 1–60 second interval, and a default maximum of 20 frames. Multi-region capture collects several regions from one frozen snapshot in selection order and commits one batch. Session limits and memory guards always apply.

Quarter-turn/flip changes affect the image and linked annotations through the common transform; they do not mutate original pixels. Resize document is an explicit command with either “scale content and canvas” or “canvas size only”; export scaling is a separate output option. Cut out supports a selected image or a flattened duplicate of the composition: choose a horizontal/vertical strip, preview, then create a derived image asset with the remaining portions joined. The original document/layer remains recoverable by undo. Do not attempt a piecewise remapping of every annotation in the first cut-out implementation: offer a visibly labeled “Create flattened copy and cut out” when annotations are involved.

Blur/pixelate act on selected rectangular regions of an image. They are editable effect records in source coordinates; preview/export share the effect processor. Blur/pixelate are visual effects and must not be represented as secure redaction. Opaque redaction uses a full-opacity fill and a flattened output. Editable projects retain source content. Magnify displays a cropped source region at a chosen scale with an optional border; dragging the lens changes its source anchor. Stamps are bundled vector/simple raster symbols and user-imported assets, never remote downloads. Numbered steps are editable labels with an explicit renumber action rather than silently renumbering on every deletion.

Effects are per-image or document-edge settings, distinct from source pixels. Border/shadow/rounded/torn edges must have explicit bounds expansion included by Fit canvas and export. Store a deterministic seed for torn-edge geometry so reopening/exporting cannot change it. Presets serialize validated settings without source images. UI exposes the selected tool's controls and remembers its last used style, keeping the main canvas uncluttered.

See [acceptance checklist](../../ACCEPTANCE.md) and [implementation plan](../plans/2026-09-27-snagitopen.md) for concrete verification and task mapping.
