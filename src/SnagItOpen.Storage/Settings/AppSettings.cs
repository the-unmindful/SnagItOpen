using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Storage.Settings;

/// <summary>Global hotkey assignment, e.g. Action="Region", Gesture="Ctrl+Shift+1". Empty gesture = unbound.</summary>
public sealed record HotkeyBinding(string Action, string Gesture);

public static class HotkeyActions
{
    public const string Region = "Region";
    public const string Window = "Window";
    public const string AppendRegion = "AppendRegion";
    public const string FullScreen = "AllMonitors";
    public const string LastRegion = "LastRegion";
    public const string Scrolling = "Scrolling";
    public static readonly string[] All = [Region, Window, AppendRegion, FullScreen, LastRegion, Scrolling];
}

/// <summary>How the editor shows content outside a locked canvas (it is never exported).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<OutsideCanvasMode>))]
public enum OutsideCanvasMode { Dim, Show, Hide }

/// <summary>Persisted user preferences (settings.json).</summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public int CaptureDelaySeconds { get; init; }
    public bool IncludeCursor { get; init; }
    public CaptureDestination DefaultDestination { get; init; } = CaptureDestination.AppendBelow;
    public bool CloseToTray { get; init; }
    public bool ShowTrayIcon { get; init; } = true;
    public bool CopyAfterCapture { get; init; }
    public bool SnapEnabled { get; init; } = true;
    public OutsideCanvasMode OutsideCanvas { get; init; } = OutsideCanvasMode.Dim;
    public int JpegQuality { get; init; } = 90;
    public int HistoryMaxCount { get; init; } = 200;
    public int HistoryMaxMegabytes { get; init; } = 500;
    public bool SaveCapturesToHistory { get; init; } = true;
    public bool ShowCaptureGallery { get; init; } = true;
    public string? LastImportDirectory { get; init; }
    public string? LastExportDirectory { get; init; }
    public string? LastProjectDirectory { get; init; }
    public Rgba32 DefaultBackground { get; init; } = Rgba32.Transparent;
    public LayoutOptions DefaultLayout { get; init; } = LayoutOptions.Default;
    public HotkeyBinding[] Hotkeys { get; init; } = DefaultHotkeys();

    public static HotkeyBinding[] DefaultHotkeys() =>
    [
        new(HotkeyActions.Region, "Ctrl+Shift+1"),
        new(HotkeyActions.Window, "Ctrl+Shift+2"),
        new(HotkeyActions.AppendRegion, "Ctrl+Shift+3"),
        new(HotkeyActions.FullScreen, "Ctrl+Shift+4"),
        new(HotkeyActions.LastRegion, ""),
        new(HotkeyActions.Scrolling, ""),
    ];

    public string GestureFor(string action) => Hotkeys.FirstOrDefault(h => h.Action == action)?.Gesture ?? "";

    public AppSettings WithHotkey(string action, string gesture) => this with
    {
        Hotkeys = Hotkeys.Any(h => h.Action == action)
            ? Hotkeys.Select(h => h.Action == action ? h with { Gesture = gesture } : h).ToArray()
            : [.. Hotkeys, new HotkeyBinding(action, gesture)],
    };

    /// <summary>Clamps every value into its valid range; unknown actions are dropped.</summary>
    public AppSettings Sanitize()
    {
        var hk = (Hotkeys ?? []).Where(h => h is not null && HotkeyActions.All.Contains(h.Action))
            .GroupBy(h => h.Action).Select(g => g.First() with { Gesture = g.First().Gesture ?? "" }).ToList();
        foreach (var d in DefaultHotkeys()) if (hk.All(h => h.Action != d.Action)) hk.Add(d with { Gesture = "" });
        var layout = DefaultLayout ?? LayoutOptions.Default;
        layout = layout with
        {
            Gap = Math.Clamp(layout.Gap, 0, Limits.MaxGap),
            Padding = Math.Clamp(layout.Padding, 0, Limits.MaxPadding),
            TargetCrossPixels = layout.TargetCrossPixels is { } t && t > 0 && t <= Limits.MaxDimension ? t : null,
            Mode = Enum.IsDefined(layout.Mode) ? layout.Mode : LayoutMode.Vertical,
            Alignment = Enum.IsDefined(layout.Alignment) ? layout.Alignment : CrossAlignment.Start,
            Scale = Enum.IsDefined(layout.Scale) ? layout.Scale : ScaleMode.Original,
        };
        return this with
        {
            CaptureDelaySeconds = CaptureOptions.AllowedDelays.Contains(CaptureDelaySeconds) ? CaptureDelaySeconds : 0,
            DefaultDestination = Enum.IsDefined(DefaultDestination) ? DefaultDestination : CaptureDestination.AppendBelow,
            JpegQuality = Math.Clamp(JpegQuality, 1, 100),
            OutsideCanvas = Enum.IsDefined(OutsideCanvas) ? OutsideCanvas : OutsideCanvasMode.Dim,
            HistoryMaxCount = Math.Clamp(HistoryMaxCount, 1, 10_000),
            HistoryMaxMegabytes = Math.Clamp(HistoryMaxMegabytes, 10, 100_000),
            DefaultLayout = layout,
            Hotkeys = hk.ToArray(),
        };
    }
}

/// <summary>settings.json persistence with corrupt-file backup and fallback.</summary>
public sealed class SettingsStore
{
    private readonly JsonFileStore<AppSettings> _file;

    public SettingsStore(string path) =>
        _file = new JsonFileStore<AppSettings>(path, () => new AppSettings(), s => s.Sanitize());

    public string Path => _file.Path;
    public LoadOutcome<AppSettings> Load() => _file.Load();
    public void Save(AppSettings s) => _file.Save(s.Sanitize());
}
