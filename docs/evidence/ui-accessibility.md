# UI and accessibility evidence — 0.2.0 preview

Recorded 2026-10-01 on Windows 11, .NET SDK 10.0.401. These are application-level WPF renders and regression tests, not screenshots of a completed live desktop walkthrough.

## Verified automatically

- Shell controls load in Light, Dark and HighContrast token dictionaries at 1280, 1000, 800 and 640 DIP widths. Every visible focusable control has an automation name.
- RenderTargetBitmap evidence uses 96, 144 and 192 DPI (100%, 150%, 200%). This verifies rendering at those resolutions; it does **not** verify the Windows text-size setting, per-monitor transitions or every custom High contrast palette.
- Focus traversal regression covers Escape clearing the canvas selection, then Tab leaving the canvas. Rail/segmented/gallery navigation, slider commits, settings Cancel, invalid numeric input and dialog validation have focused STA coverage.
- Native-window tests verify desktop toasts do not activate or change the foreground window, are excluded from capture, and stay within the monitor work area. Pin tests verify native click-through, passive recovery-tab styles, minimize/restore recovery and zoom persistence.
- Theme dictionaries have matching keys. `Text.Selected` preserves ordinary Light/Dark text and uses system HighlightText for highlighted High contrast controls. Primary-button hover/press retains its brand background.

## Visual review

Light, Dark and HighContrast renders were inspected. Review corrected dark text on dark surfaces, native light menu/combo surfaces, a rail scrollbar clipping icons, truncated image-list actions and start-card text shrinking to fit. The start card now keeps its text size and scrolls when space is limited; narrow layouts collapse the left panel and move Properties into a flyout.

Representative final renders:

- [Dark, 1280 DIP / 100%](shell-Dark-1280-100.png)
- [Light, 800 DIP / 100%](shell-Light-800-100.png)
- [HighContrast, 640 DIP / 100%](shell-HighContrast-640-100.png)
- [Dark, 800 DIP / 200%](shell-Dark-800-200.png)

Generated images omit the OS window frame. Tests deliberately disable global hotkeys and the tray, so shortcut labels show “Not set”.

## Still open on a real desktop

- Windows 200% text size, Narrator announcements, a complete keyboard-only workflow and arbitrary system High contrast palettes.
- Capture at 100%/150% display scaling, mixed-DPI/negative-origin/portrait monitors, monitor disconnect and capture exclusion observed in actual output.
- Drag to Explorer/chat apps, clipboard file paste, IME text entry, global hotkeys while hidden, tray interaction and real scrolling targets.
- Performance measurements and a clean Windows machine without an installed SDK.

The computer-use tool reported “Computer Use was not approved to use SnagItOpen” when reading its window. No live UI automation was performed afterward. These remaining items are listed explicitly in `docs/ACCEPTANCE.md` and are not marked passed.
