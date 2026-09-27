using System.Runtime.InteropServices;
using System.Text;
using SnagItOpen.Core.Geometry;
using static SnagItOpen.Windows.Native.NativeMethods;

namespace SnagItOpen.Windows.Capture;

/// <summary>A top-level window snapshot. Bounds are physical pixels (DWM frame bounds where available).</summary>
public sealed record WindowInfo(
    IntPtr Handle, string Title, string ClassName, PixelRect Bounds, uint ProcessId,
    bool IsVisible, bool IsMinimized, bool IsCloaked, bool IsToolWindow);

/// <summary>Pure eligibility rules for visible-window capture (testable with fake catalogs).</summary>
public static class WindowFilter
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>Keeps capturable windows in z-order and clips their bounds to the desktop.</summary>
    public static IReadOnlyList<WindowInfo> Eligible(IEnumerable<WindowInfo> zOrdered, uint ownProcessId, PixelRect desktop)
    {
        var list = new List<WindowInfo>();
        foreach (var w in zOrdered)
        {
            if (!w.IsVisible || w.IsMinimized || w.IsCloaked || w.IsToolWindow) continue;
            if (w.ProcessId == ownProcessId) continue;
            if (ExcludedClasses.Contains(w.ClassName)) continue;
            if (string.IsNullOrWhiteSpace(w.Title)) continue;
            var clipped = w.Bounds.Intersect(desktop);
            if (clipped.Width < 2 || clipped.Height < 2) continue;
            list.Add(w with { Bounds = clipped });
        }
        return list;
    }

    /// <summary>Topmost eligible window containing a physical point.</summary>
    public static WindowInfo? At(IReadOnlyList<WindowInfo> eligibleZOrdered, PixelPoint p) =>
        eligibleZOrdered.FirstOrDefault(w => w.Bounds.Contains(p.X, p.Y));
}

/// <summary>Enumerates top-level windows in z-order (topmost first).</summary>
public sealed class WindowCatalog
{
    public IReadOnlyList<WindowInfo> Enumerate()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (Describe(h) is { } w) list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public IReadOnlyList<WindowInfo> Eligible(PixelRect desktop) =>
        WindowFilter.Eligible(Enumerate(), (uint)Environment.ProcessId, desktop);

    public static IntPtr Foreground() => GetForegroundWindow();

    public static bool Exists(IntPtr h) => h != IntPtr.Zero && IsWindow(h);

    /// <summary>Root window under a physical point.</summary>
    public static IntPtr RootAt(PixelPoint p)
    {
        var h = WindowFromPoint(new POINT(p.X, p.Y));
        return h == IntPtr.Zero ? h : GetAncestor(h, GA_ROOT);
    }

    public static WindowInfo? Describe(IntPtr h)
    {
        if (!IsWindow(h)) return null;
        int len = GetWindowTextLength(h);
        var sb = new StringBuilder(Math.Clamp(len + 1, 1, 1024));
        GetWindowText(h, sb, sb.Capacity);
        var cls = new StringBuilder(256);
        GetClassName(h, cls, cls.Capacity);
        GetWindowThreadProcessId(h, out var pid);
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        bool cloaked = false;
        try { cloaked = DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0; }
        catch (DllNotFoundException) { }
        return new WindowInfo(h, sb.ToString(), cls.ToString(), FrameBounds(h), pid,
            IsWindowVisible(h), IsIconic(h), cloaked, (ex & WS_EX_TOOLWINDOW) != 0);
    }

    /// <summary>DWM extended frame bounds (excludes invisible resize borders); falls back to GetWindowRect.</summary>
    public static PixelRect FrameBounds(IntPtr h)
    {
        try
        {
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0 && r.Height > 0)
                return MonitorService.ToRect(r);
        }
        catch (DllNotFoundException) { }
        return GetWindowRect(h, out var wr) ? MonitorService.ToRect(wr) : default;
    }
}
