using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.App.Capture;

/// <summary>
/// One borderless topmost overlay per monitor. It shows the frozen desktop snapshot for its monitor,
/// dims everything outside the selection and feeds physical cursor positions (GetCursorPos) into the
/// shared <see cref="RegionSelection"/>. Physical → local DIP conversion uses the monitor's scale, so
/// mixed-DPI setups never share one DPI.
/// </summary>
internal sealed class RegionOverlayWindow : Window
{
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)));
    private static readonly Pen SelectionPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0, 150, 255)), 1.5));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(255, 170, 0)), 2));
    private static readonly Pen CommittedPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(40, 200, 90)), 2) { DashStyle = DashStyles.Dash });
    private static readonly Pen CrossPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1));
    private static readonly Brush LabelBack = Freeze(new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)));

    private readonly MonitorInfo _monitor;
    private readonly RegionSelection _selection;
    private readonly BitmapSource _snapshot;
    private readonly string _hint;
    private readonly OverlaySurface _surface;

    /// <summary>Full-window element that paints the overlay via <see cref="RenderOverlay"/>.</summary>
    private sealed class OverlaySurface(RegionOverlayWindow owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => owner.RenderOverlay(dc);
    }

    public RegionOverlayWindow(MonitorInfo monitor, BitmapSource snapshot, RegionSelection selection, string hint)
    {
        _monitor = monitor;
        _snapshot = snapshot;
        _selection = selection;
        _hint = hint;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Focusable = true;
        Title = "SnagItOpen selection";
        AutomationProperties.SetName(this, "Capture selection. Drag to select, Enter to confirm, Escape to cancel.");
        // Draw in a child element: the Window template's background Border renders above Window.OnRender.
        _surface = new OverlaySurface(this);
        Content = _surface;
        _selection.Changed += _surface.InvalidateVisual;
        SourceInitialized += (_, _) =>
        {
            AppNative.MakeToolWindow(this);
            AppNative.PlaceTopmost(this, _monitor.Bounds);
        };
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Physical pixels per DIP on this window's monitor.</summary>
    private double Scale
    {
        get
        {
            var src = PresentationSource.FromVisual(this);
            return src?.CompositionTarget?.TransformToDevice.M11 ?? _monitor.Scale;
        }
    }

    private Rect ToLocal(PixelRect r)
    {
        double s = Scale;
        return new Rect((r.X - _monitor.Bounds.X) / s, (r.Y - _monitor.Bounds.Y) / s, r.Width / s, r.Height / s);
    }

    private Point ToLocal(PointD p)
    {
        double s = Scale;
        return new Point((p.X - _monitor.Bounds.X) / s, (p.Y - _monitor.Bounds.Y) / s);
    }

    private void RenderOverlay(DrawingContext dc)
    {
        var full = new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight);
        dc.DrawImage(_snapshot, full);

        // Dim everything except the current / committed selections.
        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(full));
        foreach (var c in _selection.Committed) dim.Children.Add(new RectangleGeometry(ToLocal(c)));
        if (_selection.Current is { } cur && _selection.Shape == CaptureShape.Rectangle) dim.Children.Add(new RectangleGeometry(ToLocal(cur)));
        dc.DrawGeometry(Dim, null, dim);

        foreach (var c in _selection.Committed) dc.DrawRectangle(null, CommittedPen, ToLocal(c));

        if (_selection.Phase == SelectionPhase.Dragging && _selection.Shape == CaptureShape.Freehand && _selection.Path.Count > 1)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToLocal(_selection.Path[0]), false, false);
                for (int i = 1; i < _selection.Path.Count; i++) ctx.LineTo(ToLocal(_selection.Path[i]), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, SelectionPen, g);
        }
        else if (_selection.Current is { } r)
        {
            var lr = ToLocal(r);
            if (_selection.Shape == CaptureShape.Ellipse)
                dc.DrawEllipse(null, SelectionPen, new Point(lr.X + lr.Width / 2, lr.Y + lr.Height / 2), lr.Width / 2, lr.Height / 2);
            dc.DrawRectangle(null, SelectionPen, lr);
            DrawLabel(dc, $"{r.Width} × {r.Height}", new Point(lr.X, lr.Y - 24));
        }
        else if (_selection.Hover is { } h)
        {
            var lh = ToLocal(h);
            dc.DrawRectangle(null, HoverPen, lh);
            DrawLabel(dc, $"{h.Width} × {h.Height}", new Point(lh.X + 4, lh.Y + 4));
        }

        // Crosshair and hint on the monitor that contains the cursor.
        var cursor = _selection.Cursor;
        if (_monitor.Bounds.Contains(cursor.X, cursor.Y))
        {
            var p = ToLocal(new PointD(cursor.X + 0.5, cursor.Y + 0.5));
            if (_selection.Phase != SelectionPhase.Dragging)
            {
                dc.DrawLine(CrossPen, new Point(0, p.Y), new Point(_surface.ActualWidth, p.Y));
                dc.DrawLine(CrossPen, new Point(p.X, 0), new Point(p.X, _surface.ActualHeight));
            }
            DrawLabel(dc, _hint, new Point(16, 16));
        }
    }

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = Math.Clamp(at.X, 2, Math.Max(2, _surface.ActualWidth - ft.Width - 12));
        var y = Math.Clamp(at.Y, 2, Math.Max(2, _surface.ActualHeight - ft.Height - 8));
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(x, y, ft.Width + 10, ft.Height + 4), 3, 3);
        dc.DrawText(ft, new Point(x + 5, y + 2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _selection.Move(AppNative.CursorPosition());
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        _selection.Down(AppNative.CursorPosition());
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _selection.Up(AppNative.CursorPosition());
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        // Right click cancels the drag first, then the whole session.
        _selection.Key(_selection.Phase == SelectionPhase.Dragging ? SelectionKey.Backspace : SelectionKey.Escape);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_selection.Phase == SelectionPhase.Dragging && Mouse.LeftButton != MouseButtonState.Pressed)
            _selection.Up(AppNative.CursorPosition());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var p = AppNative.CursorPosition();
        switch (e.Key)
        {
            case Key.Escape: _selection.Key(SelectionKey.Escape); break;
            case Key.Enter: _selection.Key(SelectionKey.Enter); break;
            case Key.Back: _selection.Key(SelectionKey.Backspace); break;
            // Arrow keys move the cursor one physical pixel (Shift: ten) for precise edges.
            case Key.Left or Key.Right or Key.Up or Key.Down:
                int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                var np = e.Key switch
                {
                    Key.Left => p with { X = p.X - step },
                    Key.Right => p with { X = p.X + step },
                    Key.Up => p with { Y = p.Y - step },
                    _ => p with { Y = p.Y + step },
                };
                AppNative.MoveCursor(np);
                _selection.Move(np);
                break;
            case Key.Space:
                // Space toggles press/release for keyboard-only selection.
                if (_selection.Phase == SelectionPhase.Dragging) _selection.Up(p); else _selection.Down(p);
                break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _selection.Changed -= _surface.InvalidateVisual;
        base.OnClosed(e);
    }
}
