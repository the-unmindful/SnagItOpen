# SnagItOpen Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` when available to implement this plan task by task. If the user explicitly chooses delegated execution, use `superpowers:subagent-driven-development`. Steps use checkbox syntax for tracking. These skills are optional environment tooling for another model; all project requirements are specified in these documents.

**Goal:** Build a local Windows 11 application with Snagit-like screen capture and image editing, prioritizing quick image combination and an editable free canvas.

**Architecture:** Plain .NET owns documents, geometry, and command history. WPF owns UI and image rendering; isolated adapters own Windows capture and local persistence. Import and capture converge on the same image-asset/document pipeline.

**Tech Stack:** C#, .NET 10 LTS, WPF, Win32 interop, WPF imaging, System.Text.Json, System.IO.Compression, xUnit, PowerShell.

---

## How to execute

Read the [specification](../specs/2026-09-27-snagitopen-design.md), [capability matrix](../../CAPABILITIES.md), and [small-model guide](../../SMALL_MODEL_GUIDE.md) before starting. All paths below are relative to `E:\Misc\SnagItOpen`. Every named source/test file is to be created unless its task says modify. Do not implement multiple tasks in one model run. A task that exceeds a model's context or four focused hours should be split at its numbered substeps; keep the same contracts.

This is a design-to-implementation plan, not prewritten application source. Code fragments specify contracts or test oracles; the implementing model must produce working code and report actual verification. Native API work is explicitly a hardware/Windows integration task, not something a unit test alone can establish.

For each task: inspect current files → add the stated behavioral checks → implement only owned responsibilities → run targeted checks and build → review diff → update task status. Commit only after checks pass and Git has been initialized. Choose package patch versions in T01, record them once, and do not independently upgrade packages in later tasks.

### Verification commands

Use PowerShell from the repository root. T01 creates these projects and a `SnagItOpen.slnx` solution.

```powershell
# V0: compile the complete current solution
dotnet build .\SnagItOpen.slnx -c Debug --no-restore

# V1: deterministic domain checks; add --filter FullyQualifiedName~ClassName
dotnet test .\tests\SnagItOpen.Core.Tests\SnagItOpen.Core.Tests.csproj -c Debug --no-restore

# V2: Windows/STA imaging and persistence checks
dotnet test .\tests\SnagItOpen.Windows.Tests\SnagItOpen.Windows.Tests.csproj -c Debug --no-restore

# V3: interactive application
dotnet run --project .\src\SnagItOpen.App\SnagItOpen.App.csproj
```

Expected V0: exit 0, zero build errors. Expected V1/V2: exit 0, no failed tests; hardware-dependent checks must be separately identified, never silently skipped then claimed passed. Run targeted test classes while iterating, full suites at each release gate. UI-only layout changes need the corresponding manual check, not tests that simply mirror XAML.

### Dependency graph and checkpoints

```text
T01 → T02 → T03 → T04
              ├→ T05 → T06 → T07 → T08
              └──────────→ T09 → T10 → T11        [A: quick combiner]
T11 → T12 → T13 → T14 → T15 → T16 → T17
                       └──────→ T18 → T19 → T20  [B: editable composition]
T20 → T21 → T22 → T23 → T24 → T25 → T26          [C: capture]
T26 → T27 → T28 → T29 → T30 → T31 → T32 → T33    [D: daily-use baseline]
T33 → T34 → T35 → T36                            [E: scrolling]
T36 → T37 → T38 → T39 → T40 → T41 → T42 → T43 → T44 [F: broader toolset]
T44 → T45(optional) → T46(evaluation) → T47 → T48 [G: integrated product]
```

The graph shows the recommended serial order for smaller models. Tasks list minimum functional dependencies; serial execution avoids merge and shared-schema conflicts. T09 additionally requires T08 before wiring copy; T18 requires T17 for complete crop/canvas persistence; T20 waits for all B tasks. T45 can be explicitly deferred without blocking T46–T48. T46 may retain GDI with documented limits after the evaluation.

### Planned file organization

```text
src/
  SnagItOpen.Core/
    Geometry/       pixel rectangles, transforms, bounds
    Documents/      assets, layers, document, annotations, validation
    Layout/         stacking, presets, snap calculations
    Editing/        actions, reducer, history, selection calculations
    Capture/        requests/results and sessions
    Contracts/      storage/import/export/capture interfaces
    Stitching/      overlap geometry and matching
  SnagItOpen.Imaging/
    Import/         decode, orientation, normalization
    Rendering/      single scene renderer and asset cache
    Export/         PNG/JPEG output
    Effects/        cut-out, blur, pixelate, edge effects
    Threading/      STA dispatcher lifetime
  SnagItOpen.Windows/
    Native/         narrowly scoped P/Invoke and safe handles
    Capture/        screen pixels and monitor/window adapters
    Clipboard/     bounded STA copy/paste
    Shell/         hotkeys, tray, single-instance activation
  SnagItOpen.Storage/
    Assets/         normalized asset files
    Projects/       ZIP manifests and validation
    Settings/      validated preferences/presets
    Recovery/      versioned autosave snapshots
    History/       recent-capture metadata/retention
  SnagItOpen.App/
    Capture/        overlay and countdown windows
    Editor/         view, view model, tools, input controller
    Library/        recent image tray
    Settings/       settings view
    Shell/         main window, composition root
tests/
  SnagItOpen.Core.Tests/
  SnagItOpen.Windows.Tests/
  Fixtures/         generated tiny images and documented manual fixtures
scripts/           build/test/package commands
docs/              this planning pack and implementation evidence
```

## Release A: quick image combiner

### T01 — Reproducible Windows solution

**Dependencies:** none. **Ownership:** solution, project files, `global.json`, `Directory.Build.props`, `.gitignore`, `scripts/verify.ps1`, `docs/BUILD.md`.

- [ ] Run `dotnet --list-sdks`. If no 10.x SDK exists, install the supported .NET 10 SDK using Microsoft's installer, then rerun. Record exact SDK/version and OS architecture. Do not confuse a runtime with an SDK.
- [ ] Create the five source projects and two test projects using the target frameworks in the spec. Add references only in the documented direction. Keep nullable references and implicit usings enabled; warnings are reviewed before treating all as errors.
- [ ] Use `dotnet new sln -n SnagItOpen --format slnx`; `dotnet new wpf -n SnagItOpen.App -o src/SnagItOpen.App --framework net10.0`; create class libraries and xUnit projects in their declared paths, then set Windows TFMs where needed. Add projects with `dotnet sln .\SnagItOpen.slnx add <explicit-project-path>`.
- [ ] Pin the actual SDK in global.json with latestPatch roll-forward; pin direct package versions and enable package lock files. Restore once, build, then run an empty WPF shell. Remove generated placeholder class/tests.
- [ ] Add build instructions and ignore bin/obj/user caches. Initialize Git if desired for checkpoint commits; do not overwrite an existing repository.

