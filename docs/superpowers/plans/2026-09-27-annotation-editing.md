# Annotation editing plan

Date: 2026-09-27. Follows Release G of the main plan. Decisions confirmed by the user are marked (confirmed).

## Principle (confirmed)

Annotations are composition (canvas) objects, not parts of images. They live in document pixels, draw above
all images, are never clipped by an image, and are unaffected by image crop, move, resize, rotate, flip,
reorder, hide or delete. Image-level pixel effects (blur, pixelate, edges) remain image properties.

## Phase 0: annotations as canvas objects

- Migration: annotations linked to an image in older projects / drafts (`ImageLayerId`) are converted once to
  document coordinates at their current on-screen position (stroke and font scaled by the image scale), and
  unlinked. Links to missing images are dropped with the geometry kept.
- New annotations are always document-level. The "Attach to image" option is removed.
- Renderer: images first, then annotations in their own order, then redactions, magnifiers (sampling a scene
  without magnifiers), and redactions again. No annotation clip to an image.
- Bounds: auto canvas, Fit canvas, marquee and export use each annotation's full extent.
- Deleting or duplicating an image does not delete or duplicate annotations.
- Tests: migration geometry, annotation over a later image is visible, annotation beyond an image grows the
  canvas, crop leaves annotations untouched, redaction outside any image stays fully covered.

## Phase 1: shared geometry foundation

- `Rotation` (degrees, about the centre) on rectangle, ellipse, highlight, text, callout, step, stamp and
  magnifier lens. Redaction, blur and pixelate stay axis-aligned (confirmed) so coverage stays exact.
- Common `Opacity`, `Shadow` and `Locked` on every annotation.
- Schema version 2; v1 projects open (migrated), newer schemas fail explicitly.
- Core `AnnotationGeometry`: per-type handles (resize, rotate, start/end, tail, bend), shape-accurate hit
  testing, handle-drag results with Shift snapping (15° / proportional), rotated outer bounds.
- Canvas uses these handles; constant on-screen handle size; hover highlight.

## Phase 2: arrows and lines

- Endpoint handles, rotate about midpoint, Shift = 15° with angle readout.
- Per-end caps: none, filled, open, circle, square, bar; head size.
- Solid/dashed/dotted; optional contrast outline.
- Curved mode with a bend handle (quadratic Bézier) (confirmed).
- Hit test along the stroke.

## Phase 3: text and callouts

- In-place on-canvas editor with IME; Ctrl+Enter or click-away commits, Esc cancels (confirmed).
- Rotate handle; side handles re-wrap; height auto-fits.
- Font, size, bold, italic, underline, alignment, text colour, fill, border, corner radius, padding,
  outline/shadow.
- Callout tail handle and tail width; box shape rounded / rectangle / ellipse.

## Phase 4: steps

- Shape circle / rounded square / square / diamond; fill, border, number colours; font and size.
- Labels 1 2 3, A B C, a b c, I II III, or custom; start value, prefix, suffix.
- Next number continues; renumber from selected; +/− adjusts; optional pointer tail; square resize unless Shift.

## Phase 5: editing extras (confirmed)

- Live properties panel editing the selection (shared properties for multi-select; one undo step per change).
- Copy/paste/duplicate annotations (private format; Ctrl+Shift+C still copies the flattened image).
- Right-click menu: edit text, duplicate, delete, arrange, lock, copy/paste style.
- Align and distribute.
- Colour swatches, recent colours, named quick styles.
- Opacity, shadow, lock controls; Tab / Shift+Tab cycle annotations.

## Phase 6: verification

Core geometry tests, Windows render and round-trip tests, full build/test/package, launch smoke test, and a
manual checklist for pointer and typing behaviour.
