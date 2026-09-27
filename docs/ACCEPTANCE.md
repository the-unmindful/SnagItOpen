# Windows 11 acceptance checklist

This checklist is for the future implementation. No boxes are prechecked because no application has been built in this planning task. Store evidence under `docs/evidence/` with build/version, machine details, input fixtures, actual result, and untested conditions.

## Release A: combine and output

- [ ] Drop red 100×50 and blue 80×30 images; Vertical, center, gap10, padding5 gives 110×100 with positions `(5,5)` and `(15,65)`.
- [ ] Horizontal with the same settings gives 200×60; changing back reproduces original positions.
- [ ] Match width100 + allow upscale gives second image100×38 and final110×108. Disabling upscale leaves second80×30.
- [ ] Reorder using mouse and keyboard; visible order and exported order agree.
- [ ] Paste from Windows Snipping Tool; append to current layout without a save dialog.
- [ ] Drop three files with one corrupt image; two valid images remain and the failed filename is reported.
- [ ] PNG output has exact pixel dimensions and alpha; status-bar dimensions agree with an external viewer.
- [ ] Copy image pastes successfully into Paint and another real destination; document transparency differences are recorded.
- [ ] Empty document cannot copy/export; bad numeric gap/padding input does not corrupt geometry.

## Release B: custom composition and persistence

- [ ] Switch Vertical → Free: all positions stay unchanged. Move/resize/reorder, then undo each action.
- [ ] One long drag is one undo entry; Escape during drag restores starting position.
- [ ] Nudge 1px and Shift-nudge10px at 25%, 100%, and 400% zoom; export movement stays identical.
- [ ] Resize 100×50 to width200 with aspect lock →200×100. Unlock permits independent width/height.
- [ ] Crop the exact T16 fixture; geometry and exported pixels match the oracle.
- [ ] Fit a canvas containing negative-coordinate images; no unintended border or clipped image appears.
- [ ] Crop canvas, undo, and verify previously outside pixels are still available.
- [ ] Save `.sio`, close, remove original source files, reopen; every image remains editable with the same crop/position/order.
- [ ] Malformed/future-schema/missing-asset project does not replace the currently open composition.
- [ ] Failed save leaves existing destination intact and dirty state unchanged.
- [ ] Layout preset roundtrip survives restart and contains settings only.
- [ ] Asset cache eviction followed by undo reloads the original asset correctly.

## Release C: capture

Run on the user's actual displays. If a configuration is unavailable, mark it untested rather than passed.

| Setup | Region | Window area | Monitor | All monitors | Append |
|---|---|---|---|---|---|
| Single monitor100% | pending | pending | pending | pending | pending |
| Single monitor150% | pending | pending | pending | pending | pending |
| Mixed100%/150% | pending | pending | pending | pending | pending |
| Secondary left/above primary | pending | pending | pending | pending | pending |
| Portrait secondary | pending | pending | pending | pending | pending |

- [ ] Selection crossing displays has exact pixel boundaries and no stretching.
- [ ] Monitor gaps become transparent, not repeated pixels or uninitialized memory.
- [ ] Reverse-direction drag and 1×1 selection work; Escape creates no image/history entry.
- [ ] Main editor, tray-owned popup, and selection overlays are absent from output.
- [ ] Capture with cursor off/on places hotspot correctly, including boundary clipping.
- [ ] Delay3/5/10 seconds captures an open menu/tooltip after countdown.
- [ ] Foreground target is remembered before the editor takes focus; closed target returns a clear failure.
- [ ] Visible-window mode preserves occlusion and accurately labels its limitation.
- [ ] Last region after monitor topology change asks for reselection.
- [ ] Disconnect/change display during selection cancels safely and restores editor.
- [ ] Hotkey collision gives a usable error and keeps menu/button capture available.
- [ ] Append capture adds exactly one layer; one undo removes it; canceled capture changes nothing.
- [ ] One hundred captures leave GDI handle/resource counts near baseline after cleanup.
- [ ] Protected/black output is inspectable with accurate limitations; no unsupported bypass is attempted.

## Release D: annotations, recovery, and baseline distribution

