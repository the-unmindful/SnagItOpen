# Controls, dialogs, and annotation gallery upgrade handoff

Updated 2026-10-01. Local branch `ui-upgrade`; no agent commits. This work is part of the approved UI upgrade PRD, coordinated by the root agent. The root owns all builds/tests/publish/install gates and integration in MainWindow, App, VM, InspectorPanel, and theme dictionaries. Do not run parallel builds. Read repository AGENTS.md and docs/HANDOFF.md, then the root completion handoff before continuing.

Resumed after the usage reset: U23 Blur/Pixelate galleries and concrete shared-control keyboard/automation/scrub fixes are implemented. Capture confirms scrolling SeamDialog.beforePreview integration. Root reported the integrated gate passed **211 Core + 226 Windows = 437 tests** before its latest rail/clipboard/tray cleanup. This agent ran no separate builds. Release package/publish and final interactive acceptance remain with root.

## Owned implementation

- `src/SnagItOpen.App/Controls/`: ControlVisuals, IconButton, ToolRailButton, SplitButton, NumberInput, NumberBox, SliderRow, ColorSwatchButton, SegmentedControl, InspectorSection, InfoBar, ToastHost.
- `Infrastructure/DialogWindow.cs`, `Dialogs.cs`, `DisplayNames.cs`.
- `Shell/Dialogs/DialogValidation.cs`, ScaleDialog.cs, SeamDialog.cs, IntervalDialog.cs. Their namespace is **SnagItOpen.App.Shell.DialogWindows**, avoiding a collision with Infrastructure.Dialogs.
- `Shell/ShortcutsWindow.cs`, `AboutWindow.cs`.
- `Editor/AnnotationPropertiesPanel.cs`, `ImageEdgePanel.cs`, `StyleGallery.cs`, `StyleThumbnailRenderer.cs`.
- `Storage/Settings/AnnotationStyleStore.cs`, `BuiltInStyles.cs`.
- Resumed U23: `Editor/EffectStyleGallery.cs`, `Storage/Settings/EffectStyles.cs`, and narrow root-approved ToolStyleFile/ToolStyleStore partial/load/save edits in `Storage/Settings/PresetStores.cs`. Other preset/settings changes in that file belong to root.
- Narrow root-authorized extension: RectangleAnnotation.Dash (default solid), DocumentValidator's rectangle dash check, and AnnotationRenderer's dashed rectangle pen. Root canvas agent owns all other canvas/renderer integration and normalizes curved prototypes to actual endpoints when drawing.

## APIs used by root