**Gate:** V0 and V3 work on Windows; V1/V2 test projects are discoverable. Evidence records actual versions, not guessed versions. No application features belong here.

### T02 — Document contracts, geometry, and validation

**Dependencies:** T01. **Files:** `Core/Geometry/{PixelSize,PixelPoint,PixelRect,Rgba32}.cs`, `Core/Documents/{ImageAsset,ImageLayer,DocumentState,Annotation,DocumentValidator}.cs`, `Core/Layout/LayoutOptions.cs`, `Core.Tests/DocumentValidationTests.cs` (project prefixes follow the tree above).

- [ ] Implement the records/enums in spec §4. Annotation is an abstract base with reserved discriminators; empty arrays work before derived tools exist. Add half-open rectangle operations: right/bottom excluded, intersection, union, translation.
- [ ] Implement validation for positive sizes, checked arithmetic, unique IDs, asset references, crop within source, LayoutOrder membership, orientation 0–3, and configured dimension limits. Empty document factory yields 1×1 transparent area.
- [ ] Tests: crop `(-1,0,10,10)` fails; crop `(90,0,11,10)` on 100px width fails; duplicate layer IDs fail; unknown asset fails; negative destination X is valid; coordinate sum overflow fails; two touching half-open rectangles have empty intersection.

**Gate:** V1 filtered to `DocumentValidationTests`; V0. Do not put BitmapSource or UI types into Core.

### T03 — Normalized asset storage and fixture generator

**Dependencies:** T02. **Files:** `Core/Contracts/IAssetStore.cs`, `Storage/Assets/FileAssetStore.cs`, `Windows.Tests/AssetStoreTests.cs`, `Windows.Tests/Fixtures/ImageFixtureFactory.cs`.

- [ ] Define async put/open contracts with cancellation. Compute SHA-256 while writing a temporary file, then rename to the hash-based PNG path. The importer supplies verified dimensions; the store enforces normalized asset metadata consistency.
- [ ] Return a fresh readable stream on each read; dispose streams explicitly. Same bytes deduplicate. Existing hash files are never overwritten with differing data.
- [ ] Generate fixtures in tests: red 100×50, blue 80×30, 2×2 asymmetric corner colors, transparent checker, JPEG with orientation, corrupt bytes. Generated binary fixtures must have documented dimensions.
- [ ] Test duplicate import stores one asset, canceled write leaves no reachable partial asset, missing asset returns a typed failure, and independent reads work after the original input stream closes.

**Gate:** V2 `AssetStoreTests`; V0. Production paths are under the application data folder, tests use unique temporary folders.

### T04 — Image importer and STA imaging worker

**Dependencies:** T03. **Files:** `Core/Contracts/IImageImporter.cs`, `Imaging/Threading/ImagingDispatcher.cs`, `Imaging/Import/ImageImporter.cs`, `Windows.Tests/{ImageImportTests,ImagingDispatcherTests}.cs`.

- [ ] Create one STA thread with Dispatcher, async job queue, exception propagation, cancellation, and shutdown. Do not create one STA thread per image.
- [ ] Inspect file headers/dimensions, decode PNG/JPEG/BMP with OnLoad semantics, normalize orientation and supported profile to sRGB, encode normalized PNG, then store. Enforce 40MP/dimension guards before full decode. Release input files immediately after import.
- [ ] Verify all eight EXIF orientations using asymmetric fixtures; verify alpha preservation, corrupt/unsupported files, absent/invalid profile behavior, and cancellation before decode/store. Explicitly return diagnostics for profile fallback.
- [ ] Add a large-image smoke fixture and document actual memory/decoder behavior. Ensure a faulted import does not kill the dispatcher or block later imports.

**Gate:** V2 import/dispatcher classes; V0. Native decoding may have noninterruptible stages; UI cancellation still returns control without using a canceled result.

### T05 — Deterministic vertical/horizontal layout

**Dependencies:** T02. **Files:** `Core/Layout/{LayoutEngine,LayoutResult}.cs`, `Core.Tests/LayoutEngineTests.cs`.

- [ ] Define `LayoutResult(PixelRect ExportArea, ImageLayer[] Images)` and `Arrange(IReadOnlyList<ImageLayer> ordered, LayoutOptions options)`. SourceCrop plus orientation determines effective dimensions. Reject Free input; it uses its own placement commands.
- [ ] Implement spec §5 formulas with checked 64-bit sums, round-away-from-zero scaling, and stable input order. Return new records; do not mutate input. Invisible layers do not consume gaps.
- [ ] Cover both axes, all three alignments, one/zero images, nonnegative gaps, explicit target, upscale off/on, odd quarter-turn dimensions, and over-limit result.

**Exact oracle:** images 100×50 and 80×30, Vertical/Original/Center/gap10/padding5 → 110×100; second bounds `(15,65,80,30)`. Horizontal with the same settings → 200×60; second bounds `(115,15,80,30)`. Match-width100/upscale true → vertical output 110×108.

```csharp
Assert.Equal(new PixelRect(0, 0, 110, 100), result.ExportArea);
Assert.Equal(new PixelRect(15, 65, 80, 30), result.Images[1].Bounds);
```

**Gate:** V1 `LayoutEngineTests`; V0. These assertions assume `result` is produced using the stated fixture and contract.

### T06 — Shared scene renderer

**Dependencies:** T04, T05. **Files:** `Imaging/Rendering/{DocumentRenderer,BitmapAssetCache}.cs`, `Windows.Tests/RendererTests.cs`.

- [ ] Resolve assets through a bounded LRU cache; source crops and destination rectangles are explicit. Draw in image order; ignore hidden layers. Render background over ExportArea, translate export origin, and clip to export bounds.
- [ ] Build an image drawing primitive whose transform order is the spec's order. Annotation dispatch initially receives an empty array. Keep editor adorners out of this assembly.
- [ ] Test corner-color placement, crop, transparent gaps, draw-order overlap, negative image positions, and nonzero ExportArea origin. Use exact pixels only for unscaled opaque interiors; permit documented antialias tolerance at scaled edges.

**Gate:** V2 `RendererTests`; V0. Render the same two-image document twice and compare decoded pixels, not PNG byte identity.

### T07 — PNG export with bounded allocation

**Dependencies:** T06. **Files:** `Core/Contracts/{IExportService,ExportOptions,ExportResult}.cs`, `Imaging/Export/{ExportService,ExportBudget}.cs`, `Windows.Tests/PngExportTests.cs`.

- [ ] Snapshot document state; preflight dimensions/memory; render at 96 DPI to exact output dimensions on imaging dispatcher; encode PNG to a temporary sibling file and replace only after successful completion.
- [ ] Transparent/opaque backgrounds follow document settings. Report disk access/full disk failures without changing document saved state. Check cancellation before destination replacement.
- [ ] Verify output is decodable after the exporter disposes; 110×100 fixture remains exactly 110×100 at any UI DPI; alpha survives; over-limit output is rejected before rendering; failed save leaves old destination bytes intact.

