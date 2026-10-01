using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Documents;
using CaptureMode = SnagItOpen.Core.Capture.CaptureMode;

namespace SnagItOpen.App.Capture;

/// <summary>One frozen overlay per monitor. Geometry and sampling use physical pixels;
/// controls and chrome use the owning monitor's DIPs.</summary>
internal sealed class RegionOverlayWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly CaptureOverlayState _state;
    private readonly RegionSelection _selection;
    private readonly BitmapSource _snapshot;
    private readonly OverlaySurface _surface;
    private readonly Canvas _chrome = new();
    private readonly Border _modeStrip, _actionBar, _picker;
    private readonly TextBlock _hints = new() { Margin = new Thickness(6, 4, 6, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _announcement = new() { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
    private readonly List<Button> _modeButtons = [];
    private readonly Border _coachTip;
    private readonly DispatcherTimer _coachTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private Button? _edit;
    private string _lastAnnouncement = "";

    private sealed class OverlaySurface(RegionOverlayWindow owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => owner.RenderOverlay(dc);
    }

    public RegionOverlayWindow(MonitorInfo monitor, BitmapSource snapshot, CaptureOverlayState state)
    {
        _monitor = monitor;
        _snapshot = snapshot.Format == PixelFormats.Bgra32 ? snapshot : new FormatConvertedBitmap(snapshot, PixelFormats.Bgra32, null, 0);
        _snapshot.Freeze();
        _state = state;
        _selection = state.Selection;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Cursor = Cursors.Cross;
        Focusable = true;
        FontFamily = new FontFamily("Segoe UI");
        Title = "SnagItOpen capture selection";
        SetResourceReference(BackgroundProperty, "Bg.Surface");
        AutomationProperties.SetName(this, "Capture selection");
        AutomationProperties.SetLiveSetting(_announcement, AutomationLiveSetting.Polite);
        _surface = new OverlaySurface(this);
        var root = new Grid();
        root.Children.Add(_surface);
        root.Children.Add(_chrome);
        root.Children.Add(_announcement);
        _modeStrip = BuildModeStrip();
        _actionBar = BuildActionBar();
        _picker = BuildPicker();
        var coach = new TextBlock { Text = "Enter edits, Ctrl+C copies", FontSize = 12, Margin = new Thickness(4, 2, 4, 2) };
        coach.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(coach, AutomationLiveSetting.Polite);
        _coachTip = Chrome(coach);
        _coachTip.IsHitTestVisible = false;
        _coachTip.Visibility = Visibility.Collapsed;
        _coachTimer.Tick += (_, _) => { _coachTimer.Stop(); _coachTip.Visibility = Visibility.Collapsed; };
        _chrome.Children.Add(_modeStrip);
        _chrome.Children.Add(_actionBar);
        _chrome.Children.Add(_picker);
        _chrome.Children.Add(_coachTip);
        Content = root;
        _selection.Changed += OnChanged;
        _state.Changed += OnChanged;
        _state.ActionFocusRequested += FocusActions;
        _state.ColorCopyRequested += CopyColor;
        if (ThemeService.Current is { } theme) theme.ThemeChanged += OnThemeChanged;
        SourceInitialized += (_, _) =>
        {
            AppNative.MakeToolWindow(this);
            AppNative.ExcludeFromCapture(this);
            AppNative.PlaceTopmost(this, monitor.Bounds);
        };
        Loaded += (_, _) => OnChanged();
    }

    private double Scale => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? _monitor.Scale;
    private Brush Token(string key) => TryFindResource(key) as Brush ?? SystemColors.WindowTextBrush;
    private Pen Stroke(string key, double width, bool dashed = false) => new(Token(key), width) { DashStyle = dashed ? DashStyles.Dash : DashStyles.Solid };
    private Rect Local(PixelRect r) => new((r.X - _monitor.Bounds.X) / Scale, (r.Y - _monitor.Bounds.Y) / Scale, r.Width / Scale, r.Height / Scale);
    private Point Local(PointD p) => new((p.X - _monitor.Bounds.X) / Scale, (p.Y - _monitor.Bounds.Y) / Scale);

    private static Border Chrome(UIElement child)
    {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(6) };
        border.SetResourceReference(Border.BackgroundProperty, "Bg.Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Stroke.Control");
        return border;
    }

    private static Button MakeButton(string label, string shortcut, Action click, bool primary = false)
    {
        var button = new Button { Content = string.IsNullOrEmpty(shortcut) ? label : $"{label}  {shortcut}",
            Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4), MinHeight = 30,
            ToolTip = string.IsNullOrEmpty(shortcut) ? label : $"{label} ({shortcut})" };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAcceleratorKey(button, shortcut);
        AutomationProperties.SetHelpText(button, label);
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
        button.Click += (_, _) => click();
        return button;
    }

    private Border BuildModeStrip()
    {
        var panel = new StackPanel();
        var modes = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        string[] names = ["Region", "Window", "Monitor", "Ellipse", "Freehand", "Multi"];
        for (int i = 0; i < names.Length; i++)
        {
            int n = i + 1;
            var button = MakeButton(names[i], n.ToString(CultureInfo.InvariantCulture), () => _state.SwitchMode(n));
            button.Padding = new Thickness(6, 3, 6, 3);
            _modeButtons.Add(button);
            modes.Children.Add(button);
        }
        panel.Children.Add(modes);
        _hints.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        panel.Children.Add(_hints);
        return Chrome(panel);
    }

    private Border BuildActionBar()
    {
        var panel = new WrapPanel();
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        _edit = MakeButton("Edit", "Enter", () => Commit(CaptureOverlayAction.Edit));
        panel.Children.Add(_edit);
        panel.Children.Add(MakeButton("Copy", "Ctrl+C", () => Commit(CaptureOverlayAction.Copy), primary: true));
        panel.Children.Add(MakeButton("Save…", "Ctrl+S", () => Commit(CaptureOverlayAction.Save)));
        panel.Children.Add(MakeButton("Pin", "P", () => Commit(CaptureOverlayAction.Pin)));
        panel.Children.Add(MakeButton("Append", "A", () => Commit(_state.AppendRight ? CaptureOverlayAction.AppendRight : CaptureOverlayAction.AppendBelow)));
        var direction = MakeButton("Below ▾", "", () => { });
        var menu = new ContextMenu();
        foreach (var (name, right) in new[] { ("Append below", false), ("Append right", true) })
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => { _state.AppendRight = right; direction.Content = right ? "Right ▾" : "Below ▾"; };
            menu.Items.Add(item);
        }
        direction.Click += (_, _) => { menu.PlacementTarget = direction; menu.IsOpen = true; };
        panel.Children.Add(direction);
        var drag = MakeButton("Drag", "", () => { });
        drag.PreviewMouseLeftButtonDown += (_, e) => { Commit(CaptureOverlayAction.Drag); e.Handled = true; };
        panel.Children.Add(drag);
        panel.Children.Add(MakeButton("Cancel", "Esc", () => _selection.Key(SelectionKey.Escape)));
        return Chrome(panel);
    }

    private Border BuildPicker()
    {
        var panel = new StackPanel { MinWidth = 200 };
        var title = new TextBlock { Text = _state.Picker == CapturePicker.Aspect ? "Fixed aspect" : "Fixed size", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 3, 4, 6) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        panel.Children.Add(title);
        if (_state.Picker == CapturePicker.Aspect)
        {
            foreach (var (label, ratio) in new[] { ("1:1", 1.0), ("4:3", 4.0 / 3), ("16:9", 16.0 / 9), ("3:2", 1.5), ("9:16", 9.0 / 16) })
                panel.Children.Add(MakeButton(label, "", () => _state.ChooseAspect(ratio)));
            if (_state.LastCustomAspect is { } last)
                panel.Children.Add(MakeButton($"Last custom ({last:0.###}:1)", "", () => _state.ChooseAspect(last)));
        }
        else
        {
            foreach (var size in new[] { new PixelSize(640, 480), new PixelSize(800, 600), new PixelSize(1280, 720), new PixelSize(1920, 1080) })
                AddSize(size, $"{size.Width} × {size.Height}");
            if (_state.LastCustomSize is { } last) AddSize(last, $"Last custom ({last.Width} × {last.Height})");
        }
        void AddSize(PixelSize size, string label)
        {
            var button = MakeButton(label, "", () => _state.ChooseSize(size));
            button.IsEnabled = size.Width <= _selection.Desktop.Width && size.Height <= _selection.Desktop.Height;
            panel.Children.Add(button);
        }
        var custom = new StackPanel { Margin = new Thickness(4, 8, 4, 4) };
        var fields = new StackPanel { Orientation = Orientation.Horizontal };
        var first = new TextBox { Text = _state.Picker == CapturePicker.Aspect ? "16" : "1280", Width = 70, Margin = new Thickness(0, 0, 4, 0) };
        var second = new TextBox { Text = _state.Picker == CapturePicker.Aspect ? "9" : "720", Width = 70, Margin = new Thickness(4, 0, 0, 0) };
        AutomationProperties.SetName(first, _state.Picker == CapturePicker.Aspect ? "Aspect width" : "Custom width in physical pixels");
        AutomationProperties.SetName(second, _state.Picker == CapturePicker.Aspect ? "Aspect height" : "Custom height in physical pixels");
        fields.Children.Add(first);
        var separator = new TextBlock { Text = _state.Picker == CapturePicker.Aspect ? ":" : "×", VerticalAlignment = VerticalAlignment.Center };
        separator.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        fields.Children.Add(separator);
        fields.Children.Add(second);
        custom.Children.Add(fields);
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 240 };
        validation.SetResourceReference(TextBlock.ForegroundProperty, "Status.Error");
        custom.Children.Add(MakeButton("Use custom", "", () =>
        {
            if (!int.TryParse(first.Text, out int w) || !int.TryParse(second.Text, out int h) || w < 1 || h < 1 || w > Limits.MaxDimension || h > Limits.MaxDimension)
            { validation.Text = "Enter positive whole numbers."; return; }
            if (_state.Picker == CapturePicker.Aspect) _state.ChooseAspect(w / (double)h, custom: true);
            else if (w > _selection.Desktop.Width || h > _selection.Desktop.Height) validation.Text = "This size does not fit the desktop.";
            else _state.ChooseSize(new PixelSize(w, h), custom: true);
        }));
        custom.Children.Add(validation);
        panel.Children.Add(custom);
        var scrolling = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(200, _monitor.Bounds.Height / _monitor.Scale - 140) };
        return Chrome(scrolling);
    }

    private void Commit(CaptureOverlayAction action)
    {
        if (_selection.Phase != SelectionPhase.Adjusting) return;
        _state.Action = action;
        _selection.Key(SelectionKey.Enter);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _surface.InvalidateVisual();
    private void FocusActions()
    {
        if (_actionBar.Visibility != Visibility.Visible) return;
        Activate();
        _edit?.Focus();
    }

    private void CopyColor(bool rgb)
    {
        if (MonitorTopology.At(_state.Monitors, _selection.Cursor)?.Id != _monitor.Id) return;
        try { System.Windows.Clipboard.SetText(CapturePixelSampler.ColorText(_snapshot,
            _selection.Cursor.X - _monitor.Bounds.X, _selection.Cursor.Y - _monitor.Bounds.Y, rgb)); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void OnChanged()
    {
        if (!IsLoaded) return;
        bool cursorHere = _monitor.Bounds.Contains(_selection.Cursor.X, _selection.Cursor.Y);
        _modeStrip.Visibility = cursorHere ? Visibility.Visible : Visibility.Collapsed;
        _picker.Visibility = cursorHere && _state.Picker != CapturePicker.None ? Visibility.Visible : Visibility.Collapsed;
        bool adjustHere = _selection.Phase == SelectionPhase.Adjusting && _selection.Current is { } current
            && MonitorTopology.Dominant(_state.Monitors, current)?.Id == _monitor.Id;
        _actionBar.Visibility = adjustHere ? Visibility.Visible : Visibility.Collapsed;
        _coachTip.Visibility = adjustHere && DateTimeOffset.UtcNow < _state.CoachTipUntil ? Visibility.Visible : Visibility.Collapsed;
        if (_coachTip.Visibility == Visibility.Visible && !_coachTimer.IsEnabled) _coachTimer.Start();
        _modeStrip.MaxWidth = Math.Max(160, _surface.ActualWidth - 24);
        _modeStrip.Measure(new Size(_modeStrip.MaxWidth, double.PositiveInfinity));
        double mx = (_surface.ActualWidth - _modeStrip.DesiredSize.Width) / 2, my = 12;
        if (_selection.Current is { } selected && Local(selected).IntersectsWith(new Rect(mx, my, _modeStrip.DesiredSize.Width, _modeStrip.DesiredSize.Height)))
            my = Math.Max(12, _surface.ActualHeight - _modeStrip.DesiredSize.Height - 12);
        Canvas.SetLeft(_modeStrip, mx); Canvas.SetTop(_modeStrip, my);
        var cursor = Local(new PointD(_selection.Cursor.X, _selection.Cursor.Y));
        _modeStrip.Opacity = new Rect(mx - 20, my - 20, _modeStrip.DesiredSize.Width + 40, _modeStrip.DesiredSize.Height + 40).Contains(cursor) ? 0.3 : 1;
        _hints.Text = _selection.Phase switch
        {
            SelectionPhase.Adjusting => "Enter edits · Ctrl+C copies · Esc resets",
            SelectionPhase.Dragging => "Release selects · Arrows refine · Esc cancels",
            _ when _state.Mode == CaptureMode.MultiRegion => "Drag regions · Enter finishes · Backspace removes",
            _ when _state.Mode == CaptureMode.Window => "Click a window · Enter selects · Esc cancels",
            _ => "Drag to select · M toggles loupe · Esc cancels",
        };
        if (_state.Notice is { } notice && _selection.Phase == SelectionPhase.Idle) _hints.Text = $"{notice}\nDrag to select · Esc cancels";
        for (int i = 0; i < _modeButtons.Count; i++)
        {
            bool active = i switch { 1 => _state.Mode == CaptureMode.Window, 2 => _state.Mode == CaptureMode.Monitor,
                3 => _selection.Shape == CaptureShape.Ellipse, 4 => _selection.Shape == CaptureShape.Freehand,
                5 => _state.Mode == CaptureMode.MultiRegion, _ => _state.Mode == CaptureMode.Region && _selection.Shape == CaptureShape.Rectangle };
            _modeButtons[i].SetResourceReference(Control.BackgroundProperty, active ? "Bg.Selected" : "Bg.Control");
        }
        if (adjustHere && _selection.Current is { } rect)
        {
            _actionBar.MaxWidth = Math.Max(160, _surface.ActualWidth - 16);
            _actionBar.Measure(new Size(_actionBar.MaxWidth, double.PositiveInfinity));
            var size = new PixelSize((int)Math.Ceiling(_actionBar.DesiredSize.Width * Scale), (int)Math.Ceiling(_actionBar.DesiredSize.Height * Scale));
            var placed = Local(CaptureChromePlacement.ActionBar(rect, _monitor.Bounds, size, (int)Math.Ceiling(8 * Scale)));
            Canvas.SetLeft(_actionBar, placed.X); Canvas.SetTop(_actionBar, placed.Y);
            if (_coachTip.Visibility == Visibility.Visible)
            {
                _coachTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var coachSize = new PixelSize((int)Math.Ceiling(_coachTip.DesiredSize.Width * Scale), (int)Math.Ceiling(_coachTip.DesiredSize.Height * Scale));
                var barRect = new PixelRect((int)Math.Round(placed.X * Scale) + _monitor.Bounds.X,
                    (int)Math.Round(placed.Y * Scale) + _monitor.Bounds.Y, size.Width, size.Height);
                var coachAt = Local(CaptureChromePlacement.ActionBar(barRect, _monitor.Bounds, coachSize, (int)Math.Ceiling(6 * Scale)));
                Canvas.SetLeft(_coachTip, coachAt.X); Canvas.SetTop(_coachTip, coachAt.Y);
            }
        }
        _picker.Measure(new Size(Math.Max(200, _surface.ActualWidth - 32), Math.Max(100, _surface.ActualHeight - 100)));
        Canvas.SetLeft(_picker, Math.Max(8, (_surface.ActualWidth - _picker.DesiredSize.Width) / 2));
        Canvas.SetTop(_picker, Math.Max(90, (_surface.ActualHeight - _picker.DesiredSize.Height) / 2));
        string announcement = $"{_state.ModeName} capture. {_selection.Phase}. {_hints.Text.Replace(" · ", ". ")}";
        if (_lastAnnouncement != announcement)
        {
            _announcement.Text = announcement;
            AutomationProperties.SetName(_announcement, announcement);
            AutomationProperties.SetName(this, announcement);
            UIElementAutomationPeer.FromElement(_announcement)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            _lastAnnouncement = announcement;
        }
        _surface.InvalidateVisual();
    }

    private Geometry Outline(PixelRect rect)
    {
        var local = Local(rect);
        if (_selection.Shape == CaptureShape.Ellipse) return new EllipseGeometry(local);
        if (_selection.Shape == CaptureShape.Freehand && _selection.Path.Count > 2) return Freehand(true);
        return new RectangleGeometry(local);
    }

    private StreamGeometry Freehand(bool closed)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(Local(_selection.Path[0]), closed, closed);
            for (int i = 1; i < _selection.Path.Count; i++) path.LineTo(Local(_selection.Path[i]), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void RenderOverlay(DrawingContext dc)
    {
        var full = new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight);
        dc.DrawImage(_snapshot, full);
        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(full));
        foreach (var committed in _selection.Committed) dim.Children.Add(new RectangleGeometry(Local(committed)));
        if (_selection.Current is { } current && (_selection.Shape != CaptureShape.Freehand || _selection.Path.Count > 2)) dim.Children.Add(Outline(current));
        else if (_state.Mode == CaptureMode.Monitor) dim.Children.Add(new RectangleGeometry(full));
        else if (_selection.Hover is { } hover) dim.Children.Add(new RectangleGeometry(Local(hover)));
        dc.DrawGeometry(Token("Overlay.Dim"), null, dim);
        foreach (var committed in _selection.Committed) dc.DrawRectangle(null, Stroke("Accent.Select", 2 / Scale, true), Local(committed));
        if (_selection.Phase == SelectionPhase.Dragging && _selection.Shape == CaptureShape.Freehand && _selection.Path.Count > 1)
        {
            dc.DrawGeometry(null, Stroke("Accent.Select", 2 / Scale), Freehand(false));
            dc.DrawLine(Stroke("Accent.Select", 2 / Scale, true), Local(_selection.Path[^1]), Local(_selection.Path[0]));
        }
        else if (_selection.Current is { } r)
        {
            dc.DrawGeometry(null, Stroke("Accent.Select", 2 / Scale), Outline(r));
            var local = Local(r);
            DrawLabel(dc, $"{r.Width} × {r.Height}", new Point(local.X, local.Y - 25));
            if (_selection.Phase == SelectionPhase.Adjusting)
            {
                foreach (var (_, p) in RegionSelection.Handles(r))
                {
                    var at = Local(new PointD(p.X, p.Y));
                    dc.DrawRectangle(Token("Handle.Fill"), Stroke("Accent.Select", 1.5), new Rect(at.X - 3.5, at.Y - 3.5, 7, 7));
                }
                (Point a, Point b) = _selection.ActiveEdge switch
                {
                    SelectionHandle.Top => (local.TopLeft, local.TopRight), SelectionHandle.Left => (local.TopLeft, local.BottomLeft),
                    SelectionHandle.Bottom => (local.BottomLeft, local.BottomRight), _ => (local.TopRight, local.BottomRight),
                };
                dc.DrawLine(Stroke("Accent.Select", 3), a, b);
            }
        }
        else if (_state.Mode != CaptureMode.Monitor && _selection.Hover is { } h)
        {
            var local = Local(h);
            dc.DrawRectangle(null, Stroke("Accent.Select", 2 / Scale), local);
            string title = _state.Windows.FirstOrDefault(w => w.Bounds == h)?.Title ?? "Window";
            if (title.Length > 40) title = title[..39] + "…";
            DrawLabel(dc, $"{title}  {h.Width} × {h.Height}", new Point(local.X + 4, local.Y + 4));
        }
        if (!_monitor.Bounds.Contains(_selection.Cursor.X, _selection.Cursor.Y)) return;
        var cursor = Local(new PointD(_selection.Cursor.X + 0.5, _selection.Cursor.Y + 0.5));
        if (_selection.Phase == SelectionPhase.Idle && _state.Picker == CapturePicker.None)
        {
            dc.DrawLine(Stroke("Text.OnAccent", 1), new Point(0, cursor.Y), new Point(full.Width, cursor.Y));
            dc.DrawLine(Stroke("Text.OnAccent", 1), new Point(cursor.X, 0), new Point(cursor.X, full.Height));
        }
        if (_state.ShowLoupe && !_selection.IsMovingBody && _state.Picker == CapturePicker.None) DrawLoupe(dc);
    }

    private void DrawLoupe(DrawingContext dc)
    {
        var cursor = _selection.Cursor;
        var placed = Local(CaptureChromePlacement.Loupe(cursor, _monitor.Bounds,
            new PixelSize((int)Math.Ceiling(128 * Scale), (int)Math.Ceiling(185 * Scale)), (int)Math.Ceiling(24 * Scale)));
        dc.DrawRoundedRectangle(Token("Bg.Surface"), Stroke("Stroke.Control", 1), placed, 6, 6);
        var box = new Rect(placed.X + 4, placed.Y + 4, 120, 120);
        int px = cursor.X - _monitor.Bounds.X, py = cursor.Y - _monitor.Bounds.Y;
        int x = Math.Max(0, px - 7), y = Math.Max(0, py - 7);
        int w = Math.Min(15 - (x - (px - 7)), _snapshot.PixelWidth - x);
        int h = Math.Min(15 - (y - (py - 7)), _snapshot.PixelHeight - y);
        var crop = new CroppedBitmap(_snapshot, new Int32Rect(x, y, w, h));
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.NearestNeighbor);
        dc.PushClip(new RectangleGeometry(box));
        dc.DrawImage(crop, new Rect(box.X + (x - (px - 7)) * 8, box.Y + (y - (py - 7)) * 8, w * 8, h * 8));
        for (int i = 0; i <= 15; i++)
        {
            dc.DrawLine(Stroke("Stroke.Divider", 0.5), new Point(box.X + i * 8, box.Y), new Point(box.X + i * 8, box.Bottom));
            dc.DrawLine(Stroke("Stroke.Divider", 0.5), new Point(box.X, box.Y + i * 8), new Point(box.Right, box.Y + i * 8));
        }
        dc.DrawRectangle(null, Stroke("Accent.Select", 2), new Rect(box.X + 56, box.Y + 56, 8, 8));
        dc.Pop();
        string size = _selection.Current is { } r ? $"{r.Width} × {r.Height}" : "";
        DrawText(dc, $"{cursor.X}, {cursor.Y}   {size}", new Point(placed.X + 6, placed.Y + 130), 11);
        DrawText(dc, CapturePixelSampler.ColorText(_snapshot, px, py), new Point(placed.X + 6, placed.Y + 148), 12);
        DrawText(dc, "C copies · M hides", new Point(placed.X + 6, placed.Y + 166), 10);
    }

    private void DrawText(DrawingContext dc, string text, Point at, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Token("Text.Primary"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, Token("Text.Primary"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double x = Math.Clamp(at.X, 2, Math.Max(2, _surface.ActualWidth - formatted.Width - 12));
        double y = Math.Clamp(at.Y, 2, Math.Max(2, _surface.ActualHeight - formatted.Height - 8));
        dc.DrawRoundedRectangle(Token("Bg.Surface"), Stroke("Stroke.Control", 1), new Rect(x, y, formatted.Width + 10, formatted.Height + 4), 3, 3);
        dc.DrawText(formatted, new Point(x + 5, y + 2));
    }

    private bool IsChrome(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, _modeStrip) || ReferenceEquals(source, _actionBar) || ReferenceEquals(source, _picker)) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _selection.Move(AppNative.CursorPosition());
        if (!IsChrome(e.OriginalSource as DependencyObject)) e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsChrome(e.OriginalSource as DependencyObject) || _state.Picker != CapturePicker.None) return;
        var point = AppNative.CursorPosition();
        if (_state.Mode == CaptureMode.Monitor) { _selection.Confirm(_monitor.Bounds); e.Handled = true; return; }
        if (e.ClickCount == 2 && _selection.Current is { } r && r.Contains(point.X, point.Y))
        { Commit(CaptureOverlayAction.Edit); e.Handled = true; return; }
        CaptureMouse();
        var handle = _selection.HitTestHandle(point, (int)Math.Ceiling(6 * Scale));
        if (handle != SelectionHandle.None) _selection.BeginAdjust(handle, point);
        else _selection.Down(point);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsChrome(e.OriginalSource as DependencyObject)) return;
        _selection.Up(AppNative.CursorPosition());
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        _selection.Key(_selection.Phase == SelectionPhase.Dragging ? SelectionKey.Backspace : SelectionKey.Escape);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if ((_selection.Phase is SelectionPhase.Dragging or SelectionPhase.Adjusting) && Mouse.LeftButton != MouseButtonState.Pressed)
            _selection.Up(AppNative.CursorPosition());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button focused && IsChrome(focused))
        {
            focused.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
            return;
        }
        if (_state.Picker != CapturePicker.None && e.Key != Key.Escape && Keyboard.FocusedElement is TextBox) { base.OnKeyDown(e); return; }
        if (e.Key >= Key.D1 && e.Key <= Key.D6) { _state.SwitchMode((int)e.Key - (int)Key.D1 + 1); e.Handled = true; return; }
        if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad6) { _state.SwitchMode((int)e.Key - (int)Key.NumPad1 + 1); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape: _selection.Key(SelectionKey.Escape); break;
            case Key.Enter:
                if (_state.Picker != CapturePicker.None) return;
                if (_state.Mode == CaptureMode.Monitor) _selection.Confirm(MonitorTopology.At(_state.Monitors, _selection.Cursor)?.Bounds ?? _monitor.Bounds);
                else _selection.Key(SelectionKey.Enter);
                break;
            case Key.Back: _selection.Key(SelectionKey.Backspace); break;
            case Key.M: _state.ToggleLoupe(); break;
            case Key.C:
                if (ctrl && _selection.Phase == SelectionPhase.Adjusting) Commit(CaptureOverlayAction.Copy);
                else if (!ctrl) _state.CopyColor(shift);
                break;
            case Key.S when ctrl: Commit(CaptureOverlayAction.Save); break;
            case Key.P: Commit(CaptureOverlayAction.Pin); break;
            case Key.A: Commit(_state.AppendRight ? CaptureOverlayAction.AppendRight : CaptureOverlayAction.AppendBelow); break;
            case Key.Tab:
                if (_selection.Phase != SelectionPhase.Adjusting) return;
                _selection.Key(SelectionKey.Tab);
                if (Keyboard.FocusedElement == this) { _state.FocusActions(); e.Handled = true; }
                base.OnKeyDown(e);
                return;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var key = e.Key switch { Key.Left => SelectionKey.Left, Key.Right => SelectionKey.Right, Key.Up => SelectionKey.Up, _ => SelectionKey.Down };
                if (_selection.Phase == SelectionPhase.Adjusting) _selection.Key(key, shift, ctrl);
                else
                {
                    var p = _selection.Cursor;
                    int step = shift ? 10 : 1;
                    var next = e.Key switch { Key.Left => p with { X = p.X - step }, Key.Right => p with { X = p.X + step },
                        Key.Up => p with { Y = p.Y - step }, _ => p with { Y = p.Y + step } };
                    _selection.Move(next);
                    AppNative.MoveCursor(_selection.Cursor);
                }
                break;
            case Key.Space:
                if (_selection.Phase == SelectionPhase.Dragging) _selection.Up(_selection.Cursor);
                else if (_selection.Phase != SelectionPhase.Adjusting) _selection.Down(_selection.Cursor);
                else return;
                break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _coachTimer.Stop();
        _selection.Changed -= OnChanged;
        _state.Changed -= OnChanged;
        _state.ActionFocusRequested -= FocusActions;
        _state.ColorCopyRequested -= CopyColor;
        if (ThemeService.Current is { } theme) theme.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }
}
