using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Capture;
using SnagItOpen.Storage.Settings;
using SnagItOpen.Windows.Shell;

namespace SnagItOpen.App.Shell;

/// <summary>Validated editor for settings.json. Invalid entries are reported and nothing is saved.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly ComboBox _delay = new() { ItemsSource = CaptureOptions.AllowedDelays };
    private readonly ComboBox _destination = new() { ItemsSource = Enum.GetValues<CaptureDestination>() };
    private readonly CheckBox _cursor = new() { Content = "Include the mouse cursor" };
    private readonly CheckBox _copyAfter = new() { Content = "Also copy each capture to the clipboard" };
    private readonly CheckBox _history = new() { Content = "Keep captures in the recent library" };
    private readonly CheckBox _tray = new() { Content = "Show notification-area icon" };
    private readonly CheckBox _closeToTray = new() { Content = "Closing the window keeps SnagItOpen running in the tray" };
    private readonly CheckBox _snap = new() { Content = "Snap while moving images" };
    private readonly TextBox _jpeg = new();
    private readonly TextBox _maxCount = new();
    private readonly TextBox _maxMb = new();
    private readonly CheckBox _startWithWindows = new() { Content = "Start SnagItOpen in the tray when I sign in to Windows" };
    private readonly Dictionary<string, HotkeyBox> _hotkeys = new(StringComparer.Ordinal);
    private readonly TextBlock _hotkeyStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 4, 0, 0) };

    public SettingsWindow(AppServices services)
    {
        _services = services;
        Title = "Settings - SnagItOpen";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var s = services.Settings;
        _delay.SelectedItem = s.CaptureDelaySeconds;
        _destination.SelectedItem = s.DefaultDestination;
        _cursor.IsChecked = s.IncludeCursor;
        _copyAfter.IsChecked = s.CopyAfterCapture;
        _history.IsChecked = s.SaveCapturesToHistory;
        _tray.IsChecked = s.ShowTrayIcon;
        _closeToTray.IsChecked = s.CloseToTray;
        _snap.IsChecked = s.SnapEnabled;
        _startWithWindows.IsChecked = s.StartWithWindows;
        // Registered hotkeys would swallow the keys being recorded; release them while this window is open.
        if (Application.Current.MainWindow is MainWindow owner) owner.SuspendHotkeys();
        Closed += (_, _) => { if (Application.Current.MainWindow is MainWindow m) m.ApplyHotkeys(); };
        _jpeg.Text = s.JpegQuality.ToString();
        _maxCount.Text = s.HistoryMaxCount.ToString();
        _maxMb.Text = s.HistoryMaxMegabytes.ToString();

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(Header("Capture"));
        panel.Children.Add(Row("Delay (seconds)", _delay));
        panel.Children.Add(Row("Default destination", _destination));
        panel.Children.Add(_cursor);
        panel.Children.Add(_copyAfter);
        panel.Children.Add(_history);

        panel.Children.Add(Header("Global shortcuts (work anywhere while SnagItOpen runs in the tray)"));
        panel.Children.Add(new TextBlock
        {
            Text = "Click a box and press the keys. Backspace or ✕ clears it. Hotkeys are paused while this window is open.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var action in HotkeyActions.All)
        {
            string label = action switch
            {
                HotkeyActions.Region => "Capture region",
                HotkeyActions.Window => "Capture window",
                HotkeyActions.AppendRegion => "Append region",
                HotkeyActions.FullScreen => "All monitors",
                HotkeyActions.LastRegion => "Last region",
                HotkeyActions.Scrolling => "Scrolling capture",
                _ => action,
            };
            var box = new HotkeyBox(s.GestureFor(action), label);
            box.Changed += ValidateHotkeys;
            _hotkeys[action] = box;
            panel.Children.Add(Row(label, box));
        }
        var hkButtons = new WrapPanel { Margin = new Thickness(180, 4, 0, 0) };
        var defaults = new Button { Content = "Restore default shortcuts", Padding = new Thickness(8, 2, 8, 2) };
        defaults.Click += (_, _) =>
        {
            foreach (var d in AppSettings.DefaultHotkeys())
                if (_hotkeys.TryGetValue(d.Action, out var b)) ReplaceBox(d.Action, d.Gesture);
            ValidateHotkeys();
        };
        hkButtons.Children.Add(defaults);
        panel.Children.Add(hkButtons);
        panel.Children.Add(_hotkeyStatus);
        if (Application.Current.MainWindow is MainWindow mw && mw.LastHotkeyProblems.Count > 0)
            _hotkeyStatus.Text = "Currently: " + string.Join(" ", mw.LastHotkeyProblems);

        panel.Children.Add(Header("Output and library"));
        panel.Children.Add(Row("JPEG quality (1–100)", _jpeg));
        panel.Children.Add(Row("Keep at most (captures)", _maxCount));
        panel.Children.Add(Row("Keep at most (MiB)", _maxMb));

        panel.Children.Add(Header("Application"));
        panel.Children.Add(_tray);
        panel.Children.Add(_closeToTray);
        panel.Children.Add(_startWithWindows);
        panel.Children.Add(_snap);

        var folder = new Button { Content = "Open data folder", Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        folder.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Paths.Root}\"") { UseShellExecute = false }); }
            catch (System.ComponentModel.Win32Exception ex) { Dialogs.Error(this, ex.Message); }
        };
        panel.Children.Add(folder);

        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.9 };
    }

    /// <summary>Replaces one recorder (used by "Restore defaults").</summary>
    private void ReplaceBox(string action, string gesture)
    {
        var old = _hotkeys[action];
        if (old.Parent is not Grid g) return;
        var label = (g.Children[0] as Label)?.Content as string ?? action;
        var box = new HotkeyBox(gesture, label);
        box.Changed += ValidateHotkeys;
        Grid.SetColumn(box, 1);
        g.Children.Remove(old);
        g.Children.Add(box);
        _hotkeys[action] = box;
    }

    /// <summary>Flags duplicates as you record.</summary>
    private void ValidateHotkeys()
    {
        var dup = _hotkeys.Where(kv => kv.Value.Gesture.Length > 0)
            .GroupBy(kv => kv.Value.Gesture, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is used twice.").ToList();
        _hotkeyStatus.Text = string.Join(" ", dup);
    }

    private static TextBlock Header(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap };

    private static FrameworkElement Row(string label, FrameworkElement c)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var l = new Label { Content = label, Target = c, Padding = new Thickness(0, 3, 8, 0) };
        AutomationProperties.SetName(c, label);
        Grid.SetColumn(c, 1);
        g.Children.Add(l);
        g.Children.Add(c);
        return g;
    }

    private void Save()
    {
        var errors = new List<string>();
        int Int(TextBox b, string name, int min, int max)
        {
            if (int.TryParse(b.Text.Trim(), out var v) && v >= min && v <= max) return v;
            errors.Add($"{name} must be a whole number from {min} to {max}.");
            return min;
        }
        int jpeg = Int(_jpeg, "JPEG quality", 1, 100);
        int count = Int(_maxCount, "Library capture limit", 1, 10_000);
        int mb = Int(_maxMb, "Library size limit", 10, 100_000);

        var s = _services.Settings;
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (action, box) in _hotkeys)
        {
            var text = box.Gesture;
            if (text.Length == 0) { s = s.WithHotkey(action, ""); continue; }
            if (!HotkeyGesture.TryParse(text, out var g)) { errors.Add($"'{text}' is not a valid hotkey (use a modifier plus a key, e.g. Ctrl+Shift+5)."); continue; }
            var canonical = g.ToString();
            if (seen.TryGetValue(canonical, out var other)) errors.Add($"{canonical} is assigned to both {other} and {action}.");
            seen[canonical] = action;
            s = s.WithHotkey(action, canonical);
        }
        if (errors.Count > 0) { Dialogs.Error(this, string.Join("\n", errors)); return; }

        s = s with
        {
            CaptureDelaySeconds = _delay.SelectedItem is int d ? d : 0,
            DefaultDestination = _destination.SelectedItem is CaptureDestination cd ? cd : CaptureDestination.AppendBelow,
            IncludeCursor = _cursor.IsChecked == true,
            CopyAfterCapture = _copyAfter.IsChecked == true,
            SaveCapturesToHistory = _history.IsChecked == true,
            ShowTrayIcon = _tray.IsChecked == true,
            CloseToTray = _closeToTray.IsChecked == true && _tray.IsChecked == true,
            StartWithWindows = _startWithWindows.IsChecked == true,
            SnapEnabled = _snap.IsChecked == true,
            JpegQuality = jpeg,
            HistoryMaxCount = count,
            HistoryMaxMegabytes = mb,
        };
        if (s.StartWithWindows != _services.Settings.StartWithWindows && !MainWindow.ApplyStartWithWindows(s.StartWithWindows, out var err))
        {
            Dialogs.Error(this, $"Could not change Start with Windows: {err}");
            s = s with { StartWithWindows = _services.Settings.StartWithWindows };
        }
        _services.SaveSettings(s);
        DialogResult = true;
    }
}