**Gate:** V2 `PngExportTests`; V0. Manually open the exported PNG in another Windows image viewer.

### T08 — Clipboard image import/export

**Dependencies:** T04, T07. **Files:** `Windows/Clipboard/ClipboardImageService.cs`, `Core/Contracts/IClipboardImageService.cs`, `Windows.Tests/ClipboardRetryTests.cs`.

- [ ] Read clipboard image or image-file drop list; prefer image data when both exist. Reject nonimage text without replacing the document. Route pasted assets through normal import.
- [ ] Copy flattened document using supported bitmap and PNG clipboard representations. Perform clipboard operations on the UI/clipboard STA, separate from export worker ownership. Bound retries to five attempts/one second.
- [ ] Test retry policy with a fake clipboard adapter: busy four times then success, always busy, cancellation. Keep real clipboard tests manual so automated tests do not overwrite the user's clipboard.

**Gate:** V2 `ClipboardRetryTests`; manually paste from Snipping Tool and paste output into Paint plus a second target. Record destination-specific transparency behavior.

### T09 — Editor shell and image import list

**Dependencies:** T04, T06, T08. **Files:** `App/Shell/{MainWindow.xaml,MainWindow.xaml.cs,CompositionRoot.cs}`, `App/Editor/{EditorViewModel,ImageListItemViewModel}.cs`, `App/Editor/EditorView.xaml`.

- [ ] Create conventional toolbars, image list, canvas host, context panel, and status bar. Bind commands through view model; keep pointer-event plumbing in the view.
- [ ] Wire Import, file drop, Paste, remove image, and image selection. Preserve input order. Partial batch failures report successful and failed counts; successful images remain.
- [ ] Empty state and disabled command explanations must be visible. Import progress and cancellation must not freeze window movement.

**Gate:** V0/V3; import 3 files, delete middle, paste fourth, and verify order/status/focus. No automatic screen capture yet.

### T10 — Combine controls and thumbnail reordering

**Dependencies:** T05, T09. **Files:** modify `EditorViewModel.cs`, `EditorView.xaml`; create `App/Editor/{LayoutPanelView.xaml,ImageReorderController.cs}`, `Core/Editing/LayoutActions.cs`, `Core.Tests/LayoutActionTests.cs`.

- [ ] Bind Vertical/Horizontal and gap/padding/alignment/scale/upscale controls. Validate numeric entries inline before applying. Coalesce live slider preview; one committed change per completed edit.
- [ ] Implement thumbnail drag reorder and keyboard Move up/down using image IDs. Layout order is independent of drawing order.
- [ ] Check switch axes twice preserves assets; adding image appends; removing a selected image chooses a nearby selection; a malformed numeric entry leaves last valid preview.

**Gate:** V1 `LayoutActionTests`; manual combine oracle matches T05, including dimensions shown in status bar.

### T11 — Release A export and usability gate

**Dependencies:** T07–T10. **Files:** modify editor command bindings; create `docs/evidence/release-a.md`.

- [ ] Wire Copy image and Export PNG to the same renderer, with empty-document disabled states and visible completion/error feedback.
- [ ] Run the Release A scenario in the acceptance checklist entirely from a fresh launch. Record steps, dimensions, screenshot of editor, exported fixture, build/test results, and rough timings.
- [ ] Run V0, full V1/V2, and keyboard-only import/reorder/export. Fix defects before advancing.

**Deliverable:** usable standalone image combiner. This checkpoint is deliberately earlier than capture/editor breadth.

## Release B: custom canvas and editable projects

### T12 — Reducer and undo/redo transactions

**Dependencies:** T11. **Files:** `Core/Editing/{EditorAction,DocumentReducer,History}.cs`, `Core.Tests/HistoryTests.cs`; modify editor mutations to use reducer.

- [ ] Define actions for add/remove/reorder/layout changes and atomic batches. Reducer validates proposed state before commit. History stores before/after immutable metadata states plus label, with maximum 100 entries.
- [ ] Commit one drag/slider gesture once; canceled gesture restores baseline without history. A new edit after undo clears redo. Selection and viewport changes are not document history.
- [ ] Test A→B→C undo twice/redo twice, edit-after-undo, failed action, canceled gesture, and cap eviction. Assert older states' arrays remain unchanged.

**Gate:** V1 `HistoryTests`; import/reorder/remove are undoable in UI.

### T13 — Canvas viewport, pan, zoom, and hit testing

**Dependencies:** T12. **Files:** `Core/Geometry/ViewportTransform.cs`, `App/Editor/CanvasView.cs`, `App/Editor/CanvasInputController.cs`, `Core.Tests/ViewportTransformTests.cs`.

- [ ] Define forward/inverse document-pixel ↔ viewport-DIP transforms with zoom and pan. Do not bake DPI into document coordinates. Draw scene plus independent selection adorners.
- [ ] Implement zoom-about-pointer and Space+drag pan; hit test topmost visible image bounds. Respect text-input focus before handling shortcuts.
- [ ] Tests: roundtrip points including negatives at 10%, 100%, 800%; pointer anchor remains stable under zoom; hit order follows image drawing order.

**Gate:** V1 transform tests; manual fit/zoom/pan does not change export pixels.

### T14 — Free layout, move, selection, and nudging

**Dependencies:** T13. **Files:** `Core/Editing/{SelectionState,MoveImagesAction}.cs`, `App/Editor/Tools/MoveTool.cs`, `Core.Tests/MoveImagesTests.cs`.

- [ ] Switching to Free preserves geometry; implement click/Ctrl-click, group move, drag capture/lost-capture cancellation, 1px/10px nudge, and Delete. Pointer previews are transient, release commits one action.
- [ ] Round only final bounds. Preserve relative group offsets at negative coordinates. Escape restores the pre-drag state.
- [ ] Tests: move two images by (-15,8), undo once restores both; hidden images cannot be pointer-selected; changing zoom does not change nudge distance.

**Gate:** V1 move tests; manual 5-image overlap selection and Escape recovery.

### T15 — Resize, duplicate, z-order, and snapping

**Dependencies:** T14. **Files:** `Core/Geometry/ResizeGeometry.cs`, `Core/Layout/SnapEngine.cs`, `Core/Editing/LayerActions.cs`, `App/Editor/Tools/ResizeTool.cs`, `Core.Tests/{ResizeGeometryTests,SnapEngineTests}.cs`.

