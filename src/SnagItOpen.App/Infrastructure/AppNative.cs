using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.App.Infrastructure;

/// <summary>Small window-management interop used only by the App (placement in physical pixels, capture exclusion).</summary>
internal static class AppNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();
    public static PixelRect? WindowBounds(Window w)
    {
        var handle = new WindowInteropHelper(w).Handle;
        return handle != IntPtr.Zero && GetWindowRect(handle, out var r) && r.Right > r.Left && r.Bottom > r.Top ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
    }
    public static void PlaceWindow(Window w, PixelRect physical) => SetWindowPos(Handle(w), IntPtr.Zero, physical.X, physical.Y, physical.Width, physical.Height, SWP_NOACTIVATE | 0x0004);

    /// <summary>Places a window at exact physical desktop pixels, topmost.</summary>
    public static void PlaceTopmost(Window w, PixelRect physical, bool activate = true) =>
        SetWindowPos(Handle(w), HWND_TOPMOST, physical.X, physical.Y, physical.Width, physical.Height,
            SWP_SHOWWINDOW | (activate ? 0u : SWP_NOACTIVATE));

    public static PixelPoint CursorPosition() => GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : new PixelPoint(0, 0);

    public static void MoveCursor(PixelPoint p) => SetCursorPos(p.X, p.Y);

    /// <summary>Makes the window ignore mouse input (click-through) and hide from Alt+Tab.</summary>
    public static void MakeClickThrough(Window w)
    {
        var h = Handle(w);
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    public static void MakeToolWindow(Window w)
    {
        var h = Handle(w);
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    /// <summary>Excludes a window from screen capture (Windows 10 2004+). Returns false when unsupported.</summary>
    public static bool ExcludeFromCapture(Window w)
    {
        try { return SetWindowDisplayAffinity(Handle(w), WDA_EXCLUDEFROMCAPTURE); }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static void Activate(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
    }

    /// <summary>Window never takes activation (so open menus in other apps stay open) and hides from Alt+Tab.</summary>
    public static void MakeNoActivate(Window w)
    {
        var h = Handle(w);
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static extern int DwmFlushImport();

    /// <summary>Waits for the compositor to present the next frame (best effort).</summary>
    public static void DwmFlush()
    {
        try { DwmFlushImport(); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
