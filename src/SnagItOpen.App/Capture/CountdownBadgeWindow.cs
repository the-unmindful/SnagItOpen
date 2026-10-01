using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.App.Capture;

/// <summary>Nonactivating 72 DIP countdown, with global Escape polling so open target menus keep focus.</summary>
internal sealed class CountdownBadgeWindow : Window
{
    private readonly RingSurface _surface;
    private bool _canceled;
    private int _lastSecond = -1;
    public CountdownBadgeWindow(MonitorInfo monitor)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = 144;
        Height = 112;
        Title = "SnagItOpen capture countdown";
        SetResourceReference(BackgroundProperty, "Bg.Surface");
        _surface = new RingSurface(this);
        Content = _surface;
        AutomationProperties.SetLiveSetting(_surface, AutomationLiveSetting.Polite);
        MouseRightButtonUp += (_, _) => _canceled = true;
        SourceInitialized += (_, _) =>
        {
            AppNative.MakeNoActivate(this);
            AppNative.ExcludeFromCapture(this);
            int w = (int)Math.Ceiling(Width * monitor.Scale), h = (int)Math.Ceiling(Height * monitor.Scale);
            int x = monitor.Bounds.X + (monitor.Bounds.Width - w) / 2;
            int y = monitor.Bounds.Y + (int)Math.Ceiling(20 * monitor.Scale);
            AppNative.PlaceTopmost(this, new PixelRect(x, y, w, h), activate: false);
        };
    }

    public bool CancellationRequested => _canceled || (GetAsyncKeyState(0x1B) & 0x8000) != 0;

    public void Update(double secondsRemaining, int totalSeconds)
    {
        int second = Math.Max(1, (int)Math.Ceiling(secondsRemaining));
        if (ThemeService.AnimationsEnabled || second != _lastSecond)
        {
            _surface.Seconds = second;
            _surface.Progress = ThemeService.AnimationsEnabled ? secondsRemaining / totalSeconds : second / (double)totalSeconds;
            _surface.InvalidateVisual();
        }
        if (second != _lastSecond)
        {
            AutomationProperties.SetName(_surface, $"Capturing in {second} seconds. Escape cancels.");
            _lastSecond = second;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private sealed class RingSurface(CountdownBadgeWindow owner) : FrameworkElement
    {
        public int Seconds { get; set; }
        public double Progress { get; set; } = 1;
        private Brush Token(string key) => owner.TryFindResource(key) as Brush ?? SystemColors.WindowTextBrush;
        protected override void OnRender(DrawingContext dc)
        {
            var center = new Point(72, 42);
            const double radius = 34;
            dc.DrawEllipse(null, new Pen(Token("Stroke.Divider"), 4), center, radius, radius);
            double progress = Math.Clamp(Progress, 0, 1);
            if (progress >= 0.9999) dc.DrawEllipse(null, new Pen(Token("Accent.Select"), 4), center, radius, radius);
            else if (progress > 0)
            {
                double angle = progress * Math.PI * 2;
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                    path.ArcTo(new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius),
                        new Size(radius, radius), 0, progress > 0.5, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(null, new Pen(Token("Accent.Select"), 4), geometry);
            }
            var digits = Text(Seconds.ToString(CultureInfo.InvariantCulture), 26);
            dc.DrawText(digits, new Point(center.X - digits.Width / 2, center.Y - digits.Height / 2));
            var hint = Text("Esc to cancel", 12);
            dc.DrawText(hint, new Point(center.X - hint.Width / 2, 86));
        }

        private FormattedText Text(string text, double size) => new(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Token("Text.Primary"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
