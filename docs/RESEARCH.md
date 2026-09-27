# Research and technology decision

Research date: 2026-09-27. Sources below are vendor documentation or original repositories. Recommendations, scope choices, budgets, and task estimates are engineering judgments, not measured results.

## What to reproduce

Snagit offers region/full-screen/scrolling capture, annotation, and image combination through templates or direct canvas placement. The useful target here is that workflow, with particular emphasis on fast combining and custom placement. Snagit's source code, proprietary project format, branding, video recorder, and cloud services are outside this project. [Snagit combine images](https://www.techsmith.com/snagit/features/combine-images/) · [Snagit features](https://www.techsmith.com/snagit/features/)

ShareX already provides many capture modes, image editing, an image combiner, and scrolling capture. It is the strongest existing baseline to try before investing in a full custom tool. Its scrolling documentation explicitly describes imperfect matches and problems caused by static elements; reliable scrolling is a separate engineering project. [ShareX features](https://getsharex.com/) · [ShareX scrolling documentation](https://getsharex.com/docs/scrolling-screenshot)

Three different operations are often called stitching:

| Operation | Inputs and result | Design choice |
|---|---|---|
| Combine | Independent images laid out vertically/horizontally | Main workflow, deterministic geometry |
| Compose | Arbitrary image placement, crop, resize, reorder | Main workflow, editable layers |
| Overlap join | Screenshots with repeated content aligned into a continuous image | Later feature, manual control first |

General photograph panoramas, perspective correction, and 360-degree stitching are excluded. Document screenshots should stay sharp, with no automatic perspective warping or seam blending.

## Alternatives evaluated

| Approach | Advantages | Costs and risks | Verdict |
|---|---|---|---|
| New C#/.NET WPF application | One language; direct Windows integration; mature controls and image APIs; pure geometry can be tested independently | Must implement editor interactions and capture lifecycle; Windows-only | **Recommended for this custom Windows tool** |
| Extend/fork ShareX | Broad capture functionality already exists; useful reference implementation | Large existing application/context for a small model; custom editor competes with existing architecture; license terms need to remain part of reuse decisions | Best if broad existing capture functionality matters more than a tailored stitching editor |
| Electron + TypeScript UI | Familiar web UI ecosystem; desktop capture API; potentially portable UI | Main/renderer boundary plus Windows-specific integration; mixed-DPI and native capture still need careful work | Reasonable if the maintainer strongly prefers TypeScript, otherwise unnecessary here |

WPF is a Windows-only .NET UI framework supporting XAML, data binding, controls, and graphics. Electron exposes display/window capture through its desktop capture API. The recommendation is an inference from the Windows-only target and the desire to keep implementation tasks small. [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) · [Electron desktopCapturer](https://www.electronjs.org/docs/latest/api/desktop-capturer)

The original ShareX repository contains a GNU GPL license. Read the exact license before copying source or distributing a derivative. The proposed implementation uses documented Windows APIs and independently written code; it does not depend on copied ShareX modules. [ShareX license](https://github.com/ShareX/ShareX/blob/master/LICENSE.txt)

## Stack decision

| Area | Initial choice | Reason |
|---|---|---|
| Runtime | .NET 10 LTS, supported servicing patch | Current long-lived runtime; avoid starting on a near-end-of-support line |
| UI | WPF, conventional MVVM with small view models | Native controls and predictable Windows behavior |
| Rendering/codecs | WPF `DrawingContext`, `BitmapSource`, `RenderTargetBitmap`, PNG/JPEG encoders | Same drawing code for canvas and export; no third-party raster engine at first |
| Geometry/domain | Plain .NET immutable records | Tests require no desktop, HWND, or WPF objects |
| Capture v1 | Win32 GDI desktop snapshot + physical-pixel crop | Small still-image capture implementation with explicit limitations |
| Capture upgrade | Windows.Graphics.Capture, only after a compatibility spike | Modern frame capture; more Direct3D/WinRT lifecycle complexity |
| Storage | Versioned JSON and normalized PNG assets in ZIP project | Inspectable model, no database/server needed |
| Tests | xUnit + dedicated STA test helper for Windows imaging | Separate deterministic algorithms from interactive desktop tests |
| Packaging | Self-contained `win-x64` folder ZIP | Simple local use, no installer framework or admin requirement |

.NET 10 is listed as LTS with support through November 2028. Resolve and record an actual installed SDK version in `global.json` during setup. Do not put floating NuGet versions into the build. [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)

WPF can render a visual into a `RenderTargetBitmap`; thread-affine objects must be created/used on their owning thread, and frozen shareable image resources may cross threads. Use one dedicated STA dispatcher for background imaging and export, not arbitrary `Task.Run` calls over WPF objects. [RenderTargetBitmap API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.rendertargetbitmap?view=windowsdesktop-10.0)

## Windows capture decisions and limitations

1. **Screen pixels and UI units differ.** WPF uses device-independent units. Capture uses physical desktop pixels. Per-Monitor V2 awareness and explicit conversions are required on mixed-scale displays. Test negative monitor origins and a selection crossing two displays. [Microsoft high-DPI guide](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)
2. **GDI is a bounded first implementation.** `BitBlt` transfers pixels between device contexts; `CAPTUREBLT` can include layered windows. It is useful for visible desktop snapshots, but this plan does not promise capture of protected content, minimized windows, or color-correct HDR output. Label the v1 window mode as visible window area. [BitBlt reference](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt)
3. **Do not make PrintWindow the universal fallback.** Microsoft documents it as synchronous and dependent on the target application rendering the image. It can stall and may not provide the desired result. [PrintWindow reference](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)
4. **Modern capture needs a separate integration spike.** Windows.Graphics.Capture acquires window/display frames. Check support, follow the documented desktop/picker interop path, manage frame lifetime and device loss, and verify HDR behavior. It is not necessary for the requested fast combiner. [Microsoft screen capture guide](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
5. **Hotkeys can conflict.** `RegisterHotKey` failure is an expected settings state. Keep UI capture available and let users choose another shortcut; never silently overwrite system behavior. [RegisterHotKey reference](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
6. **Automated scrolling is bounded.** `SendInput` is subject to Windows integrity-level restrictions. Only scroll the explicitly selected foreground target during an active session; stop when focus changes. Manual scroll-and-capture remains available. [SendInput reference](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

## Deliberately avoided in the initial build

- OpenCV and feature-based panorama stitching: unnecessary for stacking and free placement.
- Multiple raster libraries: increases conversions, native deployment, and preview/export mismatch.
- Browser extensions: useful for full-page browser capture later, but a separate install and permission surface.
- SQLite: recent-capture metadata is small enough for an atomic JSON index initially.
- OCR, AI text replacement, video, GIF, cloud uploads, plug-ins, and paid libraries: none are required for the confirmed workflow.

## Research boundaries

The comparison is documentation-based. Snagit, ShareX, capture backends, and performance targets were not benchmarked on this machine. Source pages can change after this research date. The plan's release gates require real Windows tests before claims about capture coverage, speed, or reliability.