- [ ] Corner resize keeps opposite corner fixed and aspect by default; minimum 1×1; Shift unlocks aspect. Numeric fields produce the same bounds. Duplicate references same asset with new layer ID and +16px offset.
- [ ] Implement front/back order separately from layout order. Snap against edges/centers within 6 viewport DIPs; convert tolerance by zoom; Alt disables. Use deterministic nearest-distance/ID tie-break.
- [ ] Tests: 100×50 resize to width200 gives 200×100; opposite corner unchanged; 6-DIP tolerance equivalent across zooms; duplicate/undo asset count stays one.

**Gate:** V1 targeted classes; manual resize handles remain constant visual size under zoom.

### T16 — Nondestructive image crop

**Dependencies:** T15. **Files:** `Core/Editing/CropImageAction.cs`, `Core/Geometry/CropGeometry.cs`, `App/Editor/Tools/CropTool.cs`, `Core.Tests/CropGeometryTests.cs`.

- [ ] Show crop handles and numeric source-pixel edges; clamp to source. Enter commits; Escape cancels. Free-mode crop preserves position of surviving pixels; automatic layout reflows.
- [ ] Convert pointer through inverse image transform. Identity orientation is supported now; reserve orientation test cases for T39. Reset crop restores full source.
- [ ] Exact test: crop `(0,0,100,50)` rendered `(10,20,200,100)` to `(10,5,80,40)` → bounds `(30,30,160,80)`. Undo restores source crop and bounds.

**Gate:** V1 crop tests; exported pixels equal the intended source subrectangle.

### T17 — Canvas bounds, background, and fit

**Dependencies:** T16. **Files:** `Core/Editing/CanvasActions.cs`, `Core/Geometry/DocumentBounds.cs`, `App/Editor/CanvasPropertiesView.xaml`, `Core.Tests/DocumentBoundsTests.cs`.

- [ ] Implement Fit canvas around visible content plus padding, explicit canvas rectangle, and transparent/opaque background. Canvas crop only changes export rectangle and is undoable.
- [ ] Negative content stays fixed; export applies origin translation. Checkerboard is preview-only.
- [ ] Tests: two layers at (-20,-10,100,50) and (100,20,80,30), padding5 → export `(-25,-15,210,70)`; canvas cropping excludes only output pixels, not source assets.

**Gate:** V1 bounds tests; manual export with off-origin content and transparent margins.

### T18 — Editable project save/load

**Dependencies:** T03, T12, T17. **Files:** `Core/Contracts/IProjectStore.cs`, `Storage/Projects/{ProjectStore,ProjectManifest,ProjectValidator}.cs`, `Windows.Tests/ProjectRoundTripTests.cs`; modify editor open/save commands.

- [ ] Implement `.sio` ZIP with manifest + referenced normalized PNGs + optional preview. Maximum entries=102. Validate allowed names and expanded-byte budgets while streaming. Do not extract supplied paths.
- [ ] Save atomically; open validates complete candidate state before replacing active document. Keep duplicate/hash references stable. Document close/new prompts only for unsaved changes; canceled Save As aborts close.
- [ ] Roundtrip crop, off-origin bounds, layout order, z-order, alpha, orientation defaults. Test missing asset, malformed JSON, future schema, traversal entry, duplicate ZIP name, excessive decompressed bytes, disk failure.

**Gate:** V2 roundtrip tests; reopen on a second path after deleting original imported files; layers remain independently editable.

### T19 — Asset lifetime and cache eviction

**Dependencies:** T18. **Files:** `Core/Documents/AssetReferences.cs`, `Storage/Assets/AssetRetentionService.cs`, modify `BitmapAssetCache.cs`, `Windows.Tests/AssetRetentionTests.cs`.

- [ ] Gather references from current state, undo/redo states, recovery, and in-progress save. Dispose evicted decoded bitmaps where applicable; never delete a disk asset solely because it left the decoded cache.
- [ ] Separate temporary session assets from history assets. Cleanup orphan temporary files on next launch after validating their owned directory.
- [ ] Tests: delete layer then undo after cache eviction reloads pixels; saving while cleanup runs keeps all saved assets; cap eviction releases references only when no other owner remains.

**Gate:** V2 retention tests; repeat import/remove/undo cycles without missing-image regressions.

### T20 — Layout presets and Release B gate

**Dependencies:** T18, T19. **Files:** `Storage/Settings/LayoutPresetStore.cs`, `App/Editor/LayoutPresetViewModel.cs`, `Windows.Tests/LayoutPresetTests.cs`, `docs/evidence/release-b.md`.

- [ ] Save named layout options/background defaults, rename/delete, and load with validation. Presets contain no assets. Ship presets Vertical compact, Vertical spaced (gap12/padding16), and Horizontal comparison.
- [ ] Loading a preset changes layout as one undoable action; Free remains a distinct mode. Corrupt preset is ignored with a message, not an app startup failure.
- [ ] Run B acceptance, V0/full V1/V2. Confirm the primary three-image workflow is fast enough before adding capture.

**Deliverable:** full quick-combine and free-canvas workflow, save/reopen included.

## Release C: Windows capture integrated with the editor

### T21 — Monitor topology and physical-pixel geometry

**Dependencies:** T20. **Files:** `Windows/Native/{MonitorNative,WindowNative}.cs`, `Windows/Capture/MonitorService.cs`, `Core/Capture/MonitorInfo.cs`, `App/app.manifest`, `Core.Tests/MonitorGeometryTests.cs`.

- [ ] Enable Per-Monitor V2 before window creation. Enumerate physical monitor bounds and DPI, including negative origins and portrait layouts. Topology fingerprint includes IDs, bounds, and DPI.
- [ ] Define conversion per overlay HWND; use screen physical coordinates for global selection. Detect display changes and invalidate active selection.
- [ ] Synthetic tests: monitor left of primary, monitor above primary, 100%+150% pair, and blank gap. Verify unions/intersections exactly.

**Gate:** V1 geometry tests plus real mixed-DPI manual evidence. A one-monitor developer cannot claim mixed-monitor support verified.

### T22 — GDI snapshot backend and resource ownership

**Dependencies:** T21. **Files:** `Core/Capture/{CaptureRequest,CaptureResult}.cs`, `Core/Contracts/ICaptureService.cs`, `Windows/Native/{GdiNative,SafeGdiHandles}.cs`, `Windows/Capture/GdiCaptureService.cs`, `Windows.Tests/CaptureBufferTests.cs`.

- [ ] Capture physical desktop bounds via compatible DC/bitmap and BitBlt. Restore selected objects and release every handle in `finally`/safe-handle ownership. Convert opaque captured BGR data to correct alpha and encode PNG.
- [ ] Composite separate monitor regions into union bounds with transparent display gaps. Honor cancellation before/after acquisition; no native call runs on UI thread when it can be isolated safely.
- [ ] Test pixel stride/bottom-up conversion with synthetic buffers; manually compare primary/secondary images and cursor-off capture. Run 100 captures and measure GDI handle count returning near baseline.

