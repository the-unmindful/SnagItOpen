# Capture review checkpoint (2026-10-02)

Review of frozen `19ccd386c4a33b633f5fe9fbf01e57c540b7cfd7..d67d790`. Scope: Core/Capture, Core/Stitching, Imaging/ScrollComposer, App/Capture, Windows/Capture and capture/scrolling session tests. Root later authorized focused fixes and regressions for substantiated capture findings. No build, app launch or push in this agent; root centralizes verification.

Read repository instructions, HANDOFF, CLAUDE Change safety, capture PRD requirements and F-SCR plan. Read complete ScrollingSession, OverlapMatcher, ScrollComposer, ScrollingSeamPreview, CaptureCoordinator, CaptureOverlayState, CountdownBadgeWindow, RegionOverlayWindow, RegionSelection, CaptureChromePlacement, SessionWindows, GDI/monitor/window/scroll-input code and relevant changed tests.

Findings being substantiated:

1. Adaptive scrolling uses `content - overlap` although overlap includes sticky bands (`Sessions.cs:144`). Actual added/displaced rows are `frame.Height - overlap`. With sticky bands this overestimates required wheel notches, potentially causing repeated overshoot/manual stops.
2. Manual seam preview ignores sticky-band rules (`SessionWindows.cs:319` and `ScrollingSeamPreview.cs:15-18`). Preview retains previous footer and skips overlap rows; final composer removes previous footer and skips overlap minus footer. User cannot tune an actual seamless boundary from this preview on sticky-footer pages.
3. Automatic scrolling never updates the new live preview or detected row controls. `UpdatePreview` is only called by guided capture/manual pending acceptance; no Changed subscription exists.

Continuing cancellation/resource lifecycle and mixed-window/session integration analysis. Root reviewer notified about session captures not participating in Coordinator.IsBusy.

## Focused fix plan and checkpoint

- Tests written before production changes: `Automatic_step_measures_new_rows_without_subtracting_sticky_bands_twice` and `Sticky_footer_preview_shows_the_same_pixels_as_final_composition`. Root notified to run the red check. Production remains unchanged pending the result.
- Adaptive repro: height 100, header/footer 20 each, initial three notches at ten rows/notch. Full overlap is 70 (30 repeated content + 40 bands). Current `content-overlap = -10` clamps to one estimated new row, next step becomes 15; even the seven-notch backoff leaves an 80-row displacement with only 60 content rows, forcing a manual seam. Expected next step is four notches for 36 target rows.
- Preview repro: previous `[100,101,2,3,4,5,6,7,200,201]`, pending `[100,101,5,6,7,8,9,10,200,201]`, full overlap 7, header/footer 2. Final pixels should be `[100,101,2,3,4,5,6,7,8,9,10,200,201]`; current preview contains the previous footer at the seam and retains only pending row 10 before its footer.
- Minimal design: measure displacement as full height minus overlap; pass explicit sticky header/footer counts into bounded seam-preview composition so it follows final composer boundaries. Existing zero-band API defaults continue to work.
- Also reviewed adjacent `PixelBuffer`, model limits, monitor coverage, capture masks/conversions, capture window tests, seam geometry and unchanged overlap tests. No new GDI handle lifecycle regression identified. Existing baseline limitations (canceled unshown session windows remain subscribed; StopReason resume behavior; session/external capture concurrency) are separated from newly introduced findings.

## Additional findings sent to root

- P2, `App/Capture/SessionWindows.cs:253-260,345-363`: automatic capture never calls UpdatePreview on confident accepted frames; neither a Changed subscription nor end-of-auto refresh exists. Start a scrolling capture, use Auto scroll on textured page and reach its end with several frames: preview remains the first viewport and row boxes stay 0 despite detection. Regression should offer accepted frames through the same automatic session and assert UI preview length/bands update on dispatcher.
- P2, `App/Capture/SessionWindows.cs:119-132,260-261`: first-position calculation uses initial ActualHeight while preview is collapsed. Its later appearance expands the SizeToContent panel by up to 220 px without a new work-area clamp. Select content near bottom of monitor; first preview can push Finish/Cancel below work area. Regression should inspect native panel bounds after showing a tall preview near work-area bottom.
- P2, `App/Capture/SessionWindows.cs:420-422`: scrolling Finish does not inspect `Vm.LastCaptureSucceeded`, unlike normal shell routing (`MainWindow.xaml.cs:1261`). If AddCapturesAsync handles a failure internally, Finish can close all retained frames and overwrite its error status with success. Root owns VM contract review and outcome fix.
- P2 resource risk, `App/Capture/SessionWindows.cs:260`, `Core/Capture/Sessions.cs:14,45`: MaxFrames grew from 20 to 80 without acquisition-memory budget. Full 4K frames (~33 MB each) with ~95% overlap can retain roughly 2.5 GB before the output 40 MP cap; the live preview also retains the full-size bitmap (~160 MB near cap) and creates another full temporary (~160 MB) despite a 300×220 display. `FinishAsync` also does not catch OutOfMemoryException from the newly added Compose. Needs bounded thumbnail generation and/or an accepted-frame memory budget; not changed by this agent.
- Conditional retry contamination, `App/Capture/SessionWindows.cs:411,424-430`: dirty NewDocument Finish restores app windows for save/discard confirmation, but a thrown AddCapturesAsync failure enables capture again without hiding them. A following frame can contain editor pixels. VM error swallowing/outcome behavior decides how reachable this is; root investigating.

No live desktop or mixed-DPI/hardware tests executed. Native exclusion assertions in the existing tests inspect affinity flags rather than actual pixel removal. Root centralizes builds/tests and decides remaining fixes.
