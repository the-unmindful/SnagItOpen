# Editing/rendering/gallery pre-push review, 2026-10-02

Frozen review range: `19ccd386c4a33b633f5fe9fbf01e57c540b7cfd7..d67d790`.
Scope: Core annotation model/style/validation/editing/layout guides; adjacent annotation geometry; Imaging rendering; App CanvasView, StyleGallery, StyleInsertion, StyleThumbnailRenderer, EffectStyleGallery, ObjectsList; related tests. Read HANDOFF, CLAUDE change safety, relevant PRD, and F-TXT/F-MAG/F-STY/F-GRP plan. No builds, pushes, user-app actions, or commits by this reviewer; root centralizes verification.

## Confirmed findings (fixes authorized by root)

- **P2 — double-click insertion restyles an unrelated selected object.** Frozen `StyleGallery.cs:54–55`: `MouseDoubleClick` inserts, but the first release already executes `Click => Apply`. With a red rectangle selected, double-click the black rectangle tile: the red rectangle becomes black and a new black object appears; undo requires more than one step. F-STY1 requires insertion as one undo step. Tests added first in `GalleryInsertionInteractionTests` for the actual first-click/double-click/second-click event sequence, single-click apply, and keyboard/automation click apply. Awaiting centralized red run before production edit.
- **P2 — fitting rotated text shifts its rotation centre.** Frozen `AnnotationRenderer.cs:337–340,345–347` changes size while preserving X/Y for all rotations. Font/padding/text changes therefore shift the document centre of a rotated text/callout, contrary to F-TXT1's centre anchor for rotated text. Test-first cases added in `TextLayoutTests.Fitting_rotated_text_keeps_its_document_centre` for AutoWidth/AutoHeight/Fixed. Awaiting centralized red run.

## Additional review leads (not final findings yet)

- Canvas group drill-in state: `_deferDrill` is cleared after normal Move release but not in `BeginSelect` or `CancelGesture`; a canceled group click can affect a later unrelated click. Trace complete; deciding materiality and reproduction regression.
- New text placement unconditionally resets `VerticalAlign` to Middle even when a saved tool default says Top/Bottom (`CanvasView:1383`). Root owns related defaults/editor flow; confirm desired new-default behavior before classifying.
- New enum/padding fields are absent from `DocumentValidator`'s text checks; numeric undefined TextSizing/TextVAlign values are accepted. Check concrete failure impact before classifying.
- **Adjacent pre-existing privacy concern, not classified as regression:** DocumentRenderer.MagnifierBase samples outside a locked ExportArea. New source drag can move a source outside while lens stays inside. Root asked to preserve old semantics pending explicit leak proof/intent; no renderer privacy changes planned from literal ambiguity.

## Review/test limits

Current review is static and test design. Existing gallery tests invoke `Apply` directly, insertion tests invoke `Create`, grouping tests cover model helpers, and text tests use unrotated Latin capitals; they do not establish live double-click behavior, cancel recovery, or rotated auto-fit. Further review continues while root runs first regressions.
