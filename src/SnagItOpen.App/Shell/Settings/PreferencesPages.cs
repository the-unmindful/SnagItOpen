using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Shell.Settings;

/// <summary>The six preference pages. Every editor writes to the window's staged settings record.</summary>
internal sealed class PreferencesPages
{
    public IReadOnlyList<SettingsPage> All { get; }
    public CapturePresetsPage Presets { get; }
    public event Action? Changed;
    private readonly Dictionary<string, HotkeyBox> _hotkeys = new(StringComparer.Ordinal);
    private readonly InfoBar _shortcutsWarning = new() { Kind = NotificationKind.Warning, Title = "Shortcut conflict", CanClose = false, Visibility = Visibility.Collapsed };
    private readonly Func<AppSettings> _current;
    private readonly Action<Func<AppSettings, AppSettings>> _change;

    public PreferencesPages(AppServices services, Func<AppSettings> current, Action<Func<AppSettings, AppSettings>> change, Action<AppTheme> previewTheme, CommandRegistry? commands)
    {
        _current = current; _change = change;
        var initial = current();
        var general = new SettingsPage("General");
        general.Check(nameof(initial.ShowTrayIcon), "Show notification-area icon", initial.ShowTrayIcon, v => change(s => s with { ShowTrayIcon = v }));
        general.Check(nameof(initial.CloseToTray), "Closing the editor keeps it running in the tray", initial.CloseToTray, v => change(s => s with { CloseToTray = v }));
        general.Check(nameof(initial.StartWithWindows), "Start with Windows", initial.StartWithWindows, v => change(s => s with { StartWithWindows = v }));
        general.Note("Start with Windows opens SnagItOpen in the tray when you sign in.");
        general.Check(nameof(initial.ShowCaptureGallery), "Show recent captures strip", initial.ShowCaptureGallery, v => change(s => s with { ShowCaptureGallery = v }));
        general.Check(nameof(initial.SnapEnabled), "Snap images and annotations while moving", initial.SnapEnabled, v => change(s => s with { SnapEnabled = v }));
        general.Choice(nameof(initial.AfterDrawing), "After drawing an element", initial.AfterDrawing, Enum.GetValues<AfterDrawBehavior>(), v => change(s => s with { AfterDrawing = v }), v => v switch
        {
            AfterDrawBehavior.KeepToolSelectNew => "Keep the tool, select the new element (like Affinity)",
            AfterDrawBehavior.SelectToolSelectNew => "Switch to Select, keep the new element selected",
            _ => "Switch to Select, select nothing",
        });

        var capture = new SettingsPage("Capture");
        capture.Choice(nameof(initial.CaptureDelaySeconds), "Capture delay", initial.CaptureDelaySeconds, CaptureOptions.AllowedDelays, v => change(s => s with { CaptureDelaySeconds = v }), v => v == 0 ? "No delay" : $"{v} seconds");
        capture.Choice(nameof(initial.DefaultDestination), "Default destination", initial.DefaultDestination, Enum.GetValues<CaptureDestination>(), v => change(s => s with { DefaultDestination = v }));
        capture.Check(nameof(initial.CopyCaptureToClipboard), "Also copy each capture to the clipboard", initial.CopyCaptureToClipboard, v => change(s => s with { CopyCaptureToClipboard = v }));
        capture.Check(nameof(initial.IncludeCursor), "Include the mouse cursor", initial.IncludeCursor, v => change(s => s with { IncludeCursor = v }));
        capture.Check(nameof(initial.CaptureOnRelease), "Capture immediately on release", initial.CaptureOnRelease, v => change(s => s with { CaptureOnRelease = v }));
        capture.Note("Turn off immediate capture to adjust the selection and choose an action before capturing.");
        capture.Check(nameof(initial.ShowLoupe), "Show pixel loupe", initial.ShowLoupe, v => change(s => s with { ShowLoupe = v }));
        capture.Check(nameof(initial.DesktopToasts), "Show a desktop toast when the editor is hidden", initial.DesktopToasts, v => change(s => s with { DesktopToasts = v }));
        Presets = new CapturePresetsPage(services.CapturePresets.Presets, services.LastRegion.Load(), MonitorTopology.Fingerprint(services.Monitors.GetMonitors()));
        capture.Add(null, "Capture presets", Presets, Presets.SearchLabels, ownLabel: true);

        var shortcuts = new SettingsPage("Shortcuts");
        shortcuts.Note("Global shortcuts work while SnagItOpen is running. Click a recorder and press the keys. Backspace clears it. Capture hotkeys are paused while these settings are open.");
        foreach (string action in HotkeyActions.All)
        {
            string label = HotkeyLabel(action);
            var row = new DockPanel();
            var reset = new IconButton { Label = "Reset " + label + " shortcut", ShowLabel = false, Width = 28, ToolTip = "Restore the default shortcut for " + label, Margin = new Thickness(6, 0, 0, 0) };
            reset.SetResourceReference(IconButton.IconProperty, "Icon.Reset");
            System.Windows.Automation.AutomationProperties.SetName(reset, "Reset " + label + " shortcut");
            DockPanel.SetDock(reset, Dock.Right); row.Children.Add(reset);
            void Install(string gesture)
            {
                if (_hotkeys.TryGetValue(action, out var old)) row.Children.Remove(old);
                var box = new HotkeyBox(gesture, label); _hotkeys[action] = box; row.Children.Add(box);
                box.Changed += () => { change(s => s.WithHotkey(action, box.Gesture)); UpdateShortcutWarning(); Changed?.Invoke(); };
                change(s => s.WithHotkey(action, gesture));
            }
            Install(initial.GestureFor(action));
            reset.Click += (_, _) => { Install(AppSettings.DefaultHotkeys().First(h => h.Action == action).Gesture); UpdateShortcutWarning(); Changed?.Invoke(); };
            shortcuts.Add(nameof(initial.Hotkeys), label, row);
        }
        shortcuts.Add(null, "Shortcut warnings", _shortcutsWarning, ownLabel: true);
        var presetKeys = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void RefreshPresetKeys() => presetKeys.Text = Presets.Presets.Count == 0 ? "No capture preset shortcuts. Add presets on the Capture page."
            : string.Join("\n", Presets.Presets.Select(p => $"{p.Name}: {(p.Hotkey.Length == 0 ? "None" : p.Hotkey)}"));
        RefreshPresetKeys();
        shortcuts.Add(null, "Capture preset shortcuts", presetKeys);
        var editorEntries = commands?.All.Where(c => c.Gesture.Length > 0).Select(c => new ShortcutEntry(c.Category, c.Title, c.Gesture)).ToList() ?? [];
        editorEntries.AddRange(ToolCatalog.All.Select(t => new ShortcutEntry("Tools", t.Name, t.Shortcut.ToString())));
        var editorKeys = new TextBlock { Text = string.Join("\n", editorEntries.Distinct().OrderBy(e => e.Category).ThenBy(e => e.Title).Select(e => $"{e.Title}   {e.Gesture}")), TextWrapping = TextWrapping.Wrap };
        shortcuts.Add(null, "In-editor shortcuts", editorKeys, "tools file edit view canvas");
        var showKeys = new Button { Content = "Open keyboard shortcuts…", HorizontalAlignment = HorizontalAlignment.Left };
        showKeys.Click += (_, _) =>
        {
            var entries = editorEntries.Concat(_current().Hotkeys.Select(h => new ShortcutEntry("Capture global", HotkeyLabel(h.Action), h.Gesture)))
                .Concat(Presets.Presets.Select(p => new ShortcutEntry("Capture presets", p.Name, p.Hotkey)));
            new ShortcutsWindow(Window.GetWindow(showKeys), entries).Show();
        };
        shortcuts.Add(null, "Keyboard shortcuts reference", showKeys);
        Presets.Changed += () => { RefreshPresetKeys(); UpdateShortcutWarning(); Changed?.Invoke(); };

        var output = new SettingsPage("Output & library");
        output.Number(nameof(initial.JpegQuality), "JPEG quality", initial.JpegQuality, 1, 100, v => change(s => s with { JpegQuality = (int)v }));
        output.Check(nameof(initial.SaveCapturesToHistory), "Keep captures in the recent library", initial.SaveCapturesToHistory, v => change(s => s with { SaveCapturesToHistory = v }));
        output.Number(nameof(initial.HistoryMaxCount), "Maximum captures in library", initial.HistoryMaxCount, 1, 10_000, v => change(s => s with { HistoryMaxCount = (int)v }));
        output.Number(nameof(initial.HistoryMaxMegabytes), "Maximum library size (MiB)", initial.HistoryMaxMegabytes, 10, 100_000, v => change(s => s with { HistoryMaxMegabytes = (int)v }));
        ColorSwatchButton swatch = null!;
        var transparent = output.Check(nameof(initial.DefaultBackground), "Transparent default background", initial.DefaultBackground.A == 0, v => change(s => s with { DefaultBackground = v ? Rgba32.Transparent : swatch.Value ?? Rgba32.White }));
        swatch = new ColorSwatchButton { Label = "Default background", Value = initial.DefaultBackground.A == 0 ? Rgba32.White : initial.DefaultBackground, AllowNone = false };
        swatch.Committed += v => { if (v is { } color) { transparent.IsChecked = false; change(s => s with { DefaultBackground = color }); Changed?.Invoke(); } };
        output.Add(nameof(initial.DefaultBackground), "Default background colour", swatch);
        var layout = initial.DefaultLayout;
        void SetLayout(Func<LayoutOptions, LayoutOptions> mutate) => change(s => s with { DefaultLayout = mutate(s.DefaultLayout) });
        output.Choice(nameof(initial.DefaultLayout), "Default combine layout", layout.Mode, Enum.GetValues<LayoutMode>(), v => SetLayout(l => l with { Mode = v }));
        output.Number(nameof(initial.DefaultLayout), "Default gap (pixels)", layout.Gap, 0, Limits.MaxGap, v => SetLayout(l => l with { Gap = (int)v }));
        output.Number(nameof(initial.DefaultLayout), "Default padding (pixels)", layout.Padding, 0, Limits.MaxPadding, v => SetLayout(l => l with { Padding = (int)v }));
        output.Choice(nameof(initial.DefaultLayout), "Default cross alignment", layout.Alignment, Enum.GetValues<CrossAlignment>(), v => SetLayout(l => l with { Alignment = v }));
        output.Choice(nameof(initial.DefaultLayout), "Default image sizing", layout.Scale, Enum.GetValues<ScaleMode>(), v => SetLayout(l => l with { Scale = v }), v => v == ScaleMode.Original ? "Original size" : "Match width or height");
        NumberBox target = null!;
        var targetOn = output.Check(nameof(initial.DefaultLayout), "Use a fixed width or height when matching", layout.TargetCrossPixels is not null, v => { target.IsEnabled = v; SetLayout(l => l with { TargetCrossPixels = v ? (int)target.Value : null }); });
        target = output.Number(nameof(initial.DefaultLayout), "Match target (pixels)", layout.TargetCrossPixels ?? 1920, 1, Limits.MaxDimension, v => { if (targetOn.IsChecked == true) SetLayout(l => l with { TargetCrossPixels = (int)v }); });
        target.IsEnabled = targetOn.IsChecked == true;
        output.Check(nameof(initial.DefaultLayout), "Allow enlarging images to match", layout.AllowUpscale, v => SetLayout(l => l with { AllowUpscale = v }));

        var appearance = new SettingsPage("Appearance");
        appearance.Choice(nameof(initial.ThemeMode), "Theme", initial.ThemeMode, Enum.GetValues<AppTheme>(), v => { change(s => s with { ThemeMode = v }); previewTheme(v); }, v => v == AppTheme.System ? "Follow Windows" : DisplayNames.For(v));
        appearance.Note("Windows High Contrast always takes priority. Theme changes are previewed immediately and restored on Cancel.");
        var preview = new Border { Height = 104, Padding = new Thickness(20), Margin = new Thickness(0, 8, 0, 8) };
        preview.SetResourceReference(Border.BackgroundProperty, "Bg.Canvas");
        var sheet = new Border { BorderThickness = new Thickness(1.5), Padding = new Thickness(12), Child = new TextBlock { Text = "Canvas preview", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        sheet.SetResourceReference(Border.BackgroundProperty, "Bg.Surface"); sheet.SetResourceReference(Border.BorderBrushProperty, "Accent.Select"); preview.Child = sheet;
        appearance.Choice(nameof(initial.SelectionAccent), "Selection accent", initial.SelectionAccent, Enum.GetValues<SelectionAccent>(), v =>
        {
            change(s => s with { SelectionAccent = v });
            sheet.SetResourceReference(Border.BorderBrushProperty, v == SelectionAccent.System ? SystemColors.HighlightBrushKey : "Accent.Select");
        }, v => v == SelectionAccent.Blue ? "Blue" : "Windows accent");
        appearance.Choice(nameof(initial.OutsideCanvas), "Content outside the locked canvas", initial.OutsideCanvas, Enum.GetValues<OutsideCanvasMode>(), v => change(s => s with { OutsideCanvas = v }), v => v switch { OutsideCanvasMode.Dim => "Dim outside content", OutsideCanvasMode.Show => "Show outside content", _ => "Hide outside content" });
        appearance.Add(null, "Canvas chrome preview", preview, ownLabel: true);

        var advanced = new SettingsPage("Advanced");
        advanced.Note("Remembered custom capture constraints. Turn them off to clear the saved custom size or aspect.");
        NumberBox width = null!, height = null!, ratio = null!;
        var sizeOn = advanced.Check(nameof(initial.LastCaptureCustomSize), "Remember a custom capture size", initial.LastCaptureCustomSize is not null, v => { width.IsEnabled = height.IsEnabled = v; change(s => s with { LastCaptureCustomSize = v ? new PixelSize((int)width.Value, (int)height.Value) : null }); });
        width = advanced.Number(nameof(initial.LastCaptureCustomSize), "Remembered capture width", initial.LastCaptureCustomSize?.Width ?? 640, 1, Limits.MaxDimension, v => { if (sizeOn.IsChecked == true) change(s => s with { LastCaptureCustomSize = new PixelSize((int)v, (int)height.Value) }); });
        height = advanced.Number(nameof(initial.LastCaptureCustomSize), "Remembered capture height", initial.LastCaptureCustomSize?.Height ?? 480, 1, Limits.MaxDimension, v => { if (sizeOn.IsChecked == true) change(s => s with { LastCaptureCustomSize = new PixelSize((int)width.Value, (int)v) }); });
        width.IsEnabled = height.IsEnabled = sizeOn.IsChecked == true;
        var aspectOn = advanced.Check(nameof(initial.LastCaptureCustomAspect), "Remember a custom capture aspect", initial.LastCaptureCustomAspect is not null, v => { ratio.IsEnabled = v; change(s => s with { LastCaptureCustomAspect = v ? ratio.Value : null }); });
        ratio = advanced.Number(nameof(initial.LastCaptureCustomAspect), "Remembered aspect (width / height)", initial.LastCaptureCustomAspect ?? 16d / 9, 0.011, 100, v => { if (aspectOn.IsChecked == true) change(s => s with { LastCaptureCustomAspect = v }); }, integer: false, step: 0.01);
        ratio.IsEnabled = aspectOn.IsChecked == true;
        var dataFolder = new Button { Content = "Open data folder", HorizontalAlignment = HorizontalAlignment.Left };
        dataFolder.Click += (_, _) =>
        {
            try { var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add(services.Paths.Root); Process.Start(info); }
            catch (System.ComponentModel.Win32Exception ex) { Dialogs.Error(Window.GetWindow(dataFolder), ex.Message); }
        };
        advanced.Add(null, "Application data folder", dataFolder);
        All = [general, capture, shortcuts, output, appearance, advanced];
        foreach (var page in All) page.Changed += () => Changed?.Invoke();
        UpdateShortcutWarning();
    }

    public string? HotkeyError()
    {
        var bindings = _current().Hotkeys.Select(h => (Name: HotkeyLabel(h.Action), Gesture: h.Gesture))
            .Concat(Presets.Presets.Select(p => (Name: "Preset " + p.Name, Gesture: p.Hotkey))).Where(h => h.Gesture.Length > 0).ToArray();
        foreach (var binding in bindings)
            if (!SnagItOpen.Windows.Shell.HotkeyGesture.TryParse(binding.Gesture, out _)) return $"{binding.Name}: invalid shortcut.";
        var duplicate = bindings.Select(h => SnagItOpen.Windows.Shell.HotkeyGesture.TryParse(h.Gesture, out var gesture) ? (h.Name, Gesture: gesture.ToString()) : h)
            .GroupBy(h => h.Gesture, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? null : $"{duplicate.Key} is assigned to {string.Join(" and ", duplicate.Select(h => h.Name))}.";
    }
    private void UpdateShortcutWarning()
    {
        string? error = HotkeyError(); _shortcutsWarning.Message = error ?? "";
        _shortcutsWarning.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private static string HotkeyLabel(string action) => action switch
    {
        HotkeyActions.Region => "Capture region", HotkeyActions.Window => "Capture window", HotkeyActions.AppendRegion => "Append region",
        HotkeyActions.FullScreen => "All monitors", HotkeyActions.LastRegion => "Last region", HotkeyActions.Scrolling => "Scrolling capture", _ => action,
    };
}
