using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;

namespace SnagItOpen.App.Shell;

internal sealed class PinnedImageWindow : Window
{
    private static readonly List<PinnedImageWindow> Instances = [];
    public static IReadOnlyList<PinnedImageWindow> OpenWindows => Instances.ToArray();
    public static event Action? Changed;
    public static void CloseAll() { foreach (var window in Instances.ToArray()) window.Close(); }
    public static void DisableClickThroughForAll() { foreach (var window in Instances.ToArray()) window.SetClickThrough(false); }

    private readonly BitmapSource _bitmap;
    private readonly Image _image;
    private readonly Border _frame;
    private readonly StackPanel _toolbar;
    private readonly Border _zoomPill;
    private readonly TextBlock _zoomLabel;
    private readonly AppServices? _services;
    private readonly Action? _edit;
    private readonly DispatcherTimer _zoomTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private Window? _recoveryTab;
    private double _zoom = 1;
    private bool _clickThrough;
    private bool _closed;
    private bool _initializedSize;
    public bool IsClickThrough => _clickThrough;

    public PinnedImageWindow(BitmapSource bitmap, string name, AppServices? services = null, Action? edit = null)
    {
        _bitmap = bitmap;
        _services = services;
        _edit = edit;
        Title = $"Pinned: {name}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        AutomationProperties.SetName(this, $"Pinned image {name}. Escape closes.");
        _image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var root = new Grid();
        root.Children.Add(_image);
        _toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4) };
        AddButton("Copy", "Icon.Copy", "Ctrl+C", () => _ = CopyAsync());
        AddButton("Open in editor", "Icon.Import", "E", () => _edit?.Invoke());
        var opacity = AddButton("Opacity", "Icon.More", "Alt+wheel", () => { });
        var opacityMenu = new ContextMenu();
        foreach (int value in new[] { 100, 75, 50, 25 })
        {
            var item = new MenuItem { Header = $"{value}%" };
            item.Click += (_, _) => Opacity = value / 100.0;
            opacityMenu.Items.Add(item);
        }
        opacity.Click += (_, _) => { opacityMenu.PlacementTarget = opacity; opacityMenu.IsOpen = true; };
        AddButton("Click-through", "Icon.Pin", "", () => SetClickThrough(!_clickThrough));
        AddButton("Close", "Icon.Close", "Esc", Close);
        var toolbarBorder = new Border { Child = _toolbar, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(4), Margin = new Thickness(3) };
        toolbarBorder.SetResourceReference(Border.BackgroundProperty, "Bg.Surface");
        root.Children.Add(toolbarBorder);
        _zoomLabel = new TextBlock { Margin = new Thickness(8, 3, 8, 3) };
        _zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _zoomPill = new Border { Child = _zoomLabel, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6), Visibility = Visibility.Collapsed };
        _zoomPill.SetResourceReference(Border.BackgroundProperty, "Bg.Surface");
        root.Children.Add(_zoomPill);
        _frame = new Border { Child = root, BorderThickness = new Thickness(1), Margin = new Thickness(8) };
        _frame.SetResourceReference(Border.BackgroundProperty, "Bg.Surface");
        RefreshShadow();
        if (ThemeService.Current is { } theme) theme.ThemeChanged += OnThemeChanged;
        Content = _frame;
        _zoomTimer.Tick += (_, _) => { _zoomTimer.Stop(); _zoomPill.Visibility = Visibility.Collapsed; };
        MouseEnter += (_, _) => UpdateChrome();
        MouseLeave += (_, _) => UpdateChrome();
        GotKeyboardFocus += (_, _) => UpdateChrome();
        LostKeyboardFocus += (_, _) => UpdateChrome();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && !IsButton(e.OriginalSource as DependencyObject)) DragMove();
        };
        MouseWheel += OnWheel;
        KeyDown += OnKeys;
        LocationChanged += (_, _) => PlaceRecoveryTab();
        SizeChanged += (_, _) => PlaceRecoveryTab();
        IsVisibleChanged += (_, _) => UpdateRecoveryVisibility();
        StateChanged += (_, _) => UpdateRecoveryVisibility();
        Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (!_initializedSize)
            {
                _initializedSize = true;
                double w = bitmap.PixelWidth / dpi.DpiScaleX + 18, h = bitmap.PixelHeight / dpi.DpiScaleY + 18;
                var monitor = services is null ? null : Core.Capture.MonitorTopology.At(services.Monitors.GetMonitors(), AppNative.CursorPosition());
                double maxW = monitor is null ? SystemParameters.WorkArea.Width : monitor.WorkArea.Width / dpi.DpiScaleX;
                double maxH = monitor is null ? SystemParameters.WorkArea.Height : monitor.WorkArea.Height / dpi.DpiScaleY;
                _zoom = Math.Clamp(Math.Min(1, Math.Min(maxW * 0.8 / w, maxH * 0.8 / h)), 0.1, 8);
                ResizeAt(_zoom, new Point(0, 0));
            }
            _frame.BorderThickness = new Thickness(1 / dpi.DpiScaleX);
            if (!Instances.Contains(this)) { Instances.Add(this); Changed?.Invoke(); }
            UpdateRecoveryVisibility();
            UpdateChrome();
        };
        ContextMenu = BuildMenu();
        UpdateChrome();

        IconButton AddButton(string label, string icon, string shortcut, Action action)
        {
            var button = new IconButton { Label = label, Shortcut = shortcut, ShowLabel = false, Margin = new Thickness(1) };
            button.SetResourceReference(IconButton.IconProperty, icon);
            button.Click += (_, _) => action();
            _toolbar.Children.Add(button);
            return button;
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Add(string label, string key, Action action) { var item = new MenuItem { Header = label, InputGestureText = key }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Copy", "Ctrl+C", () => _ = CopyAsync());
        Add("Save as PNG…", "Ctrl+S", Save);
        Add("Open in editor", "E", () => _edit?.Invoke());
        Add("Original size", "Ctrl+0", () => ResizeAt(1, new Point(ActualWidth / 2, ActualHeight / 2)));
        var opacity = new MenuItem { Header = "Opacity" };
        foreach (int value in new[] { 100, 75, 50, 25 }) { var item = new MenuItem { Header = $"{value}%" }; item.Click += (_, _) => Opacity = value / 100.0; opacity.Items.Add(item); }
        menu.Items.Add(opacity);
        Add("Toggle click-through", "", () => SetClickThrough(!_clickThrough));
        var top = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = true };
        top.Click += (_, _) => Topmost = top.IsChecked;
        menu.Items.Add(top);
        Add("Close", "Esc", Close);
        return menu;
    }

    private static bool IsButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void UpdateChrome()
    {
        bool visible = IsMouseOver || IsKeyboardFocusWithin;
        _toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) _frame.SetResourceReference(Border.BorderBrushProperty, "Accent.Select");
        else _frame.BorderBrush = Brushes.Transparent;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => RefreshShadow();
    private void RefreshShadow()
    {
        _frame.Effect = SystemParameters.HighContrast ? null : new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.22,
            Color = (TryFindResource("Text.Primary") as SolidColorBrush)?.Color ?? SystemColors.WindowTextColor };
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ResizeAt(Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 8), e.GetPosition(this));
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.1, 1);
            e.Handled = true;
        }
    }

    private void ResizeAt(double zoom, Point cursor)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = Math.Max(20, _bitmap.PixelWidth * zoom / dpi.DpiScaleX + 18), h = Math.Max(20, _bitmap.PixelHeight * zoom / dpi.DpiScaleY + 18);
        double factor = double.IsFinite(Width) && Width > 0 ? w / Width : 1;
        if (double.IsFinite(Left)) Left += cursor.X * (1 - factor);
        if (double.IsFinite(Top)) Top += cursor.Y * (1 - factor);
        Width = w; Height = h; _zoom = zoom;
        _zoomLabel.Text = $"{zoom * 100:0}%";
        _zoomPill.Visibility = Visibility.Visible;
        _zoomTimer.Stop(); _zoomTimer.Start();
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.C && ctrl) _ = CopyAsync();
        else if (e.Key == Key.S && ctrl) Save();
        else if (e.Key is Key.D0 or Key.NumPad0 && ctrl) ResizeAt(1, new Point(ActualWidth / 2, ActualHeight / 2));
        else if (e.Key == Key.E) _edit?.Invoke();
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int pixels = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            Left += e.Key == Key.Left ? -pixels / dpi.DpiScaleX : e.Key == Key.Right ? pixels / dpi.DpiScaleX : 0;
            Top += e.Key == Key.Up ? -pixels / dpi.DpiScaleY : e.Key == Key.Down ? pixels / dpi.DpiScaleY : 0;
        }
        else return;
        e.Handled = true;
    }

    private async Task CopyAsync()
    {
        try
        {
            if (_services is null) System.Windows.Clipboard.SetImage(_bitmap);
            else
            {
                var pixels = PixelBuffer.FromBitmap(_bitmap);
                var result = await _services.Clipboard.CopyImageAsync(pixels.FlattenOnto(Rgba32.White).ToBitmap(), pixels.EncodePng());
                if (!result.IsSuccess) Dialogs.Error(this, result.Message ?? "Could not copy the pinned image.");
            }
        }
        catch (ExternalException ex) { Dialogs.Error(this, ex.Message); }
    }

    private void Save()
    {
        var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "Pinned image.png", InitialDirectory = _services?.Settings.LastExportDirectory ?? "" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, PixelBuffer.FromBitmap(_bitmap).EncodePng());
            if (_services is not null) _services.SaveSettings(_services.Settings with { LastExportDirectory = Path.GetDirectoryName(dialog.FileName) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialogs.Error(this, ex.Message); }
    }

    public void SetClickThrough(bool enabled)
    {
        if (_closed || enabled == _clickThrough) return;
        _clickThrough = enabled;
        var handle = AppNative.Handle(this);
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(enabled ? style | 0x20 : style & ~0x20));
        if (enabled && _recoveryTab is null)
        {
            var button = new Button { Content = "Enable mouse", Padding = new Thickness(8, 3, 8, 3) };
            AutomationProperties.SetName(button, "Disable click-through for this pinned image");
            button.Click += (_, _) => SetClickThrough(false);
            var tab = new Window { Content = button, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, Width = 120, Height = 30 };
            _recoveryTab = tab;
            tab.SetResourceReference(BackgroundProperty, "Bg.Surface");
            tab.SourceInitialized += (_, _) => { AppNative.MakeNoActivate(tab); AppNative.ExcludeFromCapture(tab); };
            UpdateRecoveryVisibility();
        }
        else if (!enabled)
        {
            _recoveryTab?.Close();
            _recoveryTab = null;
        }
        Changed?.Invoke();
    }

    private void UpdateRecoveryVisibility()
    {
        if (_recoveryTab is not { } tab) return;
        if (!IsVisible || WindowState == WindowState.Minimized) { tab.Hide(); return; }
        if (!tab.IsVisible) tab.Show();
        PlaceRecoveryTab();
    }

    private void PlaceRecoveryTab()
    {
        if (_recoveryTab is not { IsVisible: true } tab || !IsLoaded) return;
        var point = PointToScreen(new Point(Math.Max(0, ActualWidth - 120), 0));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var monitors = _services?.Monitors.GetMonitors();
        var monitor = monitors is null ? null : Core.Capture.MonitorTopology.At(monitors, new PixelPoint((int)point.X, (int)point.Y));
        int w = (int)Math.Ceiling(120 * scale), h = (int)Math.Ceiling(30 * scale);
        int x = (int)point.X, y = (int)point.Y;
        if (monitor is not null)
        {
            x = Math.Clamp(x, monitor.WorkArea.X, Math.Max(monitor.WorkArea.X, monitor.WorkArea.Right - w));
            y = Math.Clamp(y, monitor.WorkArea.Y, Math.Max(monitor.WorkArea.Y, monitor.WorkArea.Bottom - h));
        }
        AppNative.PlaceTopmost(tab, new PixelRect(x, y, w, h), activate: false);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _clickThrough = false;
        _zoomTimer.Stop();
        if (ThemeService.Current is { } theme) theme.ThemeChanged -= OnThemeChanged;
        _recoveryTab?.Close();
        _recoveryTab = null;
        _image.Source = null;
        Instances.Remove(this);
        Changed?.Invoke();
        base.OnClosed(e);
    }
}
