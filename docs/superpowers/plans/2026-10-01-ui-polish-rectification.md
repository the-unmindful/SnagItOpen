# UI polish and editing-quality plan (V01–V24, F-tasks)

Date 2026-10-01. Branch `ui-upgrade`. Baseline `ae40a25` (0.2.0). Part 1 (V-tasks) is visual only: it must not change behaviour, export pixels, settings schema or shortcuts. Part 2 (F-tasks, at the end) changes behaviour as the user requested on 2026-10-01. The PRD (`docs/superpowers/specs/2026-09-30-ui-ux-upgrade-prd.md` §4–7) remains the source of truth for colours and metrics.

## Ownership (two agents work in parallel; never edit the other's files)

| Owner | Tasks | Where | Files owned |
|---|---|---|---|
| **Claude** (lead) | F-TXT, F-MAG, F-STY, F-GRP | `SnagItOpen` folder, branch `ui-upgrade` | `Core/Documents/Annotations/*`, `Core/Editing/*`, `Imaging/Rendering/*`, `App/Editor/*` (except `InspectorPanel.cs`), `App/Shell/MainWindow.xaml(.cs)` |
| **Worker** | F-SCR, V15, V18, V23, V24 | `SnagItOpen-worker` folder (git worktree), branch `polish-worker` | `Core/Stitching/*`, `Core/Capture/*`, `App/Capture/*`, `Imaging` import/export (not Rendering), `App/Editor/InspectorPanel.cs`, `App/Shell/MainWindow.Upgrade.cs`, `App/Themes/*`, tests for these |

- A file outside your column: stop and write the need under "Requests" in `docs/HANDOFF.md`. Don't edit it.
- The worker commits only on `polish-worker`. Claude merges it into `ui-upgrade`. Nobody pushes.
- Each agent updates the status lines of its own tasks in this file.

## Status (update as tasks land)

Batch 1, done on 2026-10-01:
- **Done:** V01, V02, V03 (`Button_icons_are_never_clipped_by_padding_at_any_width`; it failed on the old code), V04 (`IconButton.IsSubtle` defaults to true), V06–V13, V16, V17, V19, V20 and V21.
- **V21 root cause:** an empty document's 1×1 export area was fitted to the viewport, so its sheet shadow framed the whole canvas. The sheet is now skipped while the document is empty. The keyboard ring now uses `Accent.Select` at 1.5 px and appears only for keyboard-originated focus.
- **Partial:**
  - V05: both parts share one style, with a 1 px gap.
  - V14: menu radius 8, 28 px items and a drawn checkmark. Shortcut column and separators were not reviewed.
- **Decided:** V22 is kept as is. The tip appears only on first run and carries the tray information, which the start card lacks.
- **Remaining:**
  - V15: layout-mode SegmentedControl, and a Properties icon instead of the gear.
  - V18: Alignment and Auto/Locked as segmented controls, compact background row.
  - V23: rail overflow cue.
  - V24: content-state renders. The annotation and image inspectors have still never been reviewed visually; do this first.
- **Note for workers:** the in-place canvas text editor deliberately opts out of the themed TextBox style (`MainWindow.xaml.cs`, `Style = new Style(typeof(TextBox))`) to keep renderer fidelity. Classes derived from a control (for example `InspectorSection : Expander`) do not get implicit styles. Use `SetResourceReference(StyleProperty, typeof(Base))`.

## How to work (for any worker)

- Read `docs/HANDOFF.md` §2 and §2a first (PowerShell 5.1, PATH prefix, STA tests, never verify in the same parallel batch as the edit).
- Do tasks in order inside a phase. One commit per phase, with the message `UI polish: <phase> (Vxx–Vyy)`. Run the gate (build plus both test suites) once per phase, not per task.
- After any XAML change, launch once with a temp `SNAGITOPEN_DATA` and confirm the title "Untitled - SnagItOpen". Missing resource keys fail only at runtime.
- Visual check: regenerate the renders (`ShellIntegrationTests`, which write `docs/evidence/shell-*.png`) and look at them before committing. A task is not done until it looks right in Light, Dark and HighContrast.
- Use only tokens (`DynamicResource` / `SetResourceReference`). Never hard-code colours. New keys must be added to all three token files (the parity test enforces this).

## Root causes (why it looks clunky)