**Gate:** V2 buffer tests + resource/desktop evidence. No PrintWindow fallback.

### T23 — Region selection overlay lifecycle

**Dependencies:** T22. **Files:** `App/Capture/CaptureCoordinator.cs`, `App/Capture/RegionOverlayWindow.xaml`, `App/Capture/RegionOverlayWindow.xaml.cs`, `App/Capture/RegionSelectionController.cs`, `Core.Tests/RegionSelectionTests.cs`.

- [ ] Implement hide → compositor-settle → snapshot → overlay → select → crop → restore state machine. Never capture overlay dimming into the selected output.
- [ ] Use one overlay per display; draw the frozen snapshot at correct physical mapping. Drag across displays; show width/height; normalize drag direction. Escape/lost display restores editor and creates no asset.
- [ ] Test reverse-direction drag, 1×1 selection, cancel at each state, topology-change cancel, and late backend completion after cancel. Only one capture coordinator may be active.

**Gate:** manual region screenshot matches exact selected pixels on mixed-DPI monitors; V1 state/geometry tests.

### T24 — Monitor/window/full-screen/repeat-region modes

**Dependencies:** T23. **Files:** `Windows/Capture/WindowCatalog.cs`, `App/Capture/CaptureModeController.cs`, `Storage/Settings/LastRegionStore.cs`, `Windows.Tests/CaptureTargetTests.cs`.

- [ ] Add selected monitor, current monitor, all monitors, visible-window area, and last-region modes. Resolve foreground target before activating app UI; filter app-owned/minimized/cloaked windows.
- [ ] Use DWM frame bounds with documented fallback; clip to desktop. If target disappears, return TargetGone. Last region requires matching topology fingerprint.
- [ ] Tests use fake target catalogs for filtering/disappearance/topology; manual Notepad, browser, partially occluded window, secondary monitor. Label visible-area limitation clearly.

**Gate:** V2 target tests; all five modes return expected dimensions without disturbing the active composition on failure.

### T25 — Delay, cursor, hotkeys, and tray

**Dependencies:** T24. **Files:** `Windows/Native/{HotkeyNative,CursorNative}.cs`, `Windows/Shell/{GlobalHotkeyService,TrayService}.cs`, `App/Capture/CountdownController.cs`, `Windows.Tests/HotkeyLifecycleTests.cs`.

- [ ] Add delay options 0/3/5/10 seconds and cancellation, then freeze desktop. Cursor inclusion composites cursor image using its hotspot, position, and clipping at capture boundary.
- [ ] Register configurable hotkeys through HWND message hook with no-repeat; unregister on exit and rebind transactionally. Hotkey collisions leave UI capture working.
- [ ] Tray menu opens editor or capture modes and explicitly quits. Default Close exits until the user enables close-to-tray; make background state visible.
- [ ] Test fake register/unregister ownership and conflict rollback. Manually verify delay captures an open menu and cursor placement at a display edge.

**Gate:** V2 lifecycle tests; repeated launch/exit leaves no tray icon or retained hotkey.

### T26 — Capture-to-editor routing and Release C gate

**Dependencies:** T25. **Files:** `App/Capture/CaptureDestinationRouter.cs`, modify `EditorViewModel.cs`, `Core.Tests/CaptureRoutingTests.cs`, `docs/evidence/release-c.md`.

- [ ] Support New document, Append below/right, Add to free canvas, and Copy only. Route captured PNG through importer/asset store. Commit import + placement as one undoable operation.
- [ ] Preserve previous editor state during capture; cancel adds no history entry. For Free placement use viewport center when nothing is selected; explicit append selects the appropriate axis layout.
- [ ] Test capture-complete vs cancel vs stale completion, document closed during capture, and undo removing only the newly added layer.

**Gate:** full suites and C checklist, especially mixed-DPI/cross-monitor cases. Deliver capture integrated with stitching.

## Release D: annotation, recovery, and distribution

### T27 — Core annotation tools

**Dependencies:** T26. **Files:** `Core/Documents/Annotations/{RectangleAnnotation,ArrowAnnotation,TextAnnotation,HighlightAnnotation,StepAnnotation}.cs`, `Imaging/Rendering/AnnotationRenderer.cs`, `App/Editor/Tools/AnnotationTool.cs`, `Windows.Tests/AnnotationRenderTests.cs`.

- [ ] Add explicit polymorphic records and shared renderer dispatch. Implement rectangle, arrow, text, highlight, numbered step; common color/stroke/font controls. Each draw/edit is one command.
- [ ] Default annotations link to selected image; “On canvas” explicitly uses document coordinates. Linked annotations follow crop/move/resize and are clipped to visible source. Text editing uses WPF input/IME then commits text, font family/size, alignment, and color.
- [ ] Test annotation bounds/transform and project roundtrip; raster tests use geometry/interior pixels, with tolerant font comparisons. Missing font falls back visibly and is recorded, not silently substituted as exact fidelity.

**Gate:** V2 annotation rendering; move annotated image and export to verify alignment.

### T28 — Opaque redaction and flattened output checks

**Dependencies:** T27. **Files:** `Core/Documents/Annotations/RedactionAnnotation.cs`, modify `AnnotationRenderer.cs`, `App/Editor/Tools/RedactionTool.cs`, `Windows.Tests/RedactionExportTests.cs`.

- [ ] Implement solid fill, opacity fixed at 100%, integer outward-rounded coverage. Process redaction after content in its applicable scope; no translucent blur masquerading as redaction.
- [ ] Explain in Save project UI that source pixels remain editable; Copy/PNG/JPEG are flattened. Do not copy source metadata, project ZIP, or hidden layers into flattened output.
- [ ] Test a high-contrast secret fixture fully covered at all scales and export formats. Decode output and assert all covered pixels are fill color; inspecting metadata finds no source image payload.

**Gate:** V2 redaction tests; opaque fill stays opaque after resize/crop and reopening project.

### T29 — Recent captures and local library

**Dependencies:** T26, T19. **Files:** `Storage/History/{CaptureIndex,CaptureHistoryStore,RetentionPolicy}.cs`, `App/Library/{CaptureTrayView.xaml,CaptureTrayViewModel.cs}`, `Windows.Tests/HistoryRetentionTests.cs`.

- [ ] Store capture timestamps, thumbnail path, dimensions, asset ID, pin status. Index writes are atomic; damaged index can rebuild from owned metadata files.
- [ ] Add recent tray, multi-select to combine, rename, delete, pin, and folder reveal. Default retention=200 captures or 500 MiB excluding pinned entries; display actual storage.
- [ ] Test retention oldest-first, pins, shared assets, and open-document protection. Deletion removes library references first, then only unreferenced assets.

**Gate:** V2 retention tests; selected recent captures combine in explicit selection/list order, with visible order numbers.

### T30 — Autosave and crash recovery