- `IconButton`: Geometry Icon; string Label, Shortcut; bool ShowLabel; Button Click. Tooltip auto-composes label/shortcut; automation name/accelerator track them.
- `ToolRailButton`: RadioButton; Icon/Label/Shortcut; 36x36 with selected background and required 3px selection indicator.
- `SplitButton`: IconButton PrimaryButton; ContextMenu Menu; Icon/Label/Shortcut; Click; OpenMenu.
- `NumberBox`: Value/Minimum/Maximum/Step/Label/IsMixed dependency properties; Input TextBox; IsValid; Preview/Committed Action<double>, ValidityChanged Action<bool>; CommitEdit(), Revert(). Value/IsMixed internal updates preserve bindings via SetCurrentValue. Explicit UpdateSourceTrigger callers must update source from Committed.
- `SliderRow`: Slider, Number (NumberBox); Value/Minimum/Maximum/Step/Label/Unit/IsMixed/DefaultValue; Preview/Committed; Update(value,mixed). Drag previews continuously and commits once on release.
- `ColorSwatchButton`: ValueProperty nullable Rgba32, IsMixedProperty; Label/AllowNone/Recent; Preview/Committed/Canceled; OpenPicker. Calls existing ColorPicker (root has themed its chrome).
- `SegmentedControl`: options `SegmentOption(object Value,string Label,Geometry? Icon)`; SelectedValue and IsMixed DPs; Buttons; SelectionChanged; Add/SetOptions.
- Resumed controls fixes: segments use one tab stop and arrows/Home/End to select; SplitButton Alt+Down and gallery Alt+Left/Right handle WPF SystemKey; numeric accessible labels survive Refresh; NumberBox label scrub can Escape back to its starting value/mixed state; SliderRow forwards numeric scrub previews and treats track-press plus thumb drag as one gesture/commit.
- `InspectorSection(AppServices,string key,string title,bool expanded=true)`, or ConfigurePersistence(Func<UiState>,Action<UiState>,key,expanded). Saves the latest state's section map; no static open-state dictionary.
- `NotificationKind {Info,Success,Warning,Error}`; `NotificationAction(string Label,Action Execute)`; `Notification(kind,title,message=null,actions=null,thumbnail=null)`.
- `ToastHost.Show`, Clear, VisibleCount, Notifications; Advance(TimeSpan) is the timer's deterministic clock operation. Max 3; oldest drops; icon plus optional 48px thumbnail; max2 actions; lifetime4/8s; hover/focus pause; live-region peers; no animations.
- `InfoBar`: Kind/Title/Message DPs; ActionLabel, Action, CanClose, Closed; full1px border.
- `DialogWindow(owner,title,UIElement body,primary="OK",cancel="Cancel")`: Body, PrimaryButton, CancelButton, ButtonRow; Validate, Accepted; AddSecondary, Finish; handles Enter primary/Esc cancel for modal/nonmodal use.
- `Dialogs.Prompt/Info/Error` retain old signatures; Confirm(owner,title,message,primary="Yes",secondary="No",cancel="Cancel") returns Yes/No/Cancel. Raw MessageBox only before theme resources are available. Unused EditText was removed after checking for callers.
- `ScaleDialog.Show(owner,width,height)` returns ScaleRequest(Width,Height,ScaleContent,FactorX,FactorY), with Factor alias FactorX. Root added anisotropic Core scale handling.
- `SeamDialog.Show(owner,availableDimension,initial=1,preview=null,renderPreview=null,note=null,allowZero=false,beforePreview=null)` returns int?. For scrolling pass allowZero:true. For true before/after pass original ImageSource as beforePreview; renderPreview supplies the live result. Last optional parameter added on handoff update.
- `IntervalDialog.Show(owner,destination=AppendBelow)` returns IntervalRequest(Seconds,Frames,Destination); bounds1-60s,1-200frames.
- `ShortcutsWindow(owner,IEnumerable<ShortcutEntry>,Action? changeGlobalShortcuts=null)`: ShortcutEntry(Category,Title,Gesture), SearchText, VisibleEntries, EntryCount; nonmodal, grouped search and KeyCap labels. Root supplies registry/tool/global/overlay inventory.
- `AboutWindow(owner,dataFolder)` uses assembly version, themed S identity, MIT/local-offline copy, notices/open-folder buttons.
- `DisplayNames.Destination`, For/Get(Enum), Humanize.

## Gallery/panel implementation

AnnotationStyleFile is version2 with an optional additive Gallery dictionary; four-argument construction and the old Quick field remain compatible. GalleryEntry(Id,Name,Annotation Style,BuiltIn=false,Hidden=false). Missing Gallery migrates old Quick styles once. Stable shipped IDs cover12 drawing annotation kinds with6-8styles each; Arrow has8 prescribed variants, rectangles include real dashed borders. Store APIs: GalleryFor(kind,includeHidden), SaveGallery, RenameGallery, HideGallery, DeleteGallery, DuplicateGallery, MoveGallery/MoveGalleryBy, RestoreBuiltIns. Forty total entries per tool including built-ins; extra user additions raise an inline handled InvalidOperationException rather than silently deleting a saved style. Built-ins cannot rename/delete; delete hides and restore unhides. Corrupt-file backup/default behavior stays intact.

StyleGallery uses56x40 tiles, real cached renderer thumbnails by style/theme/checker colors (Magnifier uses its static icon), +save, context apply/default/rename/duplicate/reorder/hide-delete/restore, drag reorder, arrow navigation, Alt+arrows, Enter through Button. Applying selected styles uses one VM history step and preserves target identity, geometry, name, and hidden state. Explicit curved entries create endpoint-relative bends; plain entries preserve an existing curve. Prototypes discard hidden/name metadata.

AnnotationPropertiesPanel retains every existing style/shape/text/step/line property. It rebuilds only for context mode/kind-set/tool changes; registered row callbacks update values, mixed states, visibility, and text in place using VM.Displayed. Optional fill/border/cap/custom-step rows are built once and shown/hidden. Sections persist to UiState. Duplicate Arrange, defaults buttons, Quick styles, and privacy copy moved to root Inspector/header and gallery. ImageEdgePanel builds once (RebuildCount1) and updates selected images' border/shadow/corner/torn controls in place. Both share preview/commit controls.

Blur/Pixelate now integrate `EffectStyleGallery` above their existing strength slider. Six built-ins per tool use theme-dynamic static icons with a numeric strength caption (blur2/4/8/12/20/32; pixelate2/4/8/16/32/64). It supports +save, Apply/default, rename user, duplicate, reorder via drag/Alt+arrows, hide built-ins/delete users, and restore. Defaults change only EffectStrength while retaining other legacy ToolStyle properties; an actual drawn effect uses existing CanvasView/VM AddEffect and one undo step. No existing effect region is changed because effects are not selectable annotations. No .sio schema changes.

