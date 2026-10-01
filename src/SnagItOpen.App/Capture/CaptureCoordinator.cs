using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Imaging.Effects;
using SnagItOpen.Windows.Capture;

namespace SnagItOpen.App.Capture;

/// <summary>One captured image with the physical desktop rectangle it came from.</summary>
public sealed record CaptureItem(PixelBuffer Pixels, PixelRect Bounds);

public sealed record CaptureOutcome(CaptureStatus Status, IReadOnlyList<CaptureItem> Items, string? Message = null,
    CaptureOverlayAction Action = CaptureOverlayAction.Edit)
{
    public static CaptureOutcome Canceled() => new(CaptureStatus.Canceled, []);
    public static CaptureOutcome Fail(string message) => new(CaptureStatus.Failed, [], message);
}

/// <summary>
/// Runs one capture session: remember foreground target → hide app windows → wait for the compositor →
/// optional countdown → freeze the desktop → (interactive) per-monitor selection overlays → crop/mask →
/// restore windows in <c>finally</c>. Only one session may run at a time. Never changes the document.
/// </summary>
public sealed class CaptureCoordinator
{
    private readonly AppServices _services;
    private bool _busy;

    public CaptureCoordinator(AppServices services) => _services = services;

    public bool IsBusy => _busy;

    public Task<CaptureOutcome> CaptureAsync(CaptureMode mode, CaptureOptions options, CancellationToken ct = default) =>
        CaptureCoreAsync(mode, options, CapturePicker.None, ct);

    /// <summary>Opens the nonmodal physical-size or aspect presets inside the frozen overlay.</summary>
    public Task<CaptureOutcome> CaptureWithPickerAsync(bool aspect, CaptureOptions options, CancellationToken ct = default) =>
        CaptureCoreAsync(CaptureMode.Region, options, aspect ? CapturePicker.Aspect : CapturePicker.Size, ct);

