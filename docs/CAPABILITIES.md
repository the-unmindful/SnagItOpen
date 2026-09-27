# Snagit-like capability matrix

The requested target is a local capture-and-editor application with a broad Snagit-like workflow. Early milestones are useful subsets; they are not the final feature set. This is a proposed scope, not a claim of exact parity with every Snagit version. The user confirmed capture/editor breadth after selecting quick combine and free placement as the first priority.

| Area | Planned behavior | Tasks | Final-scope status |
|---|---|---|---|
| Region capture | Drag rectangle, precision adjustment, cancel | T21–T23 | Required |
| Display capture | Active/selected monitor, all displays | T21–T24 | Required |
| Window capture | Visible window area first; evaluate isolated modern window capture | T24, T46 | Required, backend limitations shown |
| Capture shapes | Fixed size/aspect, ellipse, freehand selection | T37 | Required |
| Menu/tooltips | Delayed snapshot after user opens menu/tooltip | T25, T37 | Required; no promise of universal auto menu discovery |
| Repeat capture | Last region, interval capture, multi-region session | T24, T38 | Required |
| Capture controls | Cursor, delay, hotkeys, tray, presets | T25, T31, T47 | Required |
| Scrolling | Manual overlap joins, guided manual scroll, bounded automatic vertical scroll | T34–T36 | Required, best effort across applications |
| Combine | Vertical/horizontal, reorder, matching dimensions, gaps, padding | T05, T10, T20 | Required and highest priority |
| Free composition | Layered images, selection, placement, snapping, crop | T12–T18 | Required and highest priority |
| Basic edit | Crop, rotate, flip, resize, canvas/background | T16, T17, T39 | Required |
| Cut out | Remove a horizontal/vertical strip and join remaining content | T40 | Required |
| Annotation | Text, arrows, lines, rectangle/ellipse, callouts, freehand | T27, T41 | Required |
| Emphasis | Highlight, numbered steps, magnify, stamps | T27, T43 | Required |
| Obscure details | Blur/pixelate for visual use; opaque redaction for hiding content | T28, T42 | Required |
| Styling | Border, shadow, rounded/torn edges, saved tool styles | T44 | Required |
| History | Undo/redo, editable save, capture tray/library, recovery | T12, T18, T29, T30 | Required |
| Output | Clipboard, PNG, JPEG, editable project, quick destination preset | T07, T08, T18, T33, T47 | Required |
| Desktop reference | Pin flattened capture in a movable topmost window | T47 | Required |
| Text extraction | Local OCR, copy recognized text | T45 | Optional feasibility-gated extension |
| Smart Move/text replacement | Automatically understand/edit UI objects or replace screenshot text | Separate future project | Excluded from this plan |
| Video/GIF/audio | Record and edit moving media | Separate future project | Excluded from this plan |
| Online sharing/cloud/AI | Accounts, hosted library, generative media | Separate future project | Excluded from this plan |

No task should describe the whole product as complete until required rows pass. Keep an implementation status column in a working copy as tasks are completed. A later Snagit release may offer additional capabilities; this matrix is the agreed planning boundary, not a moving promise to reproduce all future features.