**Dependencies:** T18, T29. **Files:** `Storage/Recovery/{RecoveryStore,AutosaveScheduler}.cs`, `App/Shell/RecoveryViewModel.cs`, `Windows.Tests/RecoveryTests.cs`.

- [ ] Implement quiet-period autosave, minimum interval, monotonic revisions, and atomic snapshots. Serialize one save queue per document; outdated completion cannot overwrite newer state.
- [ ] On startup offer recover/discard; restoring creates an unsaved document and keeps the last valid original project. Successful explicit save clears only older drafts for that document.
- [ ] Tests: simulated interrupted write, corrupt newest fallback, stale revision completion, disk-full, save-as race. Manual force-stop/relaunch preserves last completed autosave.

**Gate:** V2 recovery tests and recorded crash drill. No claim of preserving changes newer than the last completed snapshot.

### T31 — Settings, accessible commands, and single instance

**Dependencies:** T30. **Files:** `Storage/Settings/{AppSettings,SettingsStore}.cs`, `App/Settings/SettingsView.xaml`, `Windows/Shell/SingleInstanceService.cs`, `Windows.Tests/SettingsTests.cs`.

- [ ] Persist capture defaults, hotkeys, close-to-tray, retention, and last directories. Validate imported settings; corrupt files fall back with a readable notification and preserve a backup.
- [ ] Use a user-scoped mutex and bounded current-user IPC to activate existing instance/open an explicit file. Validate message size/path; no arbitrary command execution. Alternatively disable second-instance file forwarding initially and simply activate the existing window, documenting the limitation.
- [ ] Add accessible names, focus traversal, keyboard reordering, numeric geometry, high-contrast behavior, and text-scale checks. Tool shortcuts never fire while typing.

**Gate:** V2 settings tests; keyboard-only workflow and 200% text-scale manual review.

### T32 — Performance, cancellation, and resource stabilization

**Dependencies:** T31. **Files:** `Imaging/Rendering/PreviewScheduler.cs`, `Core/Documents/MemoryBudget.cs`, `Windows.Tests/PreviewSchedulerTests.cs`, `docs/evidence/performance.md`.

- [ ] Use latest-request-wins preview revisions, low-resolution proxies during interaction, and full preview after gesture. Do not enqueue unbounded renders.
- [ ] Enforce working-set budgets across import/cache/export/scroll sessions. Record timings, peak memory, GDI handles, image counts, dimensions, machine specs, and exact build.
- [ ] Test stale preview cannot replace a newer one; cancel/remove layers during import and export; repeat 100 capture/edit operations. Fix measured bottlenecks only.

**Gate:** full suites and measured targets from spec; failed targets are documented with reproducible inputs before release, never reported as achieved by assumption.

### T33 — JPEG output and portable baseline package

**Dependencies:** T32. **Files:** `Imaging/Export/JpegExport.cs`, `scripts/publish.ps1`, `Windows.Tests/JpegExportTests.cs`, `docs/evidence/release-d.md`.

- [ ] JPEG offers quality 1–100/default90 and explicit opaque background. Tests use color tolerance for lossy output; alpha is flattened deliberately.
- [ ] Publish without trimming: `dotnet publish .\src\SnagItOpen.App\SnagItOpen.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o .\artifacts\win-x64`. Zip that folder plus README/third-party notices.
- [ ] Run on Windows 11 without a .NET SDK, offline and under a standard user. Check local storage, hotkeys, capture, import/combine, editable save, and uninstall-by-folder-removal behavior. User data remains separately removable.

**Gate:** D acceptance and Release build; package is a baseline milestone, not completion of all required Snagit-like capabilities.

## Release E: overlapping images and scrolling capture

### T34 — Manual seam joining

**Dependencies:** T33. **Files:** `Core/Stitching/{SeamJoin,SeamGeometry}.cs`, `App/Editor/Stitching/SeamEditorView.xaml`, `Core.Tests/SeamGeometryTests.cs`.

- [ ] Add Join overlap mode distinct from Combine. Select two adjacent images, choose vertical/horizontal, set repeated rows/columns to remove from the second image, preview seam, and commit crop+placement as one action.
- [ ] Require equal cross-axis dimensions by default; offer an explicit crop-to-common-width/height preview. Do not rescale text automatically.
- [ ] Test vertical 100×300 + 100×300 with overlap80 → 100×520; horizontal equivalent transposes; reject overlap<0 or overlap>=second dimension. Zero overlap is valid manual append.

**Gate:** V1 seam tests; changing seam after save/reopen remains editable and preserves originals.

### T35 — Conservative overlap suggestion

**Dependencies:** T34. **Files:** `Core/Stitching/{OverlapMatcher,OverlapSuggestion}.cs`, `Core.Tests/OverlapMatcherTests.cs`, `tests/Fixtures/scrolling/README.md`.

- [ ] Work on raw luminance arrays supplied by Imaging. Search candidate suffix/prefix overlaps in bounded range, comparing rows across several central strips. Downsample for candidate ranking then refine at full resolution; no perspective transform.
- [ ] Return suggested overlap, normalized error, best-vs-second distinct-peak margin, and confidence status. Reject low-texture or ambiguous/repeated patterns. Ignore user-selected fixed header/footer margins.
- [ ] Initial tunable acceptance: mean normalized absolute error <=0.03, margin>=0.01 against a different peak outside ±3 rows, and luminance standard deviation>=0.02. Treat these as calibration starting points, not proven universal thresholds.
- [ ] Fixtures: known overlap80, identical frames, uniform blank, repeated table rows, small animation, fixed header, no overlap. Uncertain cases retain manual seam UI instead of silently cropping.

**Gate:** V1 matcher tests and recorded fixture confusion table; do not use a numerical confidence score as a claim of probability.

### T36 — Guided and automatic vertical scrolling sessions

**Dependencies:** T35, T25, T26. **Files:** `Core/Capture/ScrollingSession.cs`, `Windows/Capture/ScrollInputService.cs`, `App/Capture/ScrollingCaptureViewModel.cs`, `Core.Tests/ScrollingSessionTests.cs`, `docs/evidence/release-e.md`.

- [ ] State machine: select content viewport → capture first → scroll/capture next → wait for stabilization → match → accept or pause for correction → finish. Guided mode lets user scroll and explicitly capture the next frame; automatic mode sends bounded wheel input to selected foreground target only.
- [ ] Start with max20 frames, max60 seconds, at most 40MP/32,767px final output, 100ms polling, three stable comparisons, and 2s stabilization timeout. Use injected clock/input/capture adapters for tests. These are configurable bounded defaults.
- [ ] Stop on Escape, focus/target/topology change, repeated unchanged frames, low confidence, user stop, or limit. Unchanged frames indicate probable end; timeout/ambiguity keeps accepted content and offers manual continuation. Live animations may never stabilize.
- [ ] Preview accepted images as layers/seams, avoid repeatedly encoding one ever-growing bitmap. One finished session is an undoable import batch. No background scrolling after session exit.

