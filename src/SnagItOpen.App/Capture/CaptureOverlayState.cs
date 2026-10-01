using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Windows.Capture;

namespace SnagItOpen.App.Capture;

/// <summary>Intent chosen before the frozen selection is returned to the editor.</summary>
public enum CaptureOverlayAction { Edit, Copy, Save, Pin, AppendBelow, AppendRight, Drag }

internal enum CapturePicker { None, Size, Aspect }

internal sealed class CaptureOverlayState
{
    private readonly AppServices _services;
    public CaptureOverlayState(AppServices services, RegionSelection selection, CaptureMode mode,
        IReadOnlyList<WindowInfo> windows, IReadOnlyList<MonitorInfo> monitors, CapturePicker picker)
    {
        _services = services;
        Selection = selection;
        Mode = mode;
        Windows = windows;
        Monitors = monitors;
        Picker = picker;
        selection.Changed += () =>
        {
            if (selection.IsFinished) Notice = null;
            if (selection.Phase != SelectionPhase.Adjusting || _services.UiState.TipsShown.Contains("CaptureActionBar")) return;
            CoachTipUntil = DateTimeOffset.UtcNow.AddSeconds(4);
            _services.SaveUiState(_services.UiState with { TipsShown = [.. _services.UiState.TipsShown, "CaptureActionBar"] });
        };
    }

    public RegionSelection Selection { get; }
    public CaptureMode Mode { get; private set; }
    public CaptureOverlayAction Action { get; set; }
    public IReadOnlyList<WindowInfo> Windows { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public CapturePicker Picker { get; private set; }
    public bool AppendRight { get; set; }
    /// <summary>Transient feedback for this overlay session only; cleared when it finishes.</summary>
    public string? Notice { get; set; }
    public DateTimeOffset CoachTipUntil { get; private set; }
    public bool ShowLoupe => _services.Settings.ShowLoupe;
    public PixelSize? LastCustomSize => _services.Settings.LastCaptureCustomSize;
    public double? LastCustomAspect => _services.Settings.LastCaptureCustomAspect;
    public event Action? Changed;
    public event Action? ActionFocusRequested;
    public void FocusActions() => ActionFocusRequested?.Invoke();
    public event Action<bool>? ColorCopyRequested;
    public void CopyColor(bool rgb) => ColorCopyRequested?.Invoke(rgb);

    public string ModeName => Mode switch
    {
        CaptureMode.Window => "Window", CaptureMode.Monitor => "Monitor", CaptureMode.MultiRegion => "Multi-region",
        _ when Selection.Shape == CaptureShape.Ellipse => "Ellipse",
        _ when Selection.Shape == CaptureShape.Freehand => "Freehand",
        _ when Selection.Constraint.FixedSize is not null => "Fixed size",
        _ when Selection.Constraint.AspectRatio is not null => "Fixed aspect", _ => "Region",
    };

    public void ToggleLoupe()
    {
        _services.SaveSettings(_services.Settings with { ShowLoupe = !ShowLoupe });
        Changed?.Invoke();
    }

    public void SwitchMode(int number)
    {
        Notice = null;
        Picker = CapturePicker.None;
        Mode = number switch { 2 => CaptureMode.Window, 3 => CaptureMode.Monitor,
            6 => CaptureMode.MultiRegion, _ => CaptureMode.Region };
        Selection.Configure(number switch { 4 => CaptureShape.Ellipse, 5 => CaptureShape.Freehand, _ => CaptureShape.Rectangle },
            multiRegion: number == 6, windowsOnly: number == 2);
        Changed?.Invoke();
    }

    public void ChooseSize(PixelSize size, bool custom = false)
    {
        Notice = null;
        if (custom) _services.SaveSettings(_services.Settings with { LastCaptureCustomSize = size });
        Picker = CapturePicker.None;
        Selection.Configure(CaptureShape.Rectangle, new SelectionConstraint { FixedSize = size });
        Changed?.Invoke();
    }

    public void ChooseAspect(double ratio, bool custom = false)
    {
        Notice = null;
        if (custom) _services.SaveSettings(_services.Settings with { LastCaptureCustomAspect = ratio });
        Picker = CapturePicker.None;
        Selection.Configure(CaptureShape.Rectangle, new SelectionConstraint { AspectRatio = ratio });
        Changed?.Invoke();
    }
}

/// <summary>Samples physical bitmap coordinates regardless of its DPI metadata.</summary>
public static class CapturePixelSampler
{
    public static Rgba32 Sample(BitmapSource bitmap, int x, int y)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        source.CopyPixels(new System.Windows.Int32Rect(Math.Clamp(x, 0, source.PixelWidth - 1),
            Math.Clamp(y, 0, source.PixelHeight - 1), 1, 1), pixel, 4, 0);
        return new Rgba32(pixel[2], pixel[1], pixel[0], pixel[3]);
    }

    public static string ColorText(BitmapSource bitmap, int x, int y, bool rgb = false)
    {
        var c = Sample(bitmap, x, y);
        return rgb ? $"rgb({c.R}, {c.G}, {c.B})" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
