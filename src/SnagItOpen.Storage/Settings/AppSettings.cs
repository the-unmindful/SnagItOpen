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
/// <summary>Behaviour after a drawing tool adds an element.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AfterDrawBehavior>))]
public enum AfterDrawBehavior { KeepToolSelectNew, SelectToolSelectNew, SelectToolSelectNothing }

public sealed record AppSettings
{
    public const int CurrentVersion = 3;
    public int Version { get; init; } = CurrentVersion;
    /// <summary>Colour theme: follow Windows, or force Light/Dark. High contrast always wins (v3).</summary>
    public AppTheme ThemeMode { get; init; } = AppTheme.System;
    /// <summary>Selection colour: fixed blue (default, user decision) or the Windows accent (v3).</summary>
    public SelectionAccent SelectionAccent { get; init; } = SelectionAccent.Blue;
    /// <summary>Region capture commits when the mouse is released (default, user decision); off = Adjust phase with action bar (v3).</summary>
    public bool CaptureOnRelease { get; init; } = true;
    /// <summary>Show the pixel loupe in the capture overlay (v3).</summary>
    public bool ShowLoupe { get; init; } = true;
    public PixelSize? LastCaptureCustomSize { get; init; }
    public double? LastCaptureCustomAspect { get; init; }
    /// <summary>After a capture while the editor is hidden, show a desktop toast (v3).</summary>
    public bool DesktopToasts { get; init; } = true;
    public int CaptureDelaySeconds { get; init; }
    public bool IncludeCursor { get; init; }
    public CaptureDestination DefaultDestination { get; init; } = CaptureDestination.AppendBelow;
    /// <summary>Also copy every finished capture to the clipboard (on by default; "Copy to clipboard only" always copies).</summary>
    public bool CopyCaptureToClipboard { get; init; } = true;
    /// <summary>What happens after a drawing tool creates an element (default: like Affinity/Illustrator, keep the tool, select the new element).</summary>
    public AfterDrawBehavior AfterDrawing { get; init; } = AfterDrawBehavior.KeepToolSelectNew;
    /// <summary>Closing the editor hides it; SnagItOpen keeps running in the tray with its hotkeys.</summary>
    public bool CloseToTray { get; init; } = true;
    /// <summary>Start SnagItOpen hidden in the tray when you sign in to Windows.</summary>
    public bool StartWithWindows { get; init; }
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
        new(HotkeyActions.Region, "PrintScreen"),
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
    public AppSettings Sanitize() => Migrate().SanitizeCore();

    /// <summary>
    /// Version 1 → 2: region capture moves to PrintScreen (only if it was still the old default) and
    /// closing the editor keeps the app in the tray.
    /// Version 2 → 3: additive only. The new fields are absent from older files, so the property
    /// initialisers already supply their defaults; nothing existing changes.
    /// </summary>
    private AppSettings Migrate()
    {
        if (Version >= CurrentVersion) return this;
        var s = this with { Hotkeys = Hotkeys ?? DefaultHotkeys() };
        if (Version < 2)
        {
            s = s with { CloseToTray = true };
            if (string.Equals(s.GestureFor(HotkeyActions.Region), "Ctrl+Shift+1", StringComparison.OrdinalIgnoreCase)
                && !(s.Hotkeys ?? []).Any(h => h is not null && string.Equals(h.Gesture, "PrintScreen", StringComparison.OrdinalIgnoreCase)))
                s = s.WithHotkey(HotkeyActions.Region, "PrintScreen");
        }
        return s with { Version = CurrentVersion };
    }

    private AppSettings SanitizeCore()
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
            AfterDrawing = Enum.IsDefined(AfterDrawing) ? AfterDrawing : AfterDrawBehavior.KeepToolSelectNew,
            JpegQuality = Math.Clamp(JpegQuality, 1, 100),
            OutsideCanvas = Enum.IsDefined(OutsideCanvas) ? OutsideCanvas : OutsideCanvasMode.Dim,
            ThemeMode = Enum.IsDefined(ThemeMode) ? ThemeMode : AppTheme.System,
            SelectionAccent = Enum.IsDefined(SelectionAccent) ? SelectionAccent : SelectionAccent.Blue,
            LastCaptureCustomSize = LastCaptureCustomSize is { Width: > 0, Height: > 0 } size
                && size.Width <= Limits.MaxDimension && size.Height <= Limits.MaxDimension ? size : null,
            LastCaptureCustomAspect = LastCaptureCustomAspect is { } aspect && double.IsFinite(aspect) && aspect > 0 ? aspect : null,
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