`ToolStyleFile` keeps version1/Styles and adds trailing optional `Dictionary<string,EffectGalleryEntry[]>? EffectGallery`. Old two-argument construction is valid. `EffectGalleryEntry(Id,Name,ToolStyle Style,BuiltIn=false,Hidden=false)` persists only meaningful effect strength. ToolStyleStore is partial, with Changed and APIs EffectGalleryFor, SaveEffectGallery, RenameEffectGallery, HideEffectGallery, DeleteEffectGallery, DuplicateEffectGallery, MoveEffectGallery/MoveEffectGalleryBy, RestoreBuiltInEffects. Forty total styles per effect tool; built-ins survive/cannot rename, delete hides, load sanitizes/merges authoritative IDs and old defaults remain unchanged. New UI constructor `(AppServices,EditorViewModel,Action refresh)` exposes Refresh(ToolKind/string), Apply(entry), TileCount/Tiles. Parent needs no new integration API; AnnotationPropertiesPanel uses it internally.

## Tests and verification state

Added Windows.Tests files: SharedControlTests, StructuredDialogTests, ControlInteractionTests, GalleryContractTests. Meaningful coverage includes numeric clamp/invalid/mixed/Shift/Escape and binding survival; icon automation; one slider commit; section persistence; mixed toggles; toast capacity/lifetime/focus pause; shortcut inventory/search; structured limits; gallery roundtrip/migration/cap/hide/corrupt backup/built-in validity; actual thumbnail pixels/cache/theme; curved endpoint mapping; one gallery undo; annotation stable focused undo/mixed values; stable edge tree; dashed rendering and old-JSON solid default.

Resumed batch adds EffectGalleryContractTests (old defaults compatibility, roundtrip/hide/restore/order/cap, each effect gallery default through actual CanvasView.FinishDraw and one undo, stable inspector tree/tile metrics/names), plus ControlInteractionTests for track-click-before-thumb ordering, scrub preview forwarding/automation, and real-window segmented arrow/focus/tab behavior. Root reported the complete integrated gate passed with 211 Core and 226 Windows tests. Those results include this completed code batch; this agent did not independently run tests.

First sandboxed red run hung; retry failed writing Core obj with MSB3491 Access denied. Root instructed all further builds to be centralized and elevated. Root later reported integrated App compile succeeded; xUnit analyzer warnings in gallery tests were then fixed. Latest reported integrated count is 211 Core + 226 Windows. **Do not claim release publish/install or final cleanup verification are finished: root's release package is still pending, and the latest rail/clipboard/tray cleanup follows that gate.** No commits, docs task ticks, installation, or user acceptance claims were made by this agent.

## Remaining checks/work at this checkpoint

1. Root has reported 211 Core/226 Windows passing before its latest rail/clipboard/tray cleanup. Repeat checks appropriate to those cleanup changes and complete Release/publish/package, then record the final exact evidence.
2. Capture agent confirms scrolling before/after uses actual physical seam rows and allowZero:true. Root should confirm Join also passes beforePreview. The dialog API is complete.
3. Confirm UI smoke/dark/high-contrast startup, gallery tile fit/focus, slider one-undo behavior, and mixed controls visually. Existing automated STA tests supplement but do not substitute for interactive acceptance.
4. Blur/Pixelate static strength galleries now exist, with the integrated tests reported passing; visual acceptance remains. The additive ToolStyleFile field has tests for old defaults compatibility.
5. Selection of different annotation kinds preserves existing first-kind property behavior; context keys include the distinct kind set. Review whether a union of each kind's sections is desired by the final inspector acceptance.

Continue editing only assigned files unless coordinated with root. Never revert other agents' changes. Update this handoff after final gate/error repairs.

Final root-requested read-only review of new Controls.xaml menu/tab/combo templates found two material issues and reported them to root: highlighted HC states retain WindowText over HighlightColor instead of HighlightText (Controls.xaml lines65/86/238; HC tokens10-12/16-19), and custom TabItem lacks the required FocusRing (Controls.xaml79-87). Named menu/combo PART_Popup and PART_EditableTextBox are present. No root files were edited and no builds were run during this review; root owns any fixes/final validation.


## Final root checkpoint — 2026-10-01

All owned code is complete. Root resolved the final review findings (brand hover/press, cleared hotkey labels, High contrast HighlightText and tab focus). Final Release build: 0 warnings/errors; 211 Core + 226 Windows = 437 passed, 0 failed/skipped. Self-contained 0.2.0 publish and isolated filesystem startup smoke succeeded. See `docs/evidence/verification.md` for package/checksum and exact limits. No worker edits or gates remain pending. Manual desktop/hardware acceptance remains open and explicitly documented; installation/push/merge were not performed.