- [ ] Rectangle/arrow/text/highlight/step preview matches flattened export.
- [ ] Move/crop/resize an annotated image; linked annotations remain attached and clipped correctly.
- [ ] Text typing, selection, multiline text, and IME work without capture shortcuts firing.
- [ ] Opaque redaction stays fully opaque in PNG/JPEG/clipboard output; no source data/metadata is embedded.
- [ ] Saving editable project discloses that original/cropped/redacted source pixels remain in that file.
- [ ] Recent library selection combines in visible order; pinned history survives retention cleanup.
- [ ] Crash after autosave, relaunch, and recover; original project remains intact.
- [ ] Stale autosave completion never replaces a newer document revision.
- [ ] Clipboard busy, export access denied, disk-full simulation, and invalid settings all preserve the active composition.
- [ ] Keyboard-only workflow, Narrator command names, high contrast, and200% text scale are usable.
- [ ] Measure three-image preview, ten-image interaction, export duration, peak memory, and GDI handles with exact machine/build details.
- [ ] Oversized import/export is rejected before excessive allocation; application remains usable afterward.
- [ ] Portable self-contained package runs offline on Windows11 without an installed SDK, under a standard user.

## Release E: overlap and scrolling

- [ ] Manual overlap80 joins two100×300 images into100×520 without duplicated rows.
- [ ] Known-overlap suggestions agree with ground truth; no-overlap/blank/repeated-row inputs prompt manual correction.
- [ ] Fixed header/footer exclusion works and does not silently remove unrelated content.
- [ ] Guided mode handles user scroll, capture-next, revise seam, undo, finish, and cancel.
- [ ] Automatic mode stops on focus change, target close, Escape, frame/time/pixel limit, and stalled content.
- [ ] Static page, nested scrolling pane, sticky header, and dynamic content outcomes are documented individually.
- [ ] A failed match preserves accepted content and the unjoined frame for manual correction.
- [ ] Scrolling session can save/reopen as editable images/seams; no giant bitmap is repeatedly reencoded during acquisition.

## Release F: Snagit-like tools

- [ ] Fixed640×480 capture is exactly that size at100% and150% display scales.
- [ ] Ellipse/freehand output has alpha outside shape and correct captured pixels inside.
- [ ] Multi-region selection order is deterministic; Enter commits one batch and Escape discards it.
- [ ] Interval session visibly starts/stops; no frames arrive after stop; slow capture never overlaps jobs.
- [ ] Quarter-turns/flips preserve orientation, crop, annotation anchors, and pixel identity after undo/reopen.
- [ ] Resize content versus resize canvas have distinct correct behavior.
- [ ] Strip cut-out joins exact remaining rows/columns and offers flattened copy when required.
- [ ] Callout tails, ellipse, line, freehand, and text remain editable after save/reopen.
- [ ] Blur/pixelate leave pixels outside effect area unchanged and remain visually consistent across zoom.
- [ ] Magnifier samples the intended region and never reveals pixels hidden by source redaction.
- [ ] Imported stamps survive deleting the original file; undo/cache cleanup does not lose them.
- [ ] Border/shadow/edge effects fit correctly; seeded torn edges are stable across reopen and export.
- [ ] Tool style presets restore relevant settings without changing image contents.

## Release G: integration

- [ ] OCR decision is documented; if included, it works offline with clear missing-language handling.
- [ ] Modern capture evaluation records supported cases, limitations, cleanup, and backend fallback; no unsupported success claims.
- [ ] Capture preset hotkeys, pin windows, and quick output survive restart and respect limits.
- [ ] Pin windows hide during capture; closing pins releases images/resources.
- [ ] Earlier project schemas migrate without losing layers/tools; unsupported newer schemas fail explicitly.
- [ ] Every required row in CAPABILITIES.md has evidence. Optional/unverified capabilities are named.
- [ ] Final package includes instructions, known limitations, dependency notices, version, and SHA-256 checksum.

## End-to-end acceptance scenario

Capture two regions from different applications. Import a third image. Combine vertically with matched width, gap12, padding16. Crop the second image. Switch to Free and place a magnified detail beside it. Add callout, numbered steps, and opaque redaction. Save editable project, export PNG, copy to another app, close/reopen project, move one image and its annotations, undo, then export JPEG on white. Finally capture a long static page with scrolling mode and correct a seam manually. All operations are local, and the saved project remains editable.