    private async Task<CaptureOutcome> CaptureCoreAsync(CaptureMode mode, CaptureOptions options, CapturePicker picker, CancellationToken ct)
    {
        if (_busy) return CaptureOutcome.Fail("A capture is already in progress.");
        _busy = true;
        var foreground = WindowCatalog.Foreground();
        var hidden = HideAppWindows();
        try
        {
            await SettleAsync(ct);
            if (options.DelaySeconds > 0 && !await CountdownAsync(options.DelaySeconds, ct)) return CaptureOutcome.Canceled();

            var monitors = _services.Monitors.GetMonitors();
            if (monitors.Count == 0) return CaptureOutcome.Fail("No displays were found.");
            var desktop = MonitorTopology.VirtualBounds(monitors);
            var fingerprint = MonitorTopology.Fingerprint(monitors);
            string? notice = null;

            // Non-interactive modes.
            PixelRect? direct = mode switch
            {
                CaptureMode.AllMonitors => desktop,
                CaptureMode.Monitor or CaptureMode.CurrentMonitor =>
                    (MonitorTopology.At(monitors, AppNative.CursorPosition()) ?? monitors.First(m => m.IsPrimary) ?? monitors[0]).Bounds,
                CaptureMode.LastRegion => LastRegionStoreResolve(fingerprint),
                _ => null,
            };
            if (mode == CaptureMode.LastRegion && direct is null)
            {
                notice = "The last region is unavailable or the display layout changed. Select a new region.";
                mode = CaptureMode.Region;
            }
            if (direct is { } d)
            {
                var f = await Task.Run(() => GdiScreenCapture.Capture(d, monitors, options.IncludeCursor), ct);
                return Ok([new CaptureItem(ToBuffer(f), d)]);
            }

            // Interactive: freeze the whole desktop first so overlays never appear in the output.
            var catalog = _services.Windows.Eligible(desktop);
            var windows = catalog.Select(w => w.Bounds).ToList();
            if (mode == CaptureMode.Window && foreground != IntPtr.Zero && WindowCatalog.Describe(foreground) is { } fw
                && fw.ProcessId != (uint)Environment.ProcessId && !fw.IsMinimized)
            {
                // Put the remembered foreground window first so a plain Enter selects it.
                var b = fw.Bounds.Intersect(desktop);
                if (!b.IsEmpty) { windows.Remove(b); windows.Insert(0, b); }
            }
            var frame = await Task.Run(() => GdiScreenCapture.Capture(desktop, monitors, options.IncludeCursor), ct);
            var selection = new RegionSelection(desktop, options.Shape, options.Constraint,
                multiRegion: mode == CaptureMode.MultiRegion, windows: windows, windowsOnly: mode == CaptureMode.Window,
                captureOnRelease: _services.Settings.CaptureOnRelease);
            var state = new CaptureOverlayState(_services, selection, mode, catalog, monitors, picker) { Notice = notice };
            var rects = await SelectAsync(frame, monitors, state, ct);
            if (rects is null || rects.Count == 0) return CaptureOutcome.Canceled();

            var items = new List<CaptureItem>();
            foreach (var r in rects)
            {
                var px = ToBuffer(frame.Crop(r));
                if (!selection.WindowsOnly && !selection.MultiRegion)
                {
                    if (selection.Shape == CaptureShape.Ellipse) px = CaptureMask.ApplyEllipse(px);
                    else if (selection.Shape == CaptureShape.Freehand && selection.ResultPolygon.Count >= 3)
                    {
                        try { px = CaptureMask.ApplyPolygon(px, CaptureMask.Simplify(selection.ResultPolygon, 0.75)); }
                        catch (ArgumentException) { return CaptureOutcome.Canceled(); }
                    }
                }
                items.Add(new CaptureItem(px, r));
            }
            if (items.Count == 1 && state.Mode is CaptureMode.Region)
                _services.LastRegion.Save(new LastRegion(items[0].Bounds, fingerprint));
            return Ok(items, state.Action);
        }
        catch (OperationCanceledException) { return CaptureOutcome.Canceled(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            _services.Log("Capture failed: " + ex);
            return CaptureOutcome.Fail("Capture failed: " + ex.Message);
        }
        finally
        {
            RestoreAppWindows(hidden);
            _busy = false;
        }
    }

    /// <summary>Captures a fixed physical rectangle without any UI (scrolling / interval sessions).</summary>
    public async Task<PixelBuffer> CaptureRectAsync(PixelRect physical, bool includeCursor, CancellationToken ct = default)
    {
        var monitors = _services.Monitors.GetMonitors();
        var f = await Task.Run(() => GdiScreenCapture.Capture(physical, monitors, includeCursor), ct);
        return ToBuffer(f);
    }

    /// <summary>Lets the user pick a rectangle (for scrolling / interval targets) without producing an image.</summary>
    public async Task<PixelRect?> PickRegionAsync(string hint, CancellationToken ct = default)
    {
        if (_busy) return null;
        _busy = true;
        var hidden = HideAppWindows();
        try
        {
            await SettleAsync(ct);
            var monitors = _services.Monitors.GetMonitors();
            if (monitors.Count == 0) return null;
            var desktop = MonitorTopology.VirtualBounds(monitors);
            var catalog = _services.Windows.Eligible(desktop);
            var windows = catalog.Select(w => w.Bounds).ToList();
            var frame = await Task.Run(() => GdiScreenCapture.Capture(desktop, monitors, false), ct);
            var sel = new RegionSelection(desktop, windows: windows);
            var state = new CaptureOverlayState(_services, sel, CaptureMode.Region, catalog, monitors, CapturePicker.None);
            var r = await SelectAsync(frame, monitors, state, ct);
            return r is { Count: > 0 } ? r[0] : null;
        }
        catch (OperationCanceledException) { return null; }
        finally
        {
            RestoreAppWindows(hidden);
            _busy = false;
        }
    }

    private static CaptureOutcome Ok(List<CaptureItem> items, CaptureOverlayAction action = CaptureOverlayAction.Edit) =>
        new(CaptureStatus.Success, items, Action: action);

    private PixelRect? LastRegionStoreResolve(string fingerprint) =>
        Storage.Settings.LastRegionStore.Resolve(_services.LastRegion.Load(), fingerprint)?.Bounds;

    private static PixelBuffer ToBuffer(CapturedFrame f) => new(f.Width, f.Height, f.Pixels);

    private static string HintFor(CaptureMode mode, CaptureOptions o) => mode switch
    {
        CaptureMode.Window => "Click a window (visible area only; overlapping windows stay in the image). Esc cancels.",
        CaptureMode.MultiRegion => "Drag several regions. Enter finishes, Backspace removes the last, Esc cancels.",
        _ when o.Constraint.FixedSize is { } fs => $"Click to place a {fs.Width}×{fs.Height} region. Esc cancels.",
        _ when o.Shape == CaptureShape.Freehand => "Draw a closed shape. Esc cancels.",
        _ => "Drag to select, click a window, arrows nudge, Enter confirms, Esc cancels.",
    };

    private async Task<IReadOnlyList<PixelRect>?> SelectAsync(CapturedFrame frame, IReadOnlyList<MonitorInfo> monitors,
        CaptureOverlayState state, CancellationToken ct)
    {
        var selection = state.Selection;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() { if (selection.IsFinished) tcs.TrySetResult(true); }
        void OnTopology(object? s, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(selection.Cancel);
        selection.Changed += OnChanged;
        _services.Monitors.TopologyChanged += OnTopology;
        using var reg = ct.Register(() => Application.Current.Dispatcher.BeginInvoke(selection.Cancel));
        var overlays = new List<RegionOverlayWindow>();
        try
        {
            selection.Move(AppNative.CursorPosition());
            foreach (var m in monitors)
            {
                var part = frame.Crop(m.Bounds);
                var bmp = new PixelBuffer(part.Width, part.Height, part.Pixels).ToBitmap();
                var w = new RegionOverlayWindow(m, bmp, state);
                overlays.Add(w);
                w.Show();
            }
            var cursorMon = MonitorTopology.At(monitors, AppNative.CursorPosition());
            var focus = overlays.FirstOrDefault(o => cursorMon is not null && ReferenceEquals(monitors[overlays.IndexOf(o)], cursorMon)) ?? overlays[0];
            focus.Activate();
            focus.Focus();
            await tcs.Task;
            return selection.Phase == SelectionPhase.Done ? selection.Result.ToList() : null;
        }
        finally
        {
            state.Notice = null;
            selection.Changed -= OnChanged;
            _services.Monitors.TopologyChanged -= OnTopology;
            foreach (var o in overlays) o.Close();
        }
    }

    private static List<Window> HideAppWindows()
    {
        var list = new List<Window>();
        foreach (Window w in Application.Current.Windows)
            if (w.IsVisible) { list.Add(w); w.Hide(); }
        return list;
    }

    private static void RestoreAppWindows(List<Window> windows)
    {
        foreach (var w in windows)
        {
            try { w.Show(); } catch (InvalidOperationException) { }
        }
        var main = windows.FirstOrDefault(w => w == Application.Current.MainWindow);
        main?.Activate();
    }

    /// <summary>Waits until hidden windows have left the composed desktop.</summary>
    private static async Task SettleAsync(CancellationToken ct)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(150, ct);
        AppNative.DwmFlush();
        await Task.Delay(50, ct);
    }

    /// <summary>Small countdown badge excluded from capture; the desktop freezes only after it closes.</summary>
    private async Task<bool> CountdownAsync(int seconds, CancellationToken ct)
    {
        var monitors = _services.Monitors.GetMonitors();
        if (monitors.Count == 0) return false;
        var monitor = MonitorTopology.At(monitors, AppNative.CursorPosition()) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var w = new CountdownBadgeWindow(monitor);
        try
        {
            w.Update(seconds, seconds);
            w.Show();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                if (w.CancellationRequested) return false;
                w.Update(Math.Max(0, seconds - clock.Elapsed.TotalSeconds), seconds);
                await Task.Delay(ThemeService.AnimationsEnabled ? 40 : 100, ct);
            }
            return true;
        }
        finally
        {
            w.Close();
            await Task.Delay(120, CancellationToken.None);
            AppNative.DwmFlush();
        }
    }

}
