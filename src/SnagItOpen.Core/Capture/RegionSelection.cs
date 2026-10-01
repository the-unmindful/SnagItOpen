using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Capture;

public enum SelectionPhase { Idle, Dragging, Adjusting, Done, Canceled }

public enum SelectionKey { Escape, Enter, Backspace, Left, Right, Up, Down, Tab }

public enum SelectionHandle { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>
/// Pointer/keyboard state machine for interactive capture selection, entirely in physical desktop
/// pixels. Overlays feed it cursor positions and render its state; it never touches windows.
/// Drag rectangles include both the anchor and the current pixel, so the smallest drag is 1×1.
/// A click without movement over a candidate window selects that window's bounds.
/// </summary>
public sealed class RegionSelection
{
    public const int MaxFreehandPoints = 4000;
    private readonly List<PixelRect> _committed = [];
    private readonly List<PointD> _path = [];
    private readonly IReadOnlyList<PixelRect> _windows;
    private PixelPoint _anchor;
    private bool _moved;
    private PixelRect _adjustOriginal;
    private SelectionHandle _adjustHandle;

    public RegionSelection(PixelRect desktop, CaptureShape shape = CaptureShape.Rectangle,
        SelectionConstraint? constraint = null, bool multiRegion = false,
        IReadOnlyList<PixelRect>? windows = null, bool windowsOnly = false, bool captureOnRelease = true)
    {
        if (desktop.IsEmpty) throw new ArgumentOutOfRangeException(nameof(desktop));
        Desktop = desktop;
        Shape = shape;
        Constraint = constraint ?? SelectionConstraint.None;
        MultiRegion = multiRegion;
        _windows = windows ?? [];
        WindowsOnly = windowsOnly;
        CaptureOnRelease = captureOnRelease;
    }

    public PixelRect Desktop { get; }
    public CaptureShape Shape { get; private set; }
    public SelectionConstraint Constraint { get; private set; }
    public bool MultiRegion { get; private set; }
    public bool WindowsOnly { get; private set; }
    public bool CaptureOnRelease { get; }
    public SelectionHandle ActiveEdge { get; private set; } = SelectionHandle.Right;
    public bool IsMovingBody => Phase == SelectionPhase.Adjusting && _adjustHandle == SelectionHandle.Move;
    public SelectionPhase Phase { get; private set; }
    public PixelPoint Cursor { get; private set; }

    /// <summary>Window rectangle under the cursor while idle (click selects it).</summary>
    public PixelRect? Hover { get; private set; }

    /// <summary>Rectangle being dragged (or the fixed-size rectangle following the cursor).</summary>
    public PixelRect? Current { get; private set; }

    public IReadOnlyList<PixelRect> Committed => _committed;

    /// <summary>Freehand outline in physical pixels (only for <see cref="CaptureShape.Freehand"/>).</summary>
    public IReadOnlyList<PointD> Path => _path;

    /// <summary>Final rectangles in selection order (empty unless Done).</summary>
    public IReadOnlyList<PixelRect> Result => Phase == SelectionPhase.Done ? _committed : [];

    /// <summary>Freehand polygon of the final selection, relative to its rectangle's top-left.</summary>
    public IReadOnlyList<PointD> ResultPolygon { get; private set; } = [];

    public event Action? Changed;

    public void Move(PixelPoint p)
    {
        if (IsFinished) return;
        Cursor = Clamp(p);
        if (Phase == SelectionPhase.Adjusting)
        {
            if (_adjustHandle != SelectionHandle.None)
                Current = Resize(_adjustOriginal, _adjustHandle, Cursor.X - _anchor.X, Cursor.Y - _anchor.Y);
        }
        else if (Phase == SelectionPhase.Dragging)
        {
            if (Cursor != _anchor) _moved = true;
            if (Shape == CaptureShape.Freehand) AddPathPoint(Cursor);
            Current = DragRect(_anchor, Cursor);
        }
        else
        {
            Hover = WindowAt(Cursor);
            Current = FixedRect(Cursor);
        }
        Changed?.Invoke();
    }

