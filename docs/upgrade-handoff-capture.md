# Capture and secondary-window upgrade checkpoint

Saved 2026-10-01 on `ui-upgrade`. Worker: capture_upgrade. All work is shared in the current checkout; no worker commits or pushes.

Final owned batch is source-ready. Root reported all seven original added regressions passed in its224/225 Windows run (one unrelated root reflection failure). Final follow-up replaces the modal stale LastRegion notice with a scoped overlay hint; pin initial sizing runs once and recovery follows minimize/restore. The existing pin test now checks150% zoom retention across Hide/Show/minimize/restore; one new notice lifecycle fact brings the expected Windows total from225 to226. Root retains all integrated gates; these last assertions, publish and interactive acceptance are still pending.

## Owned implementation

- U28–U31: `Core/Capture/RegionSelection.cs`, new `CaptureChromePlacement.cs`; `App/Capture/RegionOverlayWindow.cs`, `CaptureOverlayState.cs`, `CountdownBadgeWindow.cs`, `CaptureCoordinator.cs`, `SessionWindows.cs`, new `ScrollingSeamPreview.cs`.
- U32–U36 secondary surfaces: new `App/Shell/DesktopToastWindow.cs`; `LibraryWindow.cs`, `PinnedImageWindow.cs`; `App/Library/CaptureGallery.cs`, new `LibraryActions.cs`; `Windows/Shell/TrayService.cs`; new `Core/Capture/CaptureLibraryQuery.cs`.
- Granted additive preferences: `AppSettings.LastCaptureCustomSize`, `.LastCaptureCustomAspect` with validation; `UiState.CaptureGalleryCollapsed`.
- Tests: new `CaptureAdjustTests` (Core), `CaptureLibraryQueryTests` (Core), `CapturePixelSamplerTests` (Windows, STA), `CaptureWindowIntegrationTests` (Windows, STA/process resources), `ScrollingSeamPreviewTests` (Windows, deterministic pixels).

## Current behavior and APIs

CaptureOnRelease remains true by default. False enables Adjust for dragged rectangles, ellipses and aspects. Eight handles/body movement, screen clamp, aspect lock, Enter/double-click/action confirmation, Escape reset then cancel, pixel/10px moves, Ctrl right/bottom and Ctrl+Shift left/top edge resizing, Tab edge highlighting/action focus are implemented. Fixed/window/multi rules remain. RegionSelection constructor adds trailing `bool captureOnRelease=true`; `Configure`, `BeginAdjust`, `HitTestHandle`, `Handles`, `Confirm` are new APIs.

Overlay: theme tokens; shape-accurate ellipse/freehand clear holes and dashed closing segment; physical frozen bitmap loupe at 8x, grid/center box/coordinates/size/hex, C/Shift+C copying and persisted M toggle; clickable numeric 1–6 mode strip with proximity opacity and phase hints; title/size window label; live announcements; nonmodal fixed size/aspect picker with custom validation and remembered values. Countdown is a nonactivating 72 DIP ring, uses reduced-motion preference and global Escape polling. Overlay windows close before returned results are cropped/masked from the frozen desktop.

Stale LastRegion/topology fallback now displays a transient idle-overlay hint rather than a modal notice while app windows are hidden. `CaptureOverlayState.Notice` clears on completion/cancel, mode/preset changes and SelectAsync cleanup before restore. Pin initial physical sizing runs only once; click-through recovery hides when the pin is hidden or minimized and shows when restored without changing user zoom.