**Gate:** fake-clock state tests; manual static browser page, nested pane, sticky header, and dynamic feed; report limitations and fallback. This required feature is best effort, not universal scrolling compatibility.

## Release F: expanded Snagit-like capture and editor tools

### T37 — Fixed size/aspect and shaped capture

**Dependencies:** T36. **Files:** `Core/Capture/{SelectionConstraint,CaptureMask}.cs`, `App/Capture/SelectionOptionsView.xaml`, `Imaging/Rendering/CaptureMaskRenderer.cs`, `Core.Tests/SelectionConstraintTests.cs`, `Windows.Tests/CaptureMaskTests.cs`.

- [ ] Add fixed pixel size, fixed aspect ratio, ellipse, and closed freehand polygon options. Constrain in physical pixel space before converting to each overlay. Fixed-size rectangle can be moved before confirmation.
- [ ] Acquire rectangular backing pixels then apply alpha mask. Normalize reverse drags; reject degenerate polygons. Freehand points are bounded and simplified to avoid unbounded memory.
- [ ] Tests: 16:9 constraint, 640×480 fixed size across DPI, ellipse corner alpha0/center opaque, polygon cancellation. Delayed capture must allow menus/tooltips to appear before freezing.

**Gate:** exact shape/size outputs, mixed-DPI manual check, and no overlay in result.

### T38 — Multi-region and interval capture

**Dependencies:** T37. **Files:** `Core/Capture/{MultiRegionSession,IntervalCaptureSession}.cs`, `App/Capture/BatchCaptureViewModel.cs`, `Core.Tests/BatchCaptureTests.cs`.

- [ ] Multi-region collects rectangles from one frozen snapshot; Enter commits in selection order, Escape cancels session, Backspace removes last selection. Offer separate images or immediate vertical/horizontal combine.
- [ ] Interval mode requires explicit start, interval1–60 seconds, maximum20 frames by default, countdown/status/Stop, and an explicit target region. Stop on topology/target change or size limit. Avoid overlapped capture jobs if capture takes longer than interval.
- [ ] Test fake-clock session, no duplicate job, cancellation at tick, one batch undo, and no capture after stop. User-driven mode is visible throughout.

**Gate:** state tests and a five-frame manual session; no hidden or indefinite capture.

### T39 — Quarter-turn, flip, and resize commands

**Dependencies:** T38. **Files:** `Core/Geometry/ImageTransform.cs`, `Core/Editing/{OrientImageAction,ResizeDocumentAction}.cs`, modify renderer/crop/annotation transforms, `Core.Tests/ImageTransformTests.cs`.

- [ ] Enable reserved orientation fields. Centralize crop→flip→rotate→scale→translate and inverse; layout uses oriented dimensions. Update crop handles and linked annotation hit testing to use inverse transform.
- [ ] Add 90° clockwise/counterclockwise, flip horizontal/vertical, scale whole document, and canvas-size-only commands. Image resize already exists; expose clear commands and preserve aspect by default.
- [ ] Tests: asymmetric 2×3 pixels rotate to 3×2 with exact expected corner order; four rotations restore identity; flip twice restores; linked annotation/crop coordinates roundtrip; whole-document 200% doubles positions/sizes and export bounds.

**Gate:** domain and pixel tests plus undo/save/reopen with oriented layers.

### T40 — Horizontal/vertical cut-out

**Dependencies:** T39. **Files:** `Imaging/Effects/StripCutoutProcessor.cs`, `App/Editor/Tools/CutoutTool.cs`, `Windows.Tests/StripCutoutTests.cs`.

- [ ] User selects strip to remove, previews remaining portions joined, and confirms. For a plain image produce a derived normalized asset; old asset remains reachable via history. Reject removing entire dimension.
- [ ] If composition/annotations are selected, expose “Create flattened copy and cut out”; generate a separate derived layer/document without destroying original editable composition.
- [ ] Tests: 100×100 remove y30..50 → 100×80 and old row50 becomes new row30; horizontal equivalent; transparent pixels preserved; undo restores original.

**Gate:** pixel oracle and project roundtrip; no misleading promise of editable remapping across removed strips.

### T41 — Callouts, lines, ellipses, and freehand

**Dependencies:** T40. **Files:** `Core/Documents/Annotations/{CalloutAnnotation,LineAnnotation,EllipseAnnotation,FreehandAnnotation}.cs`, `App/Editor/Tools/{CalloutTool,FreehandTool}.cs`, modify AnnotationRenderer, `Windows.Tests/ExtendedAnnotationTests.cs`.

- [ ] Extend explicit JSON discriminator list with schema migration as needed. Callout combines text box and movable tail anchor; line/arrow share geometry, ellipse has stroke/fill, freehand stores bounded source/document points.
- [ ] Add selection handles and editable properties. Simplify freehand within one source-pixel tolerance while preserving endpoints; capture pointer until release, Escape cancels.
- [ ] Test save/load all tool types, crop clipping, rotated linked annotations, callout bounds including tail, and freehand endpoint preservation. Unknown future tool discriminator produces UnsupportedSchema, not dropped content.

**Gate:** shared preview/export fidelity and text/IME manual check.

### T42 — Blur and pixelate effects

**Dependencies:** T41. **Files:** `Core/Documents/ImageEffect.cs`, `Imaging/Effects/{BlurProcessor,PixelateProcessor}.cs`, `App/Editor/Tools/ObscureTool.cs`, `Windows.Tests/ObscureEffectTests.cs`.

- [ ] Add effect records keyed by layer ID in source-pixel coordinates; schema migration initializes empty effects for older projects. Define effect order explicitly as array order before image orientation/scale and before linked annotations.
- [ ] Pixelate uses block size2–64 anchored at effect rectangle origin; blur uses bounded separable kernel/radius1–32. Process only affected regions plus blur sampling margin; clip writes to effect region.
- [ ] Tests: deterministic block averages on tiny fixtures; outside pixels unchanged; alpha treatment documented; undo toggles effect without losing originals; viewport zoom does not change source effect strength.

**Gate:** pixel tests and budget checks. UI clearly distinguishes blur/pixelate from opaque redaction.

### T43 — Magnify and stamps

**Dependencies:** T42. **Files:** `Core/Documents/Annotations/{MagnifierAnnotation,StampAnnotation}.cs`, `Imaging/Rendering/MagnifierRenderer.cs`, `App/Editor/Tools/{MagnifierTool,StampTool}.cs`, `Windows.Tests/MagnifierTests.cs`.