    public void Down(PixelPoint p)
    {
        if (IsFinished) return;
        Cursor = Clamp(p);
        if (Phase == SelectionPhase.Adjusting)
        {
            var handle = HitTestHandle(Cursor, 6);
            if (handle != SelectionHandle.None) { BeginAdjust(handle, Cursor); return; }
            ResetIdle();
        }
        if (Constraint.FixedSize is { IsEmpty: false })
        {
            // Fixed size commits on press; no drag needed.
            Commit(FixedRect(Cursor)!.Value);
            return;
        }
        _anchor = Cursor;
        _moved = false;
        _path.Clear();
        if (Shape == CaptureShape.Freehand) AddPathPoint(Cursor);
        Phase = SelectionPhase.Dragging;
        Current = WindowsOnly ? null : DragRect(_anchor, Cursor);
        Changed?.Invoke();
    }

    public void Up(PixelPoint p)
    {
        if (Phase == SelectionPhase.Adjusting)
        {
            Move(p);
            _adjustHandle = SelectionHandle.None;
            Changed?.Invoke();
            return;
        }
        if (Phase != SelectionPhase.Dragging) return;
        Move(p);
        Phase = SelectionPhase.Idle;
        PixelRect? pick;
        if (!_moved || WindowsOnly)
        {
            // Click: pick the hovered window when available, otherwise a single pixel.
            var w = WindowAt(Cursor);
            pick = w ?? (WindowsOnly ? null : DragRect(_anchor, _anchor));
        }
        else if (Shape == CaptureShape.Freehand)
        {
            pick = FinishFreehand();
        }
        else pick = Current;

        Current = null;
        if (pick is { IsEmpty: false } r)
        {
            if (!CaptureOnRelease && !MultiRegion && !WindowsOnly && _moved && Shape != CaptureShape.Freehand)
            {
                Current = r;
                Hover = null;
                Phase = SelectionPhase.Adjusting;
                Changed?.Invoke();
            }
            else Commit(r);
        }
        else Changed?.Invoke();
    }

