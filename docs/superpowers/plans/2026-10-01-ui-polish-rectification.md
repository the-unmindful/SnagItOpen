# UI polish rectification plan (2026-10-01)

Based on a review of the 0.2.0 `ui-upgrade` branch (`ae40a25`) against the PRD (`docs/superpowers/specs/2026-09-30-ui-ux-upgrade-prd.md` sections 4-7) and the theme render `docs/evidence/shell-Dark-1280-100.png`. The PRD features exist, but the visual layer is only partially built, so the app still looks like stock WPF with borders.

## Root causes

1. **Icon-only buttons clip their icons.** The implicit `Button` style (`Themes/Controls.xaml:102`) uses `Padding="8,3"` and a 1 px border. Icon-only buttons are fixed at `Width = 28` (`SplitButton.cs:21`, `InfoBar.cs:32`, `SliderRow.cs:15`). That leaves 28 − 16 − 2 = **10 px for a 16 px icon**, so only the left part of the icon shows: the Capture and Copy chevrons appear as `\`, and the InfoBar close X appears as `>`. Toast close (`Width = 24`) is worse.
2. **Icons are stretched.** `ControlVisuals.Icon` uses `Stretch.Uniform`, so each geometry is scaled to its own bounds instead of keeping the 16×16 grid. A chevron (8×4) is drawn twice as large as intended, and the effective stroke weight differs from icon to icon. Use `Stretch.None` (the geometries are already authored on the 16 px grid, as the U04 test confirms).
3. **PRD 5.1 control styles are mostly missing.** Only Button, ToggleButton, ComboBox (with a text `▾`), TextBox colours and a few colour setters exist. Expander (the stock circle-chevron headers in the inspector), ScrollBar (light grey in dark mode), CheckBox, RadioButton, TabControl/TabItem, Slider, Menu/ContextMenu and ListBoxItem still use stock templates.
4. **There is no "subtle" button style.** Every command-bar, status-bar and panel button has a full border, which gives the heavy "row of boxes" look. Fluent-style chrome uses borderless buttons that fill only on hover or press. The PRD implies this (R-E1.1, principle 1 "chrome is quiet") but no style was created.

## Visible gaps (from the render)

| # | Gap | PRD ref |
|---|---|---|
| G1 | Capture/Copy chevrons, InfoBar/Toast close clipped (cause 1) | 5.5, 5.6 |
| G2 | Command bar: bordered boxes, label+icon buttons look cramped; disabled primary Copy turns muddy pink | R-E1.1, R-E1.3 |
| G3 | Inspector sections use stock Expander circles; label column widths vary (Gap/Padding/Width/Height boxes start at different x) | R-E3.1, 5.6 InspectorSection |
| G4 | Inspector: Alignment is a ComboBox, not a 3-icon SegmentedControl; Canvas Auto/Locked are radio buttons, not segmented; full-width background swatch | R-E3.1 |
| G5 | Header `More` button oversized, bordered | R-E3.2 |
| G6 | Status bar: zoom shows `100` without `%`; separate tiny `▾` button; canvas-size shown as a bordered button | R-E1.7 |
| G7 | ComboBox arrow is a text glyph; TextBox/ComboBox heights (≈24) differ from `Height.Control` 28; no corner radius on inputs | 5.3 |
| G8 | Inspector scrollbar is the stock light scrollbar in Dark theme | 5.1 |
| G9 | Images/Objects tabs stock; Up/Down/Hide/Remove are cramped text buttons; large empty "Layout order" area | R-E1.8, R-E3.6 |
| G10 | Empty "Recent captures" strip takes about 120 px of canvas height | R-E10, R-E6 "canvas keeps space" |
| G11 | Bright 2 px white ring around the canvas viewport (focus ring visible in a mouse/idle state, or too harsh) | R-E2.4 |
| G12 | "Capture from anywhere" InfoBar duplicates the start card's "Set shortcut" (two homes) | principle 3 |
| G13 | Tool rail's last item cut off at the bottom with no scroll cue | R-E1.4 |

## Phases (each phase = one commit + gate)

**P1 Icon clipping and icon scale (small, highest impact): G1**
- `ControlVisuals.Icon`: `Stretch.None`, centre-aligned.
- `IconButton`: when `ShowLabel` is false, set `Padding = 4`, `Width/Height = 28` (24 for toast), `HorizontalContentAlignment = Center`. Remove the per-call `Width = 28` overrides, or keep them now that padding fits.
- SplitButton chevron: 12 px chevron icon with `Padding = 4,0`, joined visually to the main part (shared border, divider).
- Test (Windows.Tests, STA): build the shell, walk the visual tree, and for every `Path` inside a `ButtonBase` assert `LayoutInformation.GetLayoutClip(path)` is null and the rendered size equals 16 (or 12 for chevrons). This guards against regressions in every theme and at 200%.

**P2 Control templates (PRD 5.1 completion): G2, G3, G7, G8**
- `SubtleButton` / `SubtleToggleButton` styles: transparent, no border, `Bg.ControlHover`/`Pressed` fill, radius 4. Apply them to the command bar, status bar, inspector header and panel buttons. Primary Copy keeps the brand style; its disabled state uses `Bg.Control` with `Text.Disabled`, not faded red.
- ComboBox: `Icon.ChevronDown` path, height 28, radius 4, hover state.
- TextBox: template with radius 4, height 28, focus as a 2 px bottom `Accent.Select` line (Fluent) or the full border.
- Expander: section-header template with a ChevronRight/ChevronDown icon, `BodyStrong` text and a `Stroke.Divider` top line, without the circle.
- ScrollBar: thin themed (`Stroke.Control` thumb, widens on hover).
- CheckBox, RadioButton, TabControl/TabItem (as segmented pills), Slider (themed track/thumb), ListBoxItem, Menu/MenuItem/ContextMenu (check against the render).

**P3 Layout polish: G4, G5, G6, G9-G13**
- Inspector rows: one shared label column (`Grid.IsSharedSizeScope` or a fixed 96 DIP label column) for every row builder.
- Alignment uses the 3-icon SegmentedControl. Auto/Locked use a SegmentedControl. The background swatch becomes a compact chip and hex next to the Transparent toggle.
- Header `More` becomes a 28 px subtle icon button.
- Status bar: subtle `[−] [100% ⌄] [+]` as one grouped control, `%` suffix, and canvas size as a subtle text button.
- Layers: TabControl styled as segmented; Up/Down/Hide/Remove as icon buttons; an empty-state text instead of a bare list.
- Recent captures strip: collapsed to a 32 px header when there are no captures (or by default until the first capture).
- Canvas focus ring: only on keyboard focus (`IsKeyboardFocused` and the last input was from the keyboard); use `Accent.Select` at 1.5 px.
- Hide the "Capture from anywhere" InfoBar while the start card is visible.
- Rail: themed thin scrollbar or fade cue when it overflows.

**P4 Visual verification**
- Regenerate the renders in `ShellIntegrationTests` for Light/Dark/HighContrast at 640/800/1280 and 100/200 %, plus a render with an image and a selected annotation (the current renders show only the empty state, so the inspector annotation mode and the canvas adorners were never visually reviewed).
- The user reviews the renders before the final commit. Update `docs/HANDOFF.md`.

## Out of scope
Feature changes, new PRD tasks, U39 (Mica). Visual-only changes, with no change to behaviour, export pixels or settings.

## Estimate
P1 is small (3 files and a test). P2 is the largest (`Controls.xaml`). P3 is medium (`InspectorPanel`, `MainWindow.xaml`, status bar). Recommended order: P1, then P2, then P3, then P4. P1 alone removes the most visible defect.
