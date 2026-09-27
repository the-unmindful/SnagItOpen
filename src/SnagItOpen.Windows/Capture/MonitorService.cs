using System.Runtime.InteropServices;
using Microsoft.Win32;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;
using static SnagItOpen.Windows.Native.NativeMethods;

namespace SnagItOpen.Windows.Capture;

/// <summary>
/// Enumerates monitors in physical desktop pixels (requires a Per-Monitor-V2 aware process for exact
/// values) and raises <see cref="TopologyChanged"/> when displays change.
/// </summary>
public sealed class MonitorService : IDisposable
{
    public MonitorService()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    public event EventHandler? TopologyChanged;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (!GetMonitorInfo(h, ref info)) return true;
            uint dx = 96, dy = 96;
            try { if (GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var x, out var y) == 0) { dx = x; dy = y; } }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            list.Add(new MonitorInfo(
                string.IsNullOrEmpty(info.szDevice) ? $"monitor{list.Count}" : info.szDevice,
                ToRect(info.rcMonitor), ToRect(info.rcWork), (int)dx, (int)dy,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public string Fingerprint() => MonitorTopology.Fingerprint(GetMonitors());

    public static PixelPoint CursorPosition() =>
        GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : new PixelPoint(0, 0);

    internal static PixelRect ToRect(RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    private void OnDisplayChanged(object? sender, EventArgs e) => TopologyChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
}