- [ ] Magnifier references a source layer/region, zoom factor1.25–8, output bounds, and border. Sample processed image content before magnifiers to avoid recursion; respect crop and source redactions when generating magnified pixels.
- [ ] Stamp supports bundled simple symbols and imported normalized image assets. No web downloads. Resize/rotate through explicit stamp transform; include stamp assets in persistence/reference counting.
- [ ] Tests: known source color magnifies correctly; no self-reference recursion; redacted region remains covered inside lens; deleting referenced layer either deletes linked lens in same action or leaves a clear invalid-reference error before commit.

**Gate:** render, save/load, and asset-retention tests including imported stamps.

### T44 — Borders, shadows, edges, and tool styles

**Dependencies:** T43. **Files:** `Core/Documents/EdgeEffects.cs`, `Imaging/Effects/EdgeEffectRenderer.cs`, `Storage/Settings/ToolStyleStore.cs`, `Windows.Tests/EdgeEffectTests.cs`, `docs/evidence/release-f.md`.

- [ ] Add optional border, bounded shadow, rounded corners, and deterministic torn edge to selected image or document output. Store seed and effect settings; define effects order: image pixels/effects → edge mask → border/shadow → annotations/redactions.
- [ ] Fit canvas includes shadow/border expansion. Explicit canvas crop may clip effects intentionally. No checkerboard/UI marks in export. Save styles separately from image content.
- [ ] Tests: reopening preserves torn-edge geometry; effect bounds are not cut off after Fit; disabling effects restores original image rendering; rounded corners preserve alpha.

**Gate:** full suites and F acceptance; capture and editing breadth now matches all required tool rows before final integration.

## Release G: feasibility gates and final integration

### T45 — Optional local OCR feasibility

**Dependencies:** T44. **Files:** `docs/evidence/ocr-spike.md`; if successful, `Windows/Ocr/LocalOcrService.cs`, `App/Editor/OcrTextView.xaml`, `Windows.Tests/OcrAdapterTests.cs`.

- [ ] Check current official Windows OCR desktop support, package-identity requirements, available language packs, and image-size restrictions before choosing an API. Do not assume a UWP sample works unchanged in an unpackaged WPF application.
- [ ] Evaluate an offline engine using a small English fixture set and the intended portable deployment. Record accuracy/installation footprint/license. If requirements require packaging changes, document that tradeoff before implementation.
- [ ] If feasible, provide extract text from selected region → editable text panel → explicit Copy text. No automatic replacement of image text. Missing language/engine produces unavailable state with instructions.

**Gate:** documented decision. This is optional and cannot block the required application. Future package/API research must be freshly verified at execution time.

### T46 — Modern Windows capture compatibility evaluation

**Dependencies:** T44 (T45 optional). **Files:** `docs/evidence/capture-backends.md`; if adopted, `Windows/Capture/GraphicsCaptureBackend.cs`, `Windows.Tests/GraphicsCaptureLifecycleTests.cs`.

- [ ] Verify current Windows.Graphics.Capture desktop interop path with official samples. Prototype only the backend: support check, target selection, one-frame snapshot, cancellation, resize/device-loss cleanup, HDR-to-SDR policy.
- [ ] Compare GDI vs modern capture for an occluded ordinary window, accelerated browser, multiple monitors, HDR display if available, and minimized/closed targets. Never promise protected content or minimized-window capture without evidence.
- [ ] Adopt behind ICaptureService only if packaging and lifetime checks pass. Keep a visible fallback and backend-specific limitations. Do not stall the project to write a generalized video pipeline.

**Gate:** explicit adopted/deferred outcome and test evidence. GDI visible-window mode remains a supported baseline; true isolated window capture is conditional on this gate.

### T47 — Capture presets, pin-to-screen, and fast output

**Dependencies:** T44, T46 evaluation. **Files:** `Storage/Settings/CapturePresetStore.cs`, `App/Shell/PinnedImageWindow.xaml`, `App/Editor/QuickOutputViewModel.cs`, `Windows.Tests/CapturePresetTests.cs`.

- [ ] Save validated mode/delay/cursor/destination/hotkey presets. Last-region presets include topology fingerprint. Hotkey conflicts follow existing transactional registration behavior.
- [ ] Pin opens a flattened topmost movable/resizable reference window with opacity control and explicit Close. Pinned windows are hidden along with app-owned windows for new capture. They never intercept all input invisibly.
- [ ] Quick output presets target local folder/filename template/PNG or JPEG/copy. Avoid overwrite by default using unique timestamp+counter; no shell commands or uploads in preset data.
- [ ] Test corrupted presets, naming collision, pin-close resource disposal, and capture excluding own pins.

**Gate:** one-hotkey capture→append and one-click copy/export; all settings survive restart.

### T48 — Full product acceptance and final package

**Dependencies:** every required task, T45 decision, T46 decision. **Files:** `docs/evidence/release-final.md`, update `docs/CAPABILITIES.md`, `README.md`, package artifacts.

- [ ] Execute every required row of the acceptance checklist on Windows 11. Track unsupported/not-tested cases explicitly. Validate at least one mixed-DPI, negative-origin, standard-user environment.
- [ ] Run Release build, full V1/V2, 100-operation resource run, save/reopen fixtures from earlier schema versions, recovery drill, keyboard/high-contrast checks, and clean-machine portable test.
- [ ] Audit third-party notices and locked package versions; create ZIP and SHA-256 checksum. Record version, OS/build/architecture, known capture limits, performance measurements, and optional features not included.
- [ ] Rewrite README as actual installation/use instructions once implemented, retaining planning links. Mark the product complete only when required capabilities pass; otherwise publish a named preview with the remaining rows listed.

**Deliverable:** a local Windows 11 Snagit-like capture/editor application with a stitching-first workflow and documented compatibility, not merely a feature skeleton.

## Risk and effort guidance

Expect ordinary domain/UI tasks to take roughly 1–4 focused engineering hours each, larger native/imaging tasks 4–12 hours, plus human testing. These are planning ranges for experienced oversight, not promises about LLM speed. A broad stable tool of this scope is a multi-week project; mixed-DPI capture, image lifetime, rich annotations, and scrolling can dominate the schedule. Use release gates to obtain useful software early.

Highest-risk tasks for experienced review: T04 (threading/decoding), T21–T25 (DPI/native resource lifecycle), T30 (recovery races), T35–T36 (scroll matching/input), T39 (coordinate transforms), T42–T44 (render/effect ordering), T46 (WinRT/Direct3D). Smaller models should implement bounded pieces, while a stronger reviewer or knowledgeable human verifies these contracts and Windows evidence.

## Requirement coverage

F01 T03/T04/T08/T09; F02/F03 T05/T10; F04 T06–T08/T11; F05 T13–T15; F06 T12/T16/T17; F07 T18/T20; F08 T21–T24; F09 T25; F10 T26; F11 T27/T28; F12 T29–T33; F13 T34–T36; F14 T37; F15 T38; F16 T39–T41; F17 T42–T44; F18 T47; F19 T45. T48 verifies the complete required matrix.