- `CaptureOutcome(..., string? Message=null, CaptureOverlayAction Action=Edit)` adds intent. Enum: Edit, Copy, Save, Pin, AppendBelow, AppendRight, Drag.
- `CaptureCoordinator.CaptureWithPickerAsync(bool aspect, CaptureOptions options, CancellationToken ct=default)` replaces root fixed-size/aspect text prompts.
- `IntervalCaptureWindow(..., TimeSpan interval, int maxFrames, CaptureDestination? destination=null)` accepts the structured dialog destination and caps frames at200. Scrolling seam uses `SeamDialog.Show(...allowZero:true,beforePreview:...)`; new `ScrollingSeamPreview.Build(previous,pending,overlap,rowsPerFrame=120)` shows bounded previous-tail/next-head joins before and after overlap removal.
- `DesktopToastWindow(AppServices,CaptureItem,string title,Action edit,Action pin,string? outputPath=null)`: no activation, capture exclusion, target workarea/DPI placement, six-second dismissal, thumbnail drag, Edit/Pin/ShowFolder. `BeginDrag()` continues a held overlay gesture. Root must call it for Action.Drag before storing/awaiting slow work if possible. Static `ShowError(AppServices,string message,Action details)` returns a nonactivating, excluded, six-second error toast with Details/Dismiss and no dummy capture.
- `LibraryWindow(AppServices,EditorViewModel,Action? openSettings=null)`:960x640 themed search/sort/calendar filters, persisted grid/list, pinned icon, preview and actions, keyboard/context menus, multi-select+drag, protected deletion with persisted Don'tAskAgain, usage bar/settings link.
- `CaptureGallery`:28DIP persisted collapsed header, themed tiles/focus/context actions, shared library actions.
- `PinnedImageWindow(BitmapSource,string,AppServices? services=null,Action? edit=null)` preserves old calls. `OpenWindows`, `Changed`, `CloseAll()`, `DisableClickThroughForAll()`, `IsClickThrough`, `SetClickThrough(bool)`. Hover toolbar/border/shadow, Ctrl wheel10–800% zoom about cursor, Ctrl0 original physical pixels, Alt wheel opacity, Copy/PNG Save/E/arrows/Escape, full context menu, separate clickable recovery tab. Root supplies services/edit callback for configured destination routing.
- `TrayMenuItem(string? Text,Action? OnClick=null,IReadOnlyList<TrayMenuItem>? Children=null,string? Gesture=null,System.Drawing.Bitmap? Image=null,Func<int,Bitmap?>? ImageFactory=null)`. Factory argument is physical icon size. `TrayService.RefreshMenu`, `SetTheme(TrayThemeColors?)`, `SetTooltip`/`Tooltip`; `TrayThemeColors(Color Surface,Color Text,Color Hover,Color Border,bool HighContrast=false)`. Native/high contrast fallback, recursive submenus, hotkey text and DPI-sized icons. Root owns menu content/theme subscriptions.

## Pending at checkpoint

1. First Adjust coach tip is implemented: “Enter edits, Ctrl+C copies”, persisted `TipsShown` flag `CaptureActionBar`, expires after4seconds. Needs final gate/interactive verification.
2. Nonactivating error-toast API is implemented and its native behavior passed the STA regression. Root owns hidden-error routing and full-message Details callback; consult the root checkpoint for integration status.
3. Root owns integration routing: direct held Drag via BeginDrag; Save last export folder; pin E configured destination; tray Last region/empty presets/last5/ordering/live tooltip/icons. Consult the root checkpoint for final status; do not edit root MainWindow files without assignment.
4. Root runs integrated gates. The worker does not run concurrent builds.
5. Final owned observations are now addressed in source: nonmodal scoped LastRegion hint, one-time pin initial zoom and recovery minimize/restore sync. Extended pin assertions and new notice lifecycle test await root's final run. No worker builds or commits.

## Verification evidence and limits

Initial Core red:3 CaptureAdjustTests failed because CaptureOnRelease constructor capability was absent;0warnings. Integrated App compilation reached success per root; root reported a TrayService nullable warning, now corrected. Full final build/test/publish counts have **not yet been received**, so do not claim final verification from this checkpoint. The new source files are untracked and must be included by root.

Pure tests cover eight handles, eight aspect handles, body clamp, immediate semantics, Escape, Enter while dragging/adjusting, edge keys, switching, bar/loupe placements, library query/filter/sort and frozen physical color formatting. Chrome literal color scan in owned App files found no Color.From/newSolidColorBrush/White/Black literals. Transparent brushes represent hit-test/content transparency. Native interactive acceptance still needed:100%/150% and mixed-monitor DPI, actual external drag target, mode/preset gestures, capture exclusion, tray renderer/menu and pin click-through recovery.

Added real STA regressions (written before this resume's robustness changes, passed in root's Windows run): toast WS_EX_NOACTIVATE, GetWindowDisplayAffinity=0x11, unchanged foreground and capture-monitor workarea; error toast explicit Details; pin WS_EX_TRANSPARENT and clickable passive/excluded recovery tab across Hide/Show and closed guard; library dimension/name search, Today/Pinned filters, context pin toggle, persisted view/sort; Enter multi-add, Space preview toggle and Delete preserving assets referenced by the composition. Seam tests passed for exact joined rows, removed repeated rows, bounded tails/heads, overlap validation and source immutability. Last follow-up adds pin150% zoom/minimize/restore assertions and a scoped-notice cancel/commit test; these await root rerun.


## Final root checkpoint — 2026-10-01

All owned code is complete. Root resolved the final review findings (brand hover/press, cleared hotkey labels, High contrast HighlightText and tab focus). Final Release build: 0 warnings/errors; 211 Core + 226 Windows = 437 passed, 0 failed/skipped. Self-contained 0.2.0 publish and isolated filesystem startup smoke succeeded. See `docs/evidence/verification.md` for package/checksum and exact limits. No worker edits or gates remain pending. Manual desktop/hardware acceptance remains open and explicitly documented; installation/push/merge were not performed.
