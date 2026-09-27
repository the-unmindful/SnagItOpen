using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnagItOpen.App.Shell;

/// <summary>
/// Always-on-top reference image. Drag to move, grip to resize, Ctrl+wheel or the context menu changes
/// opacity, Esc/Close closes and releases the bitmap. It hides with other app windows during capture.
/// </summary>
internal sealed class PinnedImageWindow : Window
{
    private readonly Image _image;

    public PinnedImageWindow(BitmapSource bitmap, string name)
    {
        Title = $"Pinned: {name}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetName(this, $"Pinned image {name}. Escape closes.");

        _image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Child = _image,
        };

        // Size to the bitmap's physical pixels, capped to 80% of the work area.
        Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double w = bitmap.PixelWidth / dpi.DpiScaleX + 2, h = bitmap.PixelHeight / dpi.DpiScaleY + 2;
            double maxW = SystemParameters.WorkArea.Width * 0.8, maxH = SystemParameters.WorkArea.Height * 0.8;
            double s = Math.Min(1, Math.Min(maxW / w, maxH / h));
            Width = Math.Max(80, w * s);
            Height = Math.Max(60, h * s);
        };

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        MouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1);
            e.Handled = true;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var menu = new ContextMenu();
        foreach (var o in new[] { 100, 75, 50, 30 })
        {
            var mi = new MenuItem { Header = $"Opacity {o}%" };
            mi.Click += (_, _) => Opacity = o / 100.0;
            menu.Items.Add(mi);
        }
        var top = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = true };
        top.Click += (_, _) => Topmost = top.IsChecked;
        menu.Items.Add(new Separator());
        menu.Items.Add(top);
        var close = new MenuItem { Header = "Close", InputGestureText = "Esc" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);
        ContextMenu = menu;
        ToolTip = "Drag to move · Ctrl+wheel changes opacity · right-click for options · Esc closes";
    }

    protected override void OnClosed(EventArgs e)
    {
        _image.Source = null;
        base.OnClosed(e);
    }
}
