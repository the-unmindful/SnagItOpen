using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.App.Shell;

/// <summary>A six-second capture result that keeps the user's foreground application active.</summary>
internal sealed class DesktopToastWindow : Window
{
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Image _thumbnail;
    private readonly AppServices _services;
    private readonly CaptureItem? _item;
    private string? _outputPath;
    private Point _dragStart;

    public DesktopToastWindow(AppServices services, CaptureItem item, string title, Action edit, Action pin, string? outputPath = null)
    {
        _services = services; _item = item; _outputPath = outputPath;
        Title = "SnagItOpen capture result";
        Width = 350; Height = 144;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        FontFamily = new FontFamily("Segoe UI");
        SetResourceReference(BackgroundProperty, "Bg.Surface");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(this, title);
        var root = new DockPanel { Margin = new Thickness(12) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Add("Edit", () => { Close(); edit(); });
        Add("Pin", () => { Close(); pin(); });
        Add("Show in folder", ShowFolder);
        Add("Dismiss", Close);
        void Add(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 5, 0), Padding = new Thickness(6, 3, 6, 3), MinHeight = 28 };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        _thumbnail = new Image { Source = item.Pixels.ToBitmap(), Width = 96, Height = 76, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand };
        AutomationProperties.SetName(_thumbnail, "Capture thumbnail. Drag to another application.");
        _thumbnail.MouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(_thumbnail);
        _thumbnail.MouseMove += OnDrag;
        DockPanel.SetDock(_thumbnail, Dock.Left); root.Children.Add(_thumbnail);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        text.Children.Add(status);
        var dimensions = new TextBlock { Text = $"{item.Pixels.Width} × {item.Pixels.Height} px", Margin = new Thickness(0, 5, 0, 0) };
        dimensions.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary"); text.Children.Add(dimensions); root.Children.Add(text);
        var frame = new Border { Child = root, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Stroke.Control"); frame.SetResourceReference(Border.BackgroundProperty, "Bg.Surface"); Content = frame;
        SourceInitialized += (_, _) =>
        {
            AppNative.MakeNoActivate(this); AppNative.ExcludeFromCapture(this);
            var monitors = services.Monitors.GetMonitors();
            var monitor = MonitorTopology.Dominant(monitors, item.Bounds) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (monitor is null) return;
            int w = (int)Math.Ceiling(Width * monitor.Scale), h = (int)Math.Ceiling(Height * monitor.Scale), gap = (int)Math.Ceiling(16 * monitor.Scale);
            AppNative.PlaceTopmost(this, new PixelRect(Math.Max(monitor.WorkArea.X, monitor.WorkArea.Right - w - gap),
                Math.Max(monitor.WorkArea.Y, monitor.WorkArea.Bottom - h - gap), w, h), activate: false);
        };
        _dismiss.Tick += (_, _) => Close();
        Loaded += (_, _) => _dismiss.Start(); MouseEnter += (_, _) => _dismiss.Stop(); MouseLeave += (_, _) => _dismiss.Start();
    }

    /// <summary>Capture failures remain on the desktop without stealing focus. Details is an explicit user action.</summary>
    public static DesktopToastWindow ShowError(AppServices services, string message, Action details)
    {
        var window = new DesktopToastWindow(services, message, details);
        window.Show();
        return window;
    }

    private DesktopToastWindow(AppServices services, string message, Action details)
    {
        _services = services;
        _thumbnail = new Image();
        Title = "SnagItOpen error";
        Width = 350; Height = 148;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        FontFamily = new FontFamily("Segoe UI");
        SetResourceReference(BackgroundProperty, "Bg.Surface");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(this, message);
        var body = new StackPanel { Margin = new Thickness(14) };
        var heading = new TextBlock { Text = "Capture failed", FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Status.Error"); body.Children.Add(heading);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxHeight = 58, Margin = new Thickness(0, 6, 0, 8), ToolTip = message };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite); body.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var detail = new Button { Content = "Details", MinHeight = 28, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetName(detail, "Show full error details"); detail.Click += (_, _) => { Close(); details(); };
        var close = new Button { Content = "Dismiss", MinHeight = 28, Padding = new Thickness(8, 3, 8, 3) };
        AutomationProperties.SetName(close, "Dismiss error notification"); close.Click += (_, _) => Close();
        buttons.Children.Add(detail); buttons.Children.Add(close); body.Children.Add(buttons);
        var frame = new Border { Child = body, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Stroke.Control"); Content = frame;
        SourceInitialized += (_, _) =>
        {
            AppNative.MakeNoActivate(this); AppNative.ExcludeFromCapture(this);
            var monitors = services.Monitors.GetMonitors();
            var monitor = MonitorTopology.At(monitors, AppNative.CursorPosition()) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (monitor is null) return;
            int w = (int)Math.Ceiling(Width * monitor.Scale), h = (int)Math.Ceiling(Height * monitor.Scale), gap = (int)Math.Ceiling(16 * monitor.Scale);
            AppNative.PlaceTopmost(this, new PixelRect(Math.Max(monitor.WorkArea.X, monitor.WorkArea.Right - w - gap),
                Math.Max(monitor.WorkArea.Y, monitor.WorkArea.Bottom - h - gap), w, h), activate: false);
        };
        _dismiss.Tick += (_, _) => Close(); Loaded += (_, _) => _dismiss.Start();
        MouseEnter += (_, _) => _dismiss.Stop(); MouseLeave += (_, _) => _dismiss.Start();
    }

    private string EnsureFile()
    {
        if (_item is null) throw new InvalidOperationException("This notification has no image.");
        if (_outputPath is not null && File.Exists(_outputPath)) return _outputPath;
        string path = Path.Combine(_services.Paths.Temp, $"Capture-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, _item.Pixels.EncodePng());
        return _outputPath = path;
    }

    private void ShowFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{EnsureFile()}\"") { UseShellExecute = false }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { _services.Log(ex.Message); }
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_thumbnail);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        BeginDrag();
    }

    /// <summary>Continues the held mouse gesture from the capture overlay after its windows close.</summary>
    public void BeginDrag()
    {
        if (_item is not { } item) return;
        try
        {
            _dismiss.Stop();
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { EnsureFile() });
            data.SetData("PNG", new MemoryStream(item.Pixels.EncodePng()));
            DragDrop.DoDragDrop(_thumbnail, data, DragDropEffects.Copy);
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _services.Log(ex.Message); _dismiss.Start(); }
    }

    protected override void OnClosed(EventArgs e) { _dismiss.Stop(); _thumbnail.Source = null; base.OnClosed(e); }
}
