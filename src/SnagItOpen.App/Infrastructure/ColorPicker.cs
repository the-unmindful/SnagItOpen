using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;

namespace SnagItOpen.App.Infrastructure;

/// <summary>
/// Pop-up colour picker: saturation/value square, hue and transparency sliders, hex and RGB boxes,
/// before/after preview, palette and recent swatches, optional "No colour", and a screen eyedropper.
/// <c>preview</c> fires continuously while choosing; <c>commit</c> fires once when the pop-up closes with
/// a changed value; Esc (or "Cancel") restores the original via <c>cancel</c>.
/// </summary>
public static class ColorPicker
{
    public static readonly Rgba32[] Palette =
    [
        new(230, 40, 40, 255), new(245, 130, 0, 255), new(255, 210, 0, 255), new(40, 170, 70, 255), new(0, 150, 150, 255),
        new(0, 120, 215, 255), new(130, 60, 200, 255), new(220, 60, 150, 255), new(120, 80, 40, 255),
        new(0, 0, 0, 255), new(90, 90, 90, 255), new(170, 170, 170, 255), new(230, 230, 230, 255), new(255, 255, 255, 255),
    ];

    private static Brush? _checker;

    /// <summary>Checkerboard used behind translucent colours.</summary>
    public static Brush Checker
    {
        get
        {
            if (_checker is not null) return _checker;
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            var dark = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
            g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
            var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
            b.Freeze();
            return _checker = b;
        }
    }

    /// <summary>A swatch button showing <paramref name="c"/> (checker behind translucency, slash for none).</summary>
    public static Border Chip(Rgba32? c, double size = 18)
    {
        var inner = new Border { Background = c is { } v ? new SolidColorBrush(v.ToColor()) : Brushes.Transparent };
        if (c is null)
            inner.Child = new Line { X1 = 1, Y1 = size - 3, X2 = size - 3, Y2 = 1, Stroke = Brushes.Red, StrokeThickness = 1.5 };
        return new Border
        {
            Width = size, Height = size, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Background = c is { A: < 255 } ? Checker : Brushes.White, Child = inner,
        };
    }

