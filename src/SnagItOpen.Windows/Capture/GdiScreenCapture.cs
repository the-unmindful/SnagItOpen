using System.Diagnostics;
using System.Runtime.InteropServices;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using static SnagItOpen.Windows.Native.NativeMethods;

namespace SnagItOpen.Windows.Capture;

/// <summary>
/// Captured desktop pixels in physical coordinates. Pixels are straight BGRA packed as
/// A&lt;&lt;24|R&lt;&lt;16|G&lt;&lt;8|B (same layout as Imaging.PixelBuffer). Display gaps are transparent.
/// </summary>
public sealed class CapturedFrame
{
    public CapturedFrame(PixelRect bounds, uint[] pixels)
    {
        if (bounds.IsEmpty || pixels.LongLength != bounds.Area) throw new ArgumentException("Frame size mismatch.");
        Bounds = bounds;
        Pixels = pixels;
    }

    /// <summary>Physical desktop rectangle covered by this frame.</summary>
    public PixelRect Bounds { get; }
    public uint[] Pixels { get; }
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;

    /// <summary>Crops to a physical desktop rectangle (clipped to the frame).</summary>
    public CapturedFrame Crop(PixelRect physical)
    {
        var r = physical.Intersect(Bounds);
        if (r.IsEmpty) throw new ArgumentOutOfRangeException(nameof(physical), "Selection is outside the captured desktop.");
        var o = new uint[r.Area];
        for (int y = 0; y < r.Height; y++)
            Array.Copy(Pixels, (r.Y - Bounds.Y + y) * Width + (r.X - Bounds.X), o, y * r.Width, r.Width);
        return new CapturedFrame(r, o);
    }
}

/// <summary>Pure buffer conversions used by GDI capture (unit-testable without a screen).</summary>
public static class CaptureBuffers
{
    /// <summary>Converts 32-bit BGRX rows (any stride, top-down or bottom-up) into packed pixels with alpha 255.</summary>
    public static uint[] FromBgrx(ReadOnlySpan<byte> src, int width, int height, int stride, bool bottomUp)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || src.Length < (long)stride * (height - 1) + width * 4)
            throw new ArgumentException("Buffer does not match dimensions.");
        var o = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            int sy = bottomUp ? height - 1 - y : y;
            var row = MemoryMarshal.Cast<byte, uint>(src.Slice(sy * stride, width * 4));
            for (int x = 0; x < width; x++) o[y * width + x] = row[x] | 0xFF000000u;
        }
        return o;
    }

    /// <summary>
    /// Makes pixels on a monitor opaque and pixels in gaps between displays fully transparent (0).
    /// GDI leaves the fourth byte undefined, so alpha is always recomputed.
    /// </summary>
    public static void ApplyMonitorCoverage(uint[] px, PixelRect bounds, IEnumerable<PixelRect> monitors)
    {
        for (int i = 0; i < px.Length; i++) px[i] &= 0x00FFFFFFu;
        foreach (var m in monitors)
        {
            var r = m.Intersect(bounds);
            if (r.IsEmpty) continue;
            for (int y = r.Y; y < r.Bottom; y++)
            {
                int row = (y - bounds.Y) * bounds.Width + (r.X - bounds.X);
                for (int x = 0; x < r.Width; x++) px[row + x] |= 0xFF000000u;
            }
        }
        for (int i = 0; i < px.Length; i++) if ((px[i] >> 24) == 0) px[i] = 0;
    }
}

/// <summary>
/// GDI BitBlt snapshot backend. Captures what is visible on the desktop (occlusion included).
/// Every DC/bitmap is released in <c>finally</c>; selected objects are restored first.
/// Protected content may appear black; no bypass is attempted.
/// </summary>
public static class GdiScreenCapture
{
    public static CapturedFrame Capture(PixelRect physical, IReadOnlyList<MonitorInfo> monitors, bool includeCursor)
    {
        if (physical.IsEmpty) throw new ArgumentOutOfRangeException(nameof(physical), "Capture area is empty.");
        if (!Limits.IsAcceptableImageSize(physical.Width, physical.Height))
            throw new InvalidOperationException($"Capture area {physical.Width}×{physical.Height} exceeds limits.");

        int w = physical.Width, h = physical.Height;
        IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new InvalidOperationException("Screen device context is unavailable.");
            mem = CreateCompatibleDC(screen);
            if (mem == IntPtr.Zero) throw new InvalidOperationException("Could not create a memory device context.");
            var bi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };
            bmp = CreateDIBSection(screen, ref bi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate the capture bitmap.");
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, screen, physical.X, physical.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException($"Screen copy failed (error {Marshal.GetLastWin32Error()}).");
            if (includeCursor) DrawCursor(mem, physical);
            GdiFlush();

            uint[] px;
            unsafe
            {
                px = CaptureBuffers.FromBgrx(new ReadOnlySpan<byte>((void*)bits, checked(w * h * 4)), w, h, w * 4, bottomUp: false);
            }
            CaptureBuffers.ApplyMonitorCoverage(px, physical, monitors.Select(m => m.Bounds));
            return new CapturedFrame(physical, px);
        }
        finally
        {
            if (old != IntPtr.Zero && mem != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Draws the current cursor at its hotspot-adjusted position, clipped by the bitmap.</summary>
    private static void DrawCursor(IntPtr hdc, PixelRect origin)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        DrawIconEx(hdc, ci.ptScreenPos.X - hx - origin.X, ci.ptScreenPos.Y - hy - origin.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    /// <summary>Current process GDI object count (resource leak diagnostics).</summary>
    public static int GdiObjectCount()
    {
        using var p = Process.GetCurrentProcess();
        return (int)GetGuiResources(p.Handle, GR_GDIOBJECTS);
    }
}