    public void Key(SelectionKey key, bool shift = false, bool control = false)
    {
        if (IsFinished) return;
        switch (key)
        {
            case SelectionKey.Escape:
                if (Phase == SelectionPhase.Adjusting) { ResetIdle(); break; }
                Phase = SelectionPhase.Canceled;
                _committed.Clear();
                Current = null;
                break;
            case SelectionKey.Backspace:
                if (Phase == SelectionPhase.Dragging) { Phase = SelectionPhase.Idle; Current = null; _path.Clear(); }
                else if (_committed.Count > 0) _committed.RemoveAt(_committed.Count - 1);
                break;
            case SelectionKey.Enter:
                if (Phase == SelectionPhase.Adjusting && Current is { } adjusted) { Commit(adjusted); return; }
                if (Phase == SelectionPhase.Dragging)
                {
                    Up(Cursor);
                    if (IsFinished) return;
                    if (Phase == SelectionPhase.Adjusting && Current is { } entered) { Commit(entered); return; }
                }
                if (MultiRegion)
                {
                    if (_committed.Count > 0) Phase = SelectionPhase.Done;
                }
                else if (Hover is { } h) { Commit(h); return; }
                else if (FixedRect(Cursor) is { } f) { Commit(f); return; }
                break;
            case SelectionKey.Tab:
                if (Phase == SelectionPhase.Adjusting)
                    ActiveEdge = ActiveEdge switch { SelectionHandle.Right => SelectionHandle.Bottom,
                        SelectionHandle.Bottom => SelectionHandle.Left, SelectionHandle.Left => SelectionHandle.Top, _ => SelectionHandle.Right };
                break;
            case SelectionKey.Left or SelectionKey.Right or SelectionKey.Up or SelectionKey.Down:
                if (Phase != SelectionPhase.Adjusting || Current is not { } rect) break;
                int step = shift && !control ? 10 : 1;
                int dx = key == SelectionKey.Left ? -step : key == SelectionKey.Right ? step : 0;
                int dy = key == SelectionKey.Up ? -step : key == SelectionKey.Down ? step : 0;
                var handle = control ? dx != 0 ? shift ? SelectionHandle.Left : SelectionHandle.Right
                    : shift ? SelectionHandle.Top : SelectionHandle.Bottom : SelectionHandle.Move;
                if (control) ActiveEdge = handle;
                Current = Resize(rect, handle, dx, dy);
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>Cancels because displays changed or the session was aborted externally.</summary>
    public void Cancel()
    {
        if (IsFinished) return;
        Phase = SelectionPhase.Canceled;
        _committed.Clear();
        Current = null;
        Changed?.Invoke();
    }

    public bool IsFinished => Phase is SelectionPhase.Done or SelectionPhase.Canceled;

    public void Configure(CaptureShape shape, SelectionConstraint? constraint = null, bool multiRegion = false, bool windowsOnly = false)
    {
        if (IsFinished) return;
        Shape = shape;
        Constraint = constraint ?? SelectionConstraint.None;
        MultiRegion = multiRegion;
        WindowsOnly = windowsOnly;
        _committed.Clear();
        ResetIdle();
        Move(Cursor);
    }

    public void Confirm(PixelRect rectangle)
    {
        if (!IsFinished) Commit(rectangle);
    }

    public void BeginAdjust(SelectionHandle handle, PixelPoint point)
    {
        if (Phase != SelectionPhase.Adjusting || Current is not { } r || handle == SelectionHandle.None) return;
        _adjustOriginal = r;
        _adjustHandle = handle;
        _anchor = Clamp(point);
        Changed?.Invoke();
    }

    public SelectionHandle HitTestHandle(PixelPoint point, int radius)
    {
        if (Phase != SelectionPhase.Adjusting || Current is not { } r) return SelectionHandle.None;
        foreach (var (handle, p) in Handles(r))
            if (Math.Abs(point.X - p.X) <= radius && Math.Abs(point.Y - p.Y) <= radius) return handle;
        return r.Contains(point.X, point.Y) ? SelectionHandle.Move : SelectionHandle.None;
    }

    public static IEnumerable<(SelectionHandle Handle, PixelPoint Point)> Handles(PixelRect r)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return (SelectionHandle.TopLeft, new(r.X, r.Y));
        yield return (SelectionHandle.TopRight, new(r.Right, r.Y));
        yield return (SelectionHandle.BottomRight, new(r.Right, r.Bottom));
        yield return (SelectionHandle.BottomLeft, new(r.X, r.Bottom));
        yield return (SelectionHandle.Top, new(cx, r.Y));
        yield return (SelectionHandle.Right, new(r.Right, cy));
        yield return (SelectionHandle.Bottom, new(cx, r.Bottom));
        yield return (SelectionHandle.Left, new(r.X, cy));
    }

    private void ResetIdle()
    {
        Phase = SelectionPhase.Idle;
        Current = null;
        Hover = null;
        _adjustHandle = SelectionHandle.None;
        _path.Clear();
        ResultPolygon = [];
    }

    private PixelRect Resize(PixelRect r, SelectionHandle handle, int dx, int dy)
    {
        if (handle == SelectionHandle.Move)
            return new PixelRect(Math.Clamp(r.X + dx, Desktop.X, Desktop.Right - r.Width),
                Math.Clamp(r.Y + dy, Desktop.Y, Desktop.Bottom - r.Height), r.Width, r.Height);
        bool left = handle is SelectionHandle.Left or SelectionHandle.TopLeft or SelectionHandle.BottomLeft;
        bool right = handle is SelectionHandle.Right or SelectionHandle.TopRight or SelectionHandle.BottomRight;
        bool top = handle is SelectionHandle.Top or SelectionHandle.TopLeft or SelectionHandle.TopRight;
        bool bottom = handle is SelectionHandle.Bottom or SelectionHandle.BottomLeft or SelectionHandle.BottomRight;
        int l = left ? Math.Clamp(r.X + dx, Desktop.X, r.Right - 1) : r.X;
        int rr = right ? Math.Clamp(r.Right + dx, r.X + 1, Desktop.Right) : r.Right;
        int t = top ? Math.Clamp(r.Y + dy, Desktop.Y, r.Bottom - 1) : r.Y;
        int b = bottom ? Math.Clamp(r.Bottom + dy, r.Y + 1, Desktop.Bottom) : r.Bottom;
        if (Constraint.AspectRatio is not { } ar || ar <= 0 || !double.IsFinite(ar))
            return PixelRect.FromEdges(l, t, rr, b);
        double w = rr - l, h = b - t;
        bool widthDrives = (left || right) && (!(top || bottom) || Math.Abs(dx) >= Math.Abs(dy) * ar);
        if (widthDrives) h = w / ar; else w = h * ar;
        double ax = left ? r.Right : right ? r.X : r.X + r.Width / 2.0;
        double ay = top ? r.Bottom : bottom ? r.Y : r.Y + r.Height / 2.0;
        double maxW = left ? ax - Desktop.X : right ? Desktop.Right - ax : 2 * Math.Min(ax - Desktop.X, Desktop.Right - ax);
        double maxH = top ? ay - Desktop.Y : bottom ? Desktop.Bottom - ay : 2 * Math.Min(ay - Desktop.Y, Desktop.Bottom - ay);
        double fit = Math.Min(1, Math.Min(maxW / w, maxH / h));
        int iw = Math.Max(1, (int)Math.Round(w * fit)), ih = Math.Max(1, (int)Math.Round(h * fit));
        int ix = (int)Math.Round(left ? ax - iw : right ? ax : ax - iw / 2.0);
        int iy = (int)Math.Round(top ? ay - ih : bottom ? ay : ay - ih / 2.0);
        return new PixelRect(Math.Clamp(ix, Desktop.X, Desktop.Right - iw), Math.Clamp(iy, Desktop.Y, Desktop.Bottom - ih), iw, ih);
    }

    private void Commit(PixelRect r)
    {
        r = r.Intersect(Desktop);
        if (r.IsEmpty) { Changed?.Invoke(); return; }
        _committed.Add(r);
        if (!MultiRegion) Phase = SelectionPhase.Done;
        Changed?.Invoke();
    }

    private PixelRect? FinishFreehand()
    {
        if (_path.Count < 3) return null;
        var bounds = FreehandBounds(_path).Intersect(Desktop);
        if (bounds.Width < 2 || bounds.Height < 2) return null;
        ResultPolygon = _path.Select(q => new PointD(q.X - bounds.X, q.Y - bounds.Y)).ToArray();
        return bounds;
    }

    private static PixelRect FreehandBounds(IReadOnlyList<PointD> pts)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var p in pts) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
        // Points are pixel centers (x + 0.5); each covered pixel spans center ± 0.5.
        return RectD.FromEdges(l - 0.5, t - 0.5, r + 0.5, b + 0.5).ToPixelRectOutward();
    }

