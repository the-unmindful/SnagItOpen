using System.Runtime.InteropServices;
using SnagItOpen.Core.Geometry;
using static SnagItOpen.Windows.Native.NativeMethods;

namespace SnagItOpen.Windows.Capture;

/// <summary>Sends scroll input for automatic scrolling capture; fakeable in tests.</summary>
public interface IScrollInput
{
    /// <summary>Scrolls down by wheel notches (negative = up) at a physical point. Returns false if the target lost focus.</summary>
    bool ScrollDown(PixelPoint at, int notches, IntPtr expectedForeground);
}

/// <summary>
/// Bounded mouse-wheel injection into the selected foreground target only. Refuses to send input when
/// the foreground window changed, so a scroll session never scrolls an unrelated app.
/// </summary>
public sealed class ScrollInputService : IScrollInput
{
    private const int WheelDelta = 120;

    public bool ScrollDown(PixelPoint at, int notches, IntPtr expectedForeground)
    {
        notches = Math.Clamp(notches, -20, 20); // negative scrolls up
        if (notches == 0) return true;
        if (expectedForeground != IntPtr.Zero)
        {
            var fg = GetForegroundWindow();
            if (fg != expectedForeground && GetAncestor(fg, GA_ROOT) != expectedForeground) return false;
        }
        GetCursorPos(out var restore);
        SetCursorPos(at.X, at.Y);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = -WheelDelta * notches },
        };
        uint sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        SetCursorPos(restore.X, restore.Y);
        return sent == 1;
    }

    public static bool Activate(IntPtr hwnd) => hwnd != IntPtr.Zero && SetForegroundWindow(hwnd);
}
