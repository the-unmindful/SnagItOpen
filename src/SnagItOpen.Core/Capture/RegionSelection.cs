using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Capture;

public enum SelectionPhase { Idle, Dragging, Done, Canceled }

public enum SelectionKey { Escape, Enter, Backspace }

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

    public RegionSelection(PixelRect desktop, CaptureShape shape = CaptureShape.Rectangle,
        SelectionConstraint? constraint = null, bool multiRegion = false,
        IReadOnlyList<PixelRect>? windows = null, bool windowsOnly = false)
    {
        if (desktop.IsEmpty) throw new ArgumentOutOfRangeException(nameof(desktop));
        Desktop = desktop;
        Shape = shape;
        Constraint = constraint ?? SelectionConstraint.None;
        MultiRegion = multiRegion;
        _windows = windows ?? [];
        WindowsOnly = windowsOnly;
    }

    public PixelRect Desktop { get; }
    public CaptureShape Shape { get; }
    public SelectionConstraint Constraint { get; }
    public bool MultiRegion { get; }
    public bool WindowsOnly { get; }
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
        if (Phase == SelectionPhase.Dragging)
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
        if (pick is { IsEmpty: false } r) Commit(r);
        else Changed?.Invoke();
    }

    public void Key(SelectionKey key)
    {
        if (IsFinished) return;
        switch (key)
        {
            case SelectionKey.Escape:
                Phase = SelectionPhase.Canceled;
                _committed.Clear();
                Current = null;
                break;
            case SelectionKey.Backspace:
                if (Phase == SelectionPhase.Dragging) { Phase = SelectionPhase.Idle; Current = null; _path.Clear(); }
                else if (_committed.Count > 0) _committed.RemoveAt(_committed.Count - 1);
                break;
            case SelectionKey.Enter:
                if (Phase == SelectionPhase.Dragging) { Up(Cursor); if (IsFinished) return; }
                if (MultiRegion)
                {
                    if (_committed.Count > 0) Phase = SelectionPhase.Done;
                }
                else if (Hover is { } h) { Commit(h); return; }
                else if (FixedRect(Cursor) is { } f) { Commit(f); return; }
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
