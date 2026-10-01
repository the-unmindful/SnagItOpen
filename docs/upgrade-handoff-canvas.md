# UI upgrade handoff — canvas and settings

Updated 2026-10-01 by canvas_upgrade. Workspace: existing `ui-upgrade` branch. Changes are shared directly, uncommitted. Root owns integration and the final gate; do not create another checkout or revert another agent's changes.

## Completed canvas domain

Owned files: `src/SnagItOpen.App/Editor/CanvasView.cs`, `ObjectsList.cs`; new `src/SnagItOpen.Core/Layout/SpacingGuides.cs`; new `scripts/make-cursors.ps1`, `src/SnagItOpen.App/Assets/Cursors/rotate.cur`; tests `tests/SnagItOpen.Core.Tests/SpacingGuidesTests.cs`, `tests/SnagItOpen.Windows.Tests/CanvasLayerTests.cs`.

Canvas has two child DrawingVisuals, document and adorners. DocumentRenderer.Draw occurs only in document-layer redraw. Hover/selection/handles/guides/drafts use adorners. Public APIs: `DocumentRenderCount`, `RefreshAdorners()`, `IsAllContentHidden`, `ZoomToSelection()`, `FitWidth()`. Cached frozen theme tokens cover checker/backdrop, selection, handles, guides, distance pills and focus. Handles use real AnnotationGeometry shapes; rotation uses the generated custom CUR; locked annotations cannot begin geometry drag. Canvas keyboard ring, fit-width/selection zoom, annotation/image/export snapping, Alt disable, equal-spacing markers and Ctrl-hover distances are implemented. Line placement normalizes a curved prototype into a midpoint-relative bend. Objects eye/lock are named focusable native toggle peers; row Space and Ctrl+L work; duplicate arrange strip removed.

SpacingGuides is pure: both-axis distances, nearest overlapping neighbours, equal-gap detection ±1 document pixel, no equal-gap result with only two total objects, excluded unrelated rows/blocked gaps, equal-gap snapping. Existing SnapEngine sufficed; no edits there.

Canvas tests: 7 spacing cases and 5 STA canvas cases, including ten 1080p image layers plus fifty real hover-path transitions keeping DocumentRenderCount unchanged, preview/viewport redraw, zoom, UIA toggles/history, hidden redaction/cursor loading, and curved FinishDraw placement. Root adds rotate.cur Resource to the app project.

## Completed settings domain

Owned files: rewritten `src/SnagItOpen.App/Shell/SettingsWindow.cs`; new `Shell/Settings/SettingsPage.cs`, `PreferencesPages.cs`, `CapturePresetsPage.cs`; additive pure `CapturePresetStore.Reorder` in `Storage/Settings/PresetStores.cs`; new `tests/SnagItOpen.Windows.Tests/SettingsDomainTests.cs`.

SettingsWindow constructor: `SettingsWindow(AppServices services, CommandRegistry? commands = null, Action<AppTheme>? previewTheme = null)`. Public `SelectPage(string)`, `SearchSettings`, `PresetsPage`, `EditableSettingsFields`, `VisibleSettingLabels`. Six pages: General, Capture, Shortcuts, Output & library, Appearance, Advanced; alias `Capture presets` selects Capture. 760×560 window, top search across actual row labels on all pages, Save/Cancel. Every AppSettings property except Version and Last*Directory has an editor, including default layout/background, capture options, tray/startup, theme/accent, hotkeys, last custom dimensions/aspect. Numeric editors validate on commit; disabled optional controls skip validation. Enum labels use DisplayNames.For. CommandRegistry/ToolCatalog produce shortcuts.

Draft settings and presets remain in memory until Save. Theme preview rolls back once on Cancel/Closed. Existing hotkeys are suspended while owner MainWindow settings dialog is loaded and reapplied on close. Save commits numeric fields, validates names and canonical duplicate hotkeys, applies startup, saves settings then presets once; IO failure restores prior settings file/startup state and shows InfoBar. SettingsStore.Save is used directly because AppServices.SaveSettings suppresses IO failures.

Capture presets support staged add/duplicate/rename/delete/reorder and editors for every CapturePreset property, including optional region/fingerprint, fixed size/aspect, hotkey, folder browse, live filename example, output format/destination/copy. Duplicate clears hotkey; all names validated. No preset or project schema changes.

Settings tests: eight STA/storage cases for actual row reflection coverage, cross-page search, measured appearance preview plus Cancel rollback without file writes, reorder/save/reload/delete persistence, measured filename-template live example, integral pixel/count validation, and failed preset-save rollback with/without a prior settings file.

## Verification and remaining work

Owned canvas/settings implementation and the narrow integral-input validation fix are complete. Root reports the integrated Debug build passed with zero warnings and observed 211 passing Core tests plus 226 passing Windows tests. Those counts precede the final narrow rail/clipboard/tray/theme adjustments; root owns the final Release gate. This agent did not rerun builds/tests during read-only review. Targeted Core red was observed before implementation; cursor generation succeeded; owned diff whitespace checks passed. No packages, commits, project-schema changes or docs edits besides this requested durable handoff.