    public static void Open(FrameworkElement anchor, Rgba32? initial, bool allowNone, IReadOnlyList<Rgba32> recent,
        Action<Rgba32?> preview, Action<Rgba32?> commit, Action cancel)
    {
        var view = new PickerView(initial, allowNone, recent);
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = SystemColors.WindowBrush, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Child = view,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.35 },
            },
        };
        bool canceled = false;
        view.Changed += c => preview(c);
        view.Done += ok => { canceled = !ok; popup.IsOpen = false; };
        view.PickingChanged += picking => popup.StaysOpen = picking;
        popup.Closed += (_, _) =>
        {
            view.StopPicking();
            if (canceled || view.Value == initial) cancel();
            else commit(view.Value);
            anchor.Focus();
        };
        popup.IsOpen = true;
        view.FocusFirst();
    }

    private sealed class PickerView : StackPanel
    {
        private const double SquareSize = 180;
        private readonly Rgba32? _initial;
        private readonly Grid _square;
        private readonly Rectangle _hueLayer;
        private readonly Ellipse _thumb;
        private readonly Slider _hue, _alpha;
        private readonly Rectangle _alphaTrack;
        private readonly TextBox _hex, _r, _g, _b;
        private readonly Border _after;
        private readonly Button _pick;
        private double _h, _s, _v;
        private byte _a = 255;
        private bool _none, _updating, _picking;

        public event Action<Rgba32?>? Changed;
        public event Action<bool>? Done;
        public event Action<bool>? PickingChanged;

        public Rgba32? Value => _none ? null : ColorMath.FromHsv(_h, _s, _v, _a);

        public PickerView(Rgba32? initial, bool allowNone, IReadOnlyList<Rgba32> recent)
        {
            _initial = initial;
            Width = 236;
            AutomationProperties.SetName(this, "Colour picker");
            var start = initial ?? new Rgba32(230, 40, 40, 255);
            (_h, _s, _v) = ColorMath.ToHsv(start);
            _a = start.A;
            _none = initial is null && allowNone;

            // Saturation / value square.
            _hueLayer = new Rectangle();
            var white = new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
            var black = new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
            _thumb = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
            var thumbLayer = new Canvas { IsHitTestVisible = false };
            thumbLayer.Children.Add(_thumb);
            _square = new Grid { Width = SquareSize + 36, Height = SquareSize, Cursor = Cursors.Cross, Focusable = true, ClipToBounds = true };
            _square.Children.Add(_hueLayer);
            _square.Children.Add(white);
            _square.Children.Add(black);
            _square.Children.Add(thumbLayer);
            AutomationProperties.SetName(_square, "Saturation and brightness. Arrow keys adjust.");
            _square.MouseLeftButtonDown += (_, e) => { _square.CaptureMouse(); _square.Focus(); FromSquare(e.GetPosition(_square)); e.Handled = true; };
            _square.MouseMove += (_, e) => { if (_square.IsMouseCaptured) FromSquare(e.GetPosition(_square)); };
            _square.MouseLeftButtonUp += (_, _) => _square.ReleaseMouseCapture();
            _square.KeyDown += (_, e) =>
            {
                double st = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.02;
                switch (e.Key)
                {
                    case Key.Left: _s = Math.Max(0, _s - st); break;
                    case Key.Right: _s = Math.Min(1, _s + st); break;
                    case Key.Up: _v = Math.Min(1, _v + st); break;
                    case Key.Down: _v = Math.Max(0, _v - st); break;
                    default: return;
                }
                _none = false; Sync(); e.Handled = true;
            };
            Children.Add(_square);

            // Hue.
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(ColorMath.FromHsv(i * 60, 1, 1).ToColor(), i / 6.0));
            _hue = TrackSlider(0, 360, "Hue", rainbow, out _);
            _hue.ValueChanged += (_, _) => { if (_updating) return; _h = _hue.Value; _none = false; Sync(); };
            Children.Add(_hue.Parent as FrameworkElement ?? _hue);

            // Transparency.
            _alpha = TrackSlider(0, 255, "Opacity", null, out _alphaTrack);
            _alpha.ValueChanged += (_, _) => { if (_updating) return; _a = (byte)Math.Round(_alpha.Value); _none = false; Sync(); };
            Children.Add(_alpha.Parent as FrameworkElement ?? _alpha);

            // Hex / RGB and before/after.
            var fields = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var w in new[] { 1.6, 1, 1, 1, 1.2 }) fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            _hex = Field("Hex", 9); _r = Field("R", 3); _g = Field("G", 3); _b = Field("B", 3);
            AddField(fields, "Hex", _hex, 0); AddField(fields, "R", _r, 1); AddField(fields, "G", _g, 2); AddField(fields, "B", _b, 3);
            var compare = new StackPanel { Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
            var before = Chip(initial, 16); before.Width = 36; before.ToolTip = "Before";
            _after = Chip(Value, 16); _after.Width = 36; _after.ToolTip = "After";
            compare.Children.Add(before);
            compare.Children.Add(_after);
            Grid.SetColumn(compare, 4);
            fields.Children.Add(compare);
            Children.Add(fields);
            _hex.LostKeyboardFocus += (_, _) => FromHex();
            _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FromHex(); e.Handled = true; } };
            foreach (var tb in new[] { _r, _g, _b })
            {
                tb.LostKeyboardFocus += (_, _) => FromRgb();
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FromRgb(); e.Handled = true; } };
            }

            // Swatches.
            Children.Add(SwatchRow("Colours", Palette));
            if (recent.Count > 0) Children.Add(SwatchRow("Recent", recent.Take(14).ToArray()));

            // Buttons.
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            _pick = SmallButton("Eyedropper", "Pick a colour from anywhere on screen (Esc cancels)", StartPicking);
            row.Children.Add(_pick);
            if (allowNone) row.Children.Add(SmallButton("No colour", "Remove this colour", () => { _none = true; Sync(); }));
            row.Children.Add(SmallButton("Done", "Keep this colour (Enter)", () => Done?.Invoke(true)));
            row.Children.Add(SmallButton("Cancel", "Restore the original colour (Esc)", () => Done?.Invoke(false)));
            Children.Add(row);

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { if (_picking) StopPicking(); else Done?.Invoke(false); e.Handled = true; }
                else if (e.Key == Key.Enter && e.OriginalSource is not TextBox) { Done?.Invoke(true); e.Handled = true; }
            };
            Sync(raise: false);
        }

        public void FocusFirst() => Dispatcher.BeginInvoke(() => _square.Focus(), System.Windows.Threading.DispatcherPriority.Input);

        private static Slider TrackSlider(double min, double max, string name, Brush? track, out Rectangle trackRect)
        {
            var s = new Slider { Minimum = min, Maximum = max, SmallChange = 1, LargeChange = (max - min) / 10, Margin = new Thickness(0), IsMoveToPointEnabled = true };
            AutomationProperties.SetName(s, name);
            trackRect = new Rectangle { Height = 10, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, RadiusX = 3, RadiusY = 3, Fill = track };
            var back = new Border { Height = 10, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Background = Checker, CornerRadius = new CornerRadius(3) };
            var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            g.Children.Add(back);
            g.Children.Add(trackRect);
            g.Children.Add(s);
            return s;
        }

        private static TextBox Field(string name, int max)
        {
            var t = new TextBox { MaxLength = max, Margin = new Thickness(0, 0, 3, 0) };
            AutomationProperties.SetName(t, name);
            return t;
        }

        private static void AddField(Grid g, string label, TextBox tb, int col)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10, Opacity = 0.7 });
            sp.Children.Add(tb);
            Grid.SetColumn(sp, col);
            g.Children.Add(sp);
        }

        private FrameworkElement SwatchRow(string title, IReadOnlyList<Rgba32> colors)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 10, Opacity = 0.7 });
            var wrap = new WrapPanel();
            foreach (var c in colors)
            {
                var b = new Button { Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 2), Content = Chip(c, 14), ToolTip = c.ToHex(), BorderThickness = new Thickness(0) };
                AutomationProperties.SetName(b, "Colour " + c.ToHex());
                b.Click += (_, _) => { (_h, _s, _v) = ColorMath.ToHsv(c); _a = c.A; _none = false; Sync(); };
                wrap.Children.Add(b);
            }
            sp.Children.Add(wrap);
            return sp;
        }

        private static Button SmallButton(string text, string tip, Action a)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4) };
            b.Click += (_, _) => a();
            return b;
        }

        private void FromSquare(Point p)
        {
            _s = Math.Clamp(p.X / _square.ActualWidth, 0, 1);
            _v = 1 - Math.Clamp(p.Y / _square.ActualHeight, 0, 1);
            _none = false;
            Sync();
        }

        private void FromHex()
        {
            if (_updating) return;
            if (Rgba32.TryParse(_hex.Text, out var c)) { (_h, _s, _v) = ColorMath.ToHsv(c); _a = c.A; _none = false; }
            Sync();
        }

        private void FromRgb()
        {
            if (_updating) return;
            static byte B(TextBox t, byte fallback) => byte.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            var cur = ColorMath.FromHsv(_h, _s, _v, _a);
            var c = new Rgba32(B(_r, cur.R), B(_g, cur.G), B(_b, cur.B), _a);
            var (h, s, v) = ColorMath.ToHsv(c);
            // Keep the hue when the new colour is grey.
            _h = s < 1e-6 ? _h : h; _s = s; _v = v; _none = false;
            Sync();
        }

        private void Sync(bool raise = true)
        {
            _updating = true;
            try
            {
                var c = ColorMath.FromHsv(_h, _s, _v, _a);
                _hueLayer.Fill = new SolidColorBrush(ColorMath.FromHsv(_h, 1, 1).ToColor());
                double w = _square.Width, hgt = _square.Height;
                Canvas.SetLeft(_thumb, _s * w - 6);
                Canvas.SetTop(_thumb, (1 - _v) * hgt - 6);
                _thumb.Stroke = _v > 0.6 && _s < 0.4 ? Brushes.Black : Brushes.White;
                _hue.Value = _h;
                _alpha.Value = _a;
                _alphaTrack.Fill = new LinearGradientBrush(c.WithAlpha(0).ToColor(), c.WithAlpha(255).ToColor(), 0);
                if (!_hex.IsKeyboardFocused) _hex.Text = _none ? "" : c.A == 255 ? c.ToHex()[..7] : c.ToHex();
                if (!_r.IsKeyboardFocused) _r.Text = _none ? "" : c.R.ToString(CultureInfo.InvariantCulture);
                if (!_g.IsKeyboardFocused) _g.Text = _none ? "" : c.G.ToString(CultureInfo.InvariantCulture);
                if (!_b.IsKeyboardFocused) _b.Text = _none ? "" : c.B.ToString(CultureInfo.InvariantCulture);
                var after = Chip(Value, 16);
                var inner = after.Child;
                after.Child = null; // detach from the temporary chip before re-parenting
                _after.Background = after.Background;
                _after.Child = inner;
            }
            finally { _updating = false; }
            if (raise) Changed?.Invoke(Value);
        }

        // ------------------------------------------------------------------ eyedropper

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

        private static Rgba32? ScreenPixel(PixelPoint p)
        {
            var dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                uint v = GetPixel(dc, p.X, p.Y);
                return v == 0xFFFFFFFF ? null : new Rgba32((byte)v, (byte)(v >> 8), (byte)(v >> 16), 255);
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        private void StartPicking()
        {
            if (_picking) return;
            _picking = true;
            PickingChanged?.Invoke(true);
            _pick.Content = "Click a colour…";
            Mouse.Capture(this, CaptureMode.SubTree);
            Mouse.OverrideCursor = Cursors.Cross;
            PreviewMouseDown += OnPickDown;
            PreviewMouseMove += OnPickMove;
            LostMouseCapture += OnPickLost;
        }

        private void OnPickMove(object sender, MouseEventArgs e)
        {
            if (ScreenPixel(AppNative.CursorPosition()) is { } c) { _after.Background = new SolidColorBrush(c.ToColor()); _after.Child = null; }
        }

        private void OnPickDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var c = e.ChangedButton == MouseButton.Left ? ScreenPixel(AppNative.CursorPosition()) : null;
            StopPicking();
            if (c is { } v) { (_h, _s, _v) = ColorMath.ToHsv(v); _a = 255; _none = false; }
            Sync();
        }

        private void OnPickLost(object sender, MouseEventArgs e) { if (_picking) { StopPicking(); Sync(raise: false); } }

        public void StopPicking()
        {
            if (!_picking) return;
            _picking = false;
            PreviewMouseDown -= OnPickDown;
            PreviewMouseMove -= OnPickMove;
            LostMouseCapture -= OnPickLost;
            Mouse.OverrideCursor = null;
            if (Mouse.Captured == this) Mouse.Capture(null);
            _pick.Content = "Eyedropper";
            PickingChanged?.Invoke(false);
        }
    }
}