1. **Icon-only buttons clip their icons.** The `Button` style has `Padding="8,3"` and a 1 px border, and icon-only buttons are fixed at `Width = 28`. That leaves 10 px for a 16 px icon, so only its left part shows: chevrons look like `\`, and the close X looks like `>`.
2. **Icons are stretched.** `ControlVisuals.Icon` uses `Stretch.Uniform`, so each geometry is scaled to its own bounds and the 16 px grid is lost. Icons then differ in size and weight (an 8×4 chevron is drawn 16×8).
3. **There is no quiet button style.** Every chrome button is a bordered box, which makes the command bar, status bar and panels look like rows of boxes.
4. **The PRD §5.1 control styles were not built.** Expander, ScrollBar, CheckBox, RadioButton, TabControl, Slider, Menu and ListBoxItem use stock templates. The ComboBox arrow is a text `▾`. Inputs are 24 px high, not the specified 28.
5. **The inspector layout is a vertical label-over-control stack** with mixed patterns (NumberBox puts its label inline, AddRow puts it on top), so nothing aligns.

## Phase A: icons and buttons (foundation)

**V01 Icon rendering.** `Controls/ControlVisuals.cs`: `Icon()` uses `Stretch.None` and `SnapsToDevicePixels = true`, centred. Add the optional `size` parameter semantics: for 12 px chevrons, wrap the path in a `Viewbox` of 12. Acceptance: all `Icon.*` render at the same scale; the U04 bounds test still passes.

**V02 Icon-only padding.** `Controls/IconButton.cs`: in `Refresh()`, when `!ShowLabel` (or `Label` is empty), set `Padding = new Thickness(0)`, `Width = Height = 28` unless set explicitly, and centre the content. Remove the now-redundant `Width = 28` in `SplitButton.cs`, `InfoBar.cs`, `SliderRow.cs` and `PreferencesPages.cs`. `ToastHost.cs` close: 24×24 with padding 0.

**V03 Clip regression test.** `tests/SnagItOpen.Windows.Tests`: new STA test. Build `MainWindow` at 1280×800 (reuse the `ShellIntegrationTests` setup), update the layout, walk the visual tree, and for every `Path` inside a `ButtonBase` assert `ActualWidth` ≥ the geometry's bounds width, and that the button's content area (button width − padding − border) ≥ the path's width. Also run it at 640 px width.

**V04 Subtle button styles.** `Themes/Controls.xaml`: add `SubtleButton` (based on Button: `Background=Transparent`, `BorderThickness=0`, hover `Bg.ControlHover`, pressed `Bg.ControlPressed`, radius 4, `Padding="8,4"`, `MinHeight 28`), and `SubtleToggleButton` (the same, checked = `Bg.Selected` with no border). Add a `SubtleButtonStyle` resource key used by `IconButton` through a new `IsSubtle` DP (default false).

**V05 Split button as one unit.** `SplitButton.cs`: one outer `Border` (radius 4) containing the main part, a 1 px inner divider and a 24 px-wide chevron part with a 12 px `Icon.ChevronDown`. Both parts inherit the style (subtle, or primary for Copy). Hover highlights only the part under the mouse. Primary: the divider is `Text.OnAccent` at 30 % opacity.

**V06 Primary disabled state.** `PrimaryButton` disabled: `Background = Bg.Control`, `Foreground = Text.Disabled`, with no opacity fade (today it turns a muddy pink).

## Phase B: control templates (PRD §5.1)

**V07 Inputs 28 px.** TextBox template: `Border` radius 4, `MinHeight 28`, `Padding 6,0`, vertical centre. Focused: border `Accent.Select`, plus a 2 px bottom line (`Accent.Select`). Hover: border `Text.Secondary`. Disabled: `Text.Disabled` foreground. NumberBox inherits this.

**V08 ComboBox.** Height 28, radius 4, `Icon.ChevronDown` path (12 px) instead of `▾`, hover `Bg.ControlHover`, open state rotates or keeps the chevron with border `Accent.Select`. Popup: radius 8, 1 px `Stroke.Control`, `Bg.Surface`, padding 4, items 28 px with radius 4.

**V09 Expander as section header.** A template with a `ToggleButton` header that is full-width and transparent, a 16 px `Icon.ChevronRight` rotated 90° when expanded (instant when animations are off), BodyStrong text, a top `Stroke.Divider` line, a 32 px header height and content margin `0,4,0,8`. No circle. This applies to the inspector, settings and dialogs.

**V10 ScrollBar.** A thin overlay style: 6 px thumb (`Stroke.Control`, radius 3) that widens to 10 px on hover, transparent track, and no arrow buttons. Both orientations.

**V11 CheckBox / RadioButton.** 16 px box with radius 3 (circle for radio), `Stroke.Control` border, and when checked an `Accent.Select` fill with a `Text.OnAccent` `Icon.Check` (radio: an inner dot). Content margin 8. The indeterminate state shows a dash. HighContrast uses the system brushes through the tokens.

**V12 TabControl / TabItem.** Used by the Images/Objects tabs and the settings pages. The header strip is a segmented pill (`Bg.SurfaceAlt` track; the selected tab is `Bg.Surface` with a subtle border and BodyStrong text). There is no stock 3-D tab border.

**V13 Slider.** A 4 px track (`Stroke.Control`), a filled part in `Accent.Select`, and a 16 px round thumb (`Handle.Fill` with an `Accent.Select` stroke).

**V14 Menu, ContextMenu, MenuItem, ListBoxItem, ToolTip.** Menus: `Bg.Surface`, radius 8, item height 28, radius 4 on hover (`Bg.ControlHover`), a right-aligned shortcut in `Text.Secondary`, `Icon.Check` for checked items, and `Stroke.Divider` separators. ListBoxItem: radius 4, hover and selected states from tokens. ToolTip: radius 4, padding `8,4`.

## Phase C: shell layout polish

**V15 Command bar.** `Shell/MainWindow.Upgrade.cs` `CommandButton`: use `IsSubtle = true`. Vertical/Horizontal/Free becomes one `SegmentedControl` (icons; labels at 1100 DIP and wider) bound to the current layout mode, so the active mode is visible (today they are three identical buttons). Properties uses a panel icon (add `Icon.Panel` to `Icons.xaml`) instead of the gear. Divider height is 20 px, centred.

**V16 Status bar.** `MainWindow.xaml:113-123`: one zoom group `[−][100 %][⌄][+]` as subtle buttons around a borderless NumberBox with a `%` suffix. The canvas size is a subtle text button. `StatusIcon` uses `Stretch.None`. Height 28, with no stock `StatusBarItem` padding gaps.

**V17 Inspector rows.** `Editor/InspectorPanel.cs` `AddRow`/`AddNumber`/`AddCheck`, `AnnotationPropertiesPanel.Row`, `ImageEdgePanel.Row`: a single helper `InspectorRow.Create(label, control)` that builds a two-column grid (label 96 DIP, `Text.Secondary`, Caption 12; control *), with a 32 px row height and a 4 px gap. Use it everywhere. NumberBox gets a `ShowLabel=false` mode for use inside rows. Width and height share one row: `W [ ] × H [ ]`.

**V18 Inspector controls.** Alignment becomes a SegmentedControl with Start/Center/End icons (`Icon.AlignLeft/CenterX/Right`, or Top/Middle/Bottom in Horizontal mode). Canvas Auto/Locked becomes a SegmentedControl. Background becomes a compact row: a Transparent toggle plus a 28 px swatch chip and hex. The header `More` button is subtle, 28×28. The header title is Subtitle 14 SemiBold.

**V19 Layers panel.** `MainWindow.xaml:128-174`: the tabs use the V12 style. Up/Down/Hide/Remove become a row of subtle icon buttons (`Icon.Forward`/`Backward`/`EyeOff`/`Trash`) aligned right. The empty state is a centred `Text.Secondary` caption ("Images you add appear here"). Remove the "Layout order" caption when the list is empty.

**V20 Recent captures strip.** When there are no captures, collapse it to a 32 px header with an expand chevron (subtle) and a subtle "Open library" button. Restore the full height on the first capture. Persist the choice in `UiState` if the user toggles it.

**V21 Canvas focus ring and frame.** In `CanvasView`, draw the R-E2.4 ring only when keyboard focus came from the keyboard (track the last input device; for example `InputManager.Current.MostRecentInputDevice is KeyboardDevice`). Make it 1.5 px `Accent.Select`, inset 2. The viewport has no white border at rest.

**V22 Duplicate messaging.** `MainWindow.Upgrade.cs:106-111`: do not show `FirstRunTip` while the start card is visible (`IsEmpty`). Show it after the first content instead, once.

**V23 Tool rail overflow.** The rail ScrollViewer uses the V10 scrollbar, with a fade gradient at the bottom edge when more items are below.

## Phase D: verification and evidence

**V24 Renders and review.** Extend `ShellIntegrationTests` renders: {Light, Dark, HighContrast} × {640, 1280} at 100 %, Dark at 200 %, plus two content states: an image with a selected arrow (annotation inspector visible), and an image selected (image inspector). Save them to `docs/evidence/polish-*.png`. The user approves them, then update `docs/HANDOFF.md` (status, counts) and `docs/evidence/verification.md`.

## Acceptance checklist (whole plan)

- [ ] No clipped icon anywhere (V03 test green; visual check at 100/200 %).
- [ ] Chrome buttons are borderless at rest; only inputs and the primary Copy have visible fills or borders.
- [ ] All inputs and combos are 28 px with radius 4; there are no stock WPF chevrons, circles or 3-D tabs.
- [ ] Inspector labels align in one column across Document, Image and Annotation modes.
- [ ] The empty state uses the canvas: the strip is collapsed and there is no duplicate tip.
- [ ] Light, Dark and HighContrast renders were reviewed; the gate passes with 0 warnings and the test count is at least the baseline plus the new tests.

## Out of scope

Feature changes, U39 Mica, new settings, and changes to export or document behaviour.

---

# Part 2: editing-quality features (F-tasks, requested by the user on 2026-10-01)

Rules: behaviour changes are allowed here. Project schema stays **v2**: new fields are optional and have defaults that reproduce today's rendering, so old files look identical. Every model change gets Core tests, and every renderer change gets a pixel test in Windows.Tests. One commit per task group (`Area: what changed`).

## F-TXT: text and callout boxes (owner: Claude). Highest value.
Problems today: the box never grows when the font, family, weight or padding changes. The text is top-aligned with one uniform padding value, so filled "pill" or label boxes never look centred. FormattedText line height adds space below the glyphs, so even equal padding looks bottom-heavy.
- **TXT1 Sizing mode.** `TextAnnotation.Sizing` = `AutoWidth` (grow to fit the longest line, with no wrapping except at Enter), `AutoHeight` (fixed width, wraps, height follows the text) or `Fixed`. The default for a click-to-place text is AutoWidth; a drag-to-draw box is AutoHeight. Old files load as `Fixed` (the JSON default), so nothing moves. One Core function, `TextLayout.Fit(t, measure)`, recomputes `Bounds` around the anchor, which is the top-left, or the centre when rotated. It runs on every commit that changes text, font, size, bold, italic, padding or sizing, and live while typing (the editor grows with the text).
- **TXT2 Padding X/Y + vertical alignment.** Add `PaddingX` and `PaddingY` (nullable; `null` falls back to `Padding`) and `VerticalAlign` (Top/Middle/Bottom, default Top for old files and Middle for new text). The renderer centres the line box optically: it uses `ft.Extent` and `ft.OverhangLeading` / `ft.Baseline`, so cap-height and descender space are balanced.
- **TXT3 Inspector.** The Text section gets Sizing (a three-icon segmented control), Padding H/V, and vertical alignment next to horizontal alignment. Resizing a box with the handles switches AutoWidth to AutoHeight (width), or to Fixed (height drag).
- **TXT4 Built-in looks.** Gallery presets: "Label" (white on brand red, PaddingX 12, PaddingY 6, radius 6), "Note" (dark on yellow), "Pill" (radius = height/2), "Plain" and "Outline". These use the new fields so they look right immediately.
- **TXT5 Editor fidelity.** The in-place TextBox uses the same padding X/Y and vertical alignment as the renderer, so text does not jump on commit.
- Acceptance: increasing the font size grows the box with no clipping; a filled single-line label has visually equal space above and below its glyphs (a pixel test compares the ink bounds); old projects render unchanged (a golden test).

## F-MAG: magnifier lens (owner: Claude)
Problem today: moving the lens moves `SourceRegion` with it (`Annotation.Offset` maps both), so the zoomed content changes. The user expects the lens and its content to move together, and wants a way to choose what is magnified.
- **MAG1 Move keeps content.** Add a virtual `Annotation.Translate(dx, dy)` that is used for user moves and nudges (`DocumentOps` move, nudge, duplicate and paste). `MagnifierAnnotation` translates only `Bounds`. Document-wide transforms (scale, normalize) keep using `MapGeometry`, which maps both. Resizing the lens keeps the source centre and changes the zoom.
- **MAG2 Source handle.** When a lens is selected, draw its source as a dashed `Accent.Select` rectangle or circle with corner handles and a thin connector to the lens. Dragging inside the source moves only the source, and its handles resize it (the zoom changes). Alt+drag on the lens moves both, which is the old behaviour.
- **MAG3 Inspector.** A Zoom NumberBox (1–8×) resizes the source around its centre. Add Shape (circle/rounded square) and a "Show source outline in export" toggle (default off).
- Acceptance: moving the lens keeps its pixels identical (pixel test); dragging the source changes them; undo is one step each.

## F-STY: saved style gallery (owner: Claude)
Problems today: a click only applies the style or sets the tool default. Tiles cannot be dragged onto the canvas or double-clicked to insert. A saved style cannot be edited. Thumbnails are 56×40 bitmaps rendered at 96 DPI with fixed samples, so text and fill styles look cramped and blurry and don't resemble the result.
- **STY1 Insert.** Double-click or Enter on a tile inserts a new annotation with that style at the centre of the visible canvas, sized by kind (TXT1 auto-size for text), selected, as one undo step. Dragging a tile onto the canvas inserts it at the drop point. The data object carries the `SnagItOpen.Style` id plus a `SnagItOpen.StyleKind` format, and `CanvasView` accepts it. Reordering inside the gallery keeps working.
- **STY2 Edit.** The tile menu gets "Update from selection" (overwrites the saved style with the selected annotation's style, with confirmation), plus Rename, Duplicate and Delete. A hover tooltip shows a large preview.
- **STY3 Thumbnails.** Render at the monitor DPI (`VisualTreeHelper.GetDpi`) into a 64×44 tile. Text styles use the real style (font, weight, fill, padding and radius) on the word "Text" auto-fitted with TXT1. Strokes are scaled to stay proportional to how they look at 100 % on the canvas, clamped 1–6 px. Shapes keep their aspect. The selected tile is shown with a 2 px `Accent.Select` ring.
- Acceptance: a saved style's thumbnail visually matches an inserted sample (a review render in evidence); double-click and drag both insert.

## F-GRP: grouping (owner: Claude, after TXT/MAG/STY)
- **GRP1 Model.** Add an optional `Guid? GroupId` on `Annotation`. A group is the annotations sharing a GroupId (annotations only, in this version: images stay outside groups because annotations are independent of images, per HANDOFF §4).
- **GRP2 Commands.** Group (Ctrl+G) and Ungroup (Ctrl+Shift+G), in the Arrange row, the context menu and the palette.
- **GRP3 Selection.** A click selects the whole group. A double-click (or Ctrl+click) enters the group to select one member. The group shows one dashed outline with handles. Move, resize (scale members about the group bounds), lock, hide, delete and copy/paste act on the whole group. The Objects list shows a group row with an expander.
- **GRP4 Z-order.** Group members stay contiguous; bringing a group forward moves the block.
- Acceptance: Core tests for group, ungroup, z-order contiguity and round-trip; one undo step per command.
- **Open question for the user (ask before GRP1):** should images and annotations be groupable together? That would revisit the HANDOFF §4 decision.

## F-SCR: scrolling capture (owner: Worker)
Problem today: `ScrollingCaptureWindow.FinishAsync` (`App/Capture/SessionWindows.cs` ~l.328) adds every frame as a separate cropped image layer and switches the document to Free mode. The result is loose and can drift apart.
- **SCR1 One stitched image.** On Finish, compose the accepted frames, minus their overlaps, into one `PixelBuffer` in Core (`Stitching/FrameComposer.Compose(frames)`, with a pure test on synthetic buffers). Import it as one asset and deliver it through the same path as other captures (`Vm.AddCapturesAsync(..., destination)`), so it respects the capture destination setting (new composition / append / free / copy) and appears in the recent captures library. Do not switch the layout mode.
- **SCR2 Session panel.** Restyle it with the themed controls: NumberBoxes for header/footer rows, Auto scroll as the primary action, Capture next as secondary, Finish as primary once there are 2 or more frames, and Cancel as subtle. Add a live thumbnail strip of the stitched result (the right edge, scaled) and a progress line ("5 frames · 4,210 px").
- **SCR3 Robustness.** Exclude the sticky header and footer automatically: on the second frame, detect rows that are identical in both frames at the top and bottom, and prefill them. Stop automatically when the frame is unchanged twice. A per-seam "Fix seam…" option remains via the existing `SeamDialog` before Finish.
- **SCR4 Tests.** Composer (overlap 0, overlap = height − 1, differing widths rejected), sticky-row detection, and a Finish test that one image layer is added.
- Acceptance: scrolling-capture a long web page → one image in the editor, with no visible seams at the tested overlaps.

## Execution order
1. Claude: TXT1–TXT5, then MAG1–MAG3, then STY1–STY3, then GRP (after the user answers the open question).
2. Worker, in parallel: SCR1–SCR4, then V15, V18, V23, then V24 renders (which include TXT/MAG results once merged).

## F status
- TXT: not started · MAG: not started · STY: not started · GRP: waiting for the user's answer · SCR: not started (worker)