    private void AddPathPoint(PixelPoint p)
    {
        var q = new PointD(p.X + 0.5, p.Y + 0.5);
        if (_path.Count > 0 && PointD.Distance(_path[^1], q) < 1.5) return;
        if (_path.Count >= MaxFreehandPoints) return;
        _path.Add(q);
    }

    private PixelRect DragRect(PixelPoint a, PixelPoint b)
    {
        if (Constraint.AspectRatio is { } ar && ar > 0)
        {
            var c = Constraint.Apply(a, b);
            return c.IsEmpty ? new PixelRect(a.X, a.Y, 1, 1) : c.Intersect(Desktop);
        }
        // Inclusive of both endpoints.
        return PixelRect.FromEdges(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
    }

    private PixelRect? FixedRect(PixelPoint p)
    {
        if (Constraint.FixedSize is not { IsEmpty: false } fs) return null;
        int x = Math.Clamp(p.X - fs.Width / 2, Desktop.X, Math.Max(Desktop.X, Desktop.Right - fs.Width));
        int y = Math.Clamp(p.Y - fs.Height / 2, Desktop.Y, Math.Max(Desktop.Y, Desktop.Bottom - fs.Height));
        return new PixelRect(x, y, fs.Width, fs.Height);
    }

    private PixelRect? WindowAt(PixelPoint p)
    {
        if (Constraint.FixedSize is { IsEmpty: false }) return null;
        foreach (var w in _windows) if (w.Contains(p.X, p.Y)) return w;
        return null;
    }

    private PixelPoint Clamp(PixelPoint p) =>
        new(Math.Clamp(p.X, Desktop.X, Desktop.Right - 1), Math.Clamp(p.Y, Desktop.Y, Desktop.Bottom - 1));
}