Manual checks remain: native visual/focus/UIA/DPI behavior, light/dark themes, cursor and handle shapes, Ctrl distance hover, Alt snapping, responsive shell strips, settings modal interaction. Parent coordinates final checks.

## Read-only shell review in progress

Reviewed MainWindow.Upgrade.cs, XAML, keyboard routing, tray, inspector, command registry/palette, VM status/scale. Reported P1 arrow routing: root consumed arrow keys with selection outside Canvas and mutated document while navigating controls. Latest source now gates nudging to Canvas.IsKeyboardFocusWithin, so that report is addressed.

The parent is addressing these confirmed shell findings; inspect the latest shared source before applying another fix:

- Inspector privacy warning combined active Redact tool with selected-object context, wrongly describing an ordinary annotation or blurred image as secure redaction (P1).
- All-hidden InfoBar relied on HasContent, which counts hidden annotations; Canvas.IsAllContentHidden supplies the intended semantics.
- Gap/Padding NumberBox maximums used MaxDimension instead of their actual limits.
- XAML and shell text contained actual UTF-8 mojibake, confirmed by character codepoints.
- Capture registry/menu gestures did not map live Window/All monitors/Last region/Scrolling hotkeys correctly.
- Saved gallery visibility, snap and outside-canvas preferences were not reapplied to the existing editor; named IsBusy notifications did not refresh command-bar enabled state.
- Repeated status text did not restart expiry; changing Error to Info with identical text left status permanently visible.
- Tray omitted Last region and left the preset submenu visible when empty.
- Palette CanExecute relied on unbound MenuItem.IsEnabled, so unavailable selection/history commands appeared enabled.
- Palette/checkable-menu assignments used SetValue and replaced existing IsChecked bindings; use SetCurrentValue.
- F6 skipped visible collapsed-region strips, and command-bar edge arrows were not constrained to that group.

Deeper canvas/settings review found no additional P1. Document-layer invalidation keys cover displayed document/preview, viewport, editing annotation, outside mode and theme; frozen resources and adorner-only hover paths remain intact. Locked annotations expose no handles through AnnotationGeometry. Focus loss cancels gesture previews without a history entry. Settings and preset persistence use atomic writes; the new IO regression checks failure recovery and theme Cancel.

One owned fix from this review: SettingsPage integer editors previously treated fractional pixel/count input as Save-valid and cast/truncated callbacks before final validation. They now validate whole numbers immediately, disable Save via NumbersValid and keep fractional values out of the draft. Tests precede the production change; no isolated build was run. Owned diff whitespace checks pass.

Remaining unverified manual concern: mixed-DPI placement restoration scales each monitor's global origin using that monitor's DPI, then assigns saved DIP Left/Top to the current window; parent should verify restart across monitors and unplug/reconnect using actual native placement. This is a review concern, not a confirmed P1.

Controls agent was informed about SliderRow track-click ValueChanged ordering before Thumb.DragStarted and is correcting that mouse-gesture commit path plus NumberBox scrub-preview propagation in its owned files. Focused numeric updates reflecting an actual document change are intentional; preserving focus is required. No parent-owned shell or controls files were changed by this reviewer.

## Final read-only integration pass

Reviewed final native menu/tab/combo templates and foreground inheritance in Themes/Controls.xaml, scrollable start card, hidden tool-rail scrollbar, Classic toolbar icon/label wiring, command/menu execution and clipboard exception handling. No confirmed additional P1 or runtime failure was found. Two material P2 findings were sent to root:

- `Themes/Controls.xaml:117/120` shared Button-template hover/press triggers replace brand background with light neutral tokens while PrimaryButton retains white Text.OnAccent. The parent style's hover Background trigger cannot override the child Border template trigger. Copy's local brand/white style has the same problem (`MainWindow.Upgrade.cs:169`). Primary-action hover/press needs a brand-aware template or foreground/background pair.
- `Shell/MainWindow.Upgrade.cs:357` updates capture menu gestures only when the live hotkey is nonempty. Clearing a previously registered hotkey leaves its old menu gesture, which is then copied into the registry/palette and capture split menu. Mapped capture actions must explicitly write the empty string; preset and unrelated menu gestures must remain intact.

No application files were edited in this pass. Root is resolving final findings and completing the Release gate.


## Final root checkpoint — 2026-10-01

All owned code is complete. Root resolved the final review findings (brand hover/press, cleared hotkey labels, High contrast HighlightText and tab focus). Final Release build: 0 warnings/errors; 211 Core + 226 Windows = 437 passed, 0 failed/skipped. Self-contained 0.2.0 publish and isolated filesystem startup smoke succeeded. See `docs/evidence/verification.md` for package/checksum and exact limits. No worker edits or gates remain pending. Manual desktop/hardware acceptance remains open and explicitly documented; installation/push/merge were not performed.
