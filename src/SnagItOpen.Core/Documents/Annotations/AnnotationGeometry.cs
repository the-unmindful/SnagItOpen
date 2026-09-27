using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Documents.Annotations;

public enum HandleKind { Resize, Rotate, Start, End, Bend, Tail }

/// <summary>An interactive handle in document pixels. <see cref="Corner"/> is meaningful for Resize.</summary>
public readonly record struct AnnotationHandle(HandleKind Kind, PointD Position, ResizeHandle Corner = ResizeHandle.TopLeft);

public enum AlignMode { Left, CenterX, Right, Top, Middle, Bottom }

/// <summary>
/// Per-type editing geometry shared by the canvas and tests: handles, shape-accurate hit testing,
/// handle-drag results (Shift = 15° snapping / proportional) and align/distribute.
/// All inputs and outputs are document pixels.
/// </summary>
public static class AnnotationGeometry
{
    public const double SnapDegrees = 15;

    // ------------------------------------------------------------------ outlines

    /// <summary>Rotated corners (TL, TR, BR, BL) of box-shaped annotations; null for lines and freehand.</summary>
    public static PointD[]? Outline(Annotation a) => a switch
    {
        LineAnnotation => null,
        FreehandAnnotation f => Rotation2D.Corners(f.Extent(), 0),
        _ => Rotation2D.Corners(a.Bounds, a.CanRotate ? a.Rotation : 0),
    };

    // ------------------------------------------------------------------ handles

    /// <summary>Handles for one selected annotation. <paramref name="rotateDistance"/> is the rotate-handle offset in document pixels.</summary>
    public static IReadOnlyList<AnnotationHandle> Handles(Annotation a, double rotateDistance)
    {
        var list = new List<AnnotationHandle>();
        if (a.Locked) return list;
        if (a is LineAnnotation l)
        {
            list.Add(new(HandleKind.Start, l.Start));
            list.Add(new(HandleKind.End, l.End));
            list.Add(new(HandleKind.Bend, l.CurveMid));
            var mid = l.CurveMid;
            var dir = new PointD(l.End.X - l.Start.X, l.End.Y - l.Start.Y);
            double len = Math.Max(1e-6, dir.Length);
            var normal = new PointD(dir.Y / len, -dir.X / len);
            list.Add(new(HandleKind.Rotate, mid + normal * rotateDistance));
            return list;
        }
        var box = a is FreehandAnnotation { Points.Length: > 0 } fh ? FreehandAnnotation.BoundsOf(fh.Points) : a.Bounds;
        double rot = a.CanRotate ? a.Rotation : 0;
        var c = box.Center;
        foreach (var h in Enum.GetValues<ResizeHandle>())
            list.Add(new(HandleKind.Resize, Rotation2D.Rotate(LocalHandle(box, h), c, rot), h));
        if (a.CanRotate)
            list.Add(new(HandleKind.Rotate, Rotation2D.Rotate(new PointD(c.X, box.Y - rotateDistance), c, rot)));
        if (a is CalloutAnnotation co) list.Add(new(HandleKind.Tail, co.Tail));
        if (a is StepAnnotation { Tail: { } st }) list.Add(new(HandleKind.Tail, st));
        return list;
    }

    private static PointD LocalHandle(RectD r, ResizeHandle h) => h switch
    {
        ResizeHandle.TopLeft => new(r.X, r.Y),
        ResizeHandle.Top => new(r.X + r.Width / 2, r.Y),
        ResizeHandle.TopRight => new(r.Right, r.Y),
        ResizeHandle.Right => new(r.Right, r.Y + r.Height / 2),
        ResizeHandle.BottomRight => new(r.Right, r.Bottom),
        ResizeHandle.Bottom => new(r.X + r.Width / 2, r.Bottom),
        ResizeHandle.BottomLeft => new(r.X, r.Bottom),
        _ => new(r.X, r.Y + r.Height / 2),
    };

    /// <summary>Handle whose position is within <paramref name="tolerance"/> of <paramref name="p"/>; later handles win ties.</summary>
    public static AnnotationHandle? HitHandle(IReadOnlyList<AnnotationHandle> handles, PointD p, double tolerance)
    {
        AnnotationHandle? best = null;
        double bd = double.MaxValue;
        // Rotate/start/end/tail are more specific than resize; prefer them on overlap.
        foreach (var h in handles)
        {
            double d = PointD.Distance(h.Position, p);
            if (d > tolerance) continue;
            double score = d - (h.Kind == HandleKind.Resize ? 0 : 0.5);
            if (score < bd) { bd = score; best = h; }
        }
        return best;
    }

    // ------------------------------------------------------------------ hit testing

    /// <summary>True when <paramref name="p"/> is on the painted shape (with <paramref name="tolerance"/>).</summary>
    public static bool HitTest(Annotation a, PointD p, double tolerance)
    {
        switch (a)
        {
            case LineAnnotation l:
                return DistanceToPolyline(Sample(l), p) <= Math.Max(tolerance, l.StrokeWidth / 2 + tolerance)
                       || (l.EffectiveEndCap != ArrowCap.None && PointD.Distance(l.End, p) <= l.CapSize * 0.6 + tolerance)
                       || (l.EffectiveStartCap != ArrowCap.None && PointD.Distance(l.Start, p) <= l.CapSize * 0.6 + tolerance);
            case FreehandAnnotation f:
                return f.Points.Length > 0 && DistanceToPolyline(f.Points, p) <= f.StrokeWidth / 2 + tolerance;
            case CalloutAnnotation c:
                return InBox(c, p, tolerance) || DistanceToSegment(p, c.Bounds.Center, c.Tail) <= Math.Max(tolerance, c.TailWidth / 4);
            case StepAnnotation s:
                if (s.Tail is { } t && DistanceToSegment(p, s.Bounds.Center, t) <= Math.Max(tolerance, s.Bounds.Width / 6)) return true;
                return s.Shape == StepShape.Circle ? InEllipse(s, p, tolerance) : InBox(s, p, tolerance);
            case EllipseAnnotation { Fill: null } e when !e.Locked:
                return OnEllipseBorder(e, p, tolerance);
            case EllipseAnnotation e:
                return InEllipse(e, p, tolerance);
            case RectangleAnnotation { Fill: null } r when !r.Locked:
                return OnBoxBorder(r, p, tolerance);
            case MagnifierAnnotation { Circular: true } m:
                return InEllipse(m, p, tolerance);
            default:
                return InBox(a, p, tolerance);
        }
    }

    /// <summary>True when <paramref name="p"/> is anywhere inside the item's (rotated) box, ignoring see-through interiors.</summary>
    public static bool InsideBox(Annotation a, PointD p, double tolerance) => a switch
    {
        LineAnnotation or FreehandAnnotation => a.Extent().Inflate(tolerance).Contains(p),
        _ => InBox(a, p, tolerance),
    };

    /// <summary>Topmost-first list of every annotation under <paramref name="p"/>.</summary>
    public static IReadOnlyList<Annotation> HitStack(IReadOnlyList<Annotation> drawOrder, PointD p, double tolerance)
    {
        var list = new List<Annotation>();
        for (int i = drawOrder.Count - 1; i >= 0; i--)
            if (!drawOrder[i].Hidden && HitTest(drawOrder[i], p, tolerance)) list.Add(drawOrder[i]);
        return list;
    }

    private static bool OnBoxBorder(Annotation a, PointD p, double tol)
    {
        var q = Local(a, p);
        double band = tol + a.StrokeWidth / 2;
        var outer = a.Bounds.Inflate(band);
        if (!outer.Contains(q)) return false;
        var inner = a.Bounds.Inflate(-band);
        return inner.IsEmpty || !inner.Contains(q);
    }

    private static bool OnEllipseBorder(Annotation a, PointD p, double tol)
    {
        var q = Local(a, p);
        double band = tol + a.StrokeWidth / 2;
        var b = a.Bounds;
        double rx = b.Width / 2, ry = b.Height / 2;
        if (rx <= 0 || ry <= 0) return false;
        double dx = q.X - b.Center.X, dy = q.Y - b.Center.Y;
        static double N(double dx, double dy, double rx, double ry) => rx <= 0 || ry <= 0 ? double.MaxValue : (dx * dx) / (rx * rx) + (dy * dy) / (ry * ry);
        if (N(dx, dy, rx + band, ry + band) > 1) return false;
        return rx - band <= 0 || ry - band <= 0 || N(dx, dy, rx - band, ry - band) >= 1;
    }

    private static PointD Local(Annotation a, PointD p) =>
        a.CanRotate && a.Rotation != 0 ? Rotation2D.Rotate(p, a.Bounds.Center, -a.Rotation) : p;

    private static bool InBox(Annotation a, PointD p, double tol) => a.Bounds.Inflate(tol + a.StrokeWidth / 2).Contains(Local(a, p));

    private static bool InEllipse(Annotation a, PointD p, double tol)
    {
        var q = Local(a, p);
        var b = a.Bounds.Inflate(tol + a.StrokeWidth / 2);
        if (b.Width <= 0 || b.Height <= 0) return false;
        double dx = (q.X - b.Center.X) / (b.Width / 2), dy = (q.Y - b.Center.Y) / (b.Height / 2);
        return dx * dx + dy * dy <= 1;
    }

    /// <summary>Polyline approximation of a (curved) line.</summary>
    public static PointD[] Sample(LineAnnotation l, int segments = 24)
    {
        if (l.Control is null) return [l.Start, l.End];
        var pts = new PointD[segments + 1];
        for (int i = 0; i <= segments; i++) pts[i] = l.PointAt(i / (double)segments);
        return pts;
    }

    public static double DistanceToPolyline(IReadOnlyList<PointD> pts, PointD p)
    {
        if (pts.Count == 1) return PointD.Distance(pts[0], p);
        double best = double.MaxValue;
        for (int i = 1; i < pts.Count; i++) best = Math.Min(best, DistanceToSegment(p, pts[i - 1], pts[i]));
        return best;
    }

    public static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 < 1e-12) return PointD.Distance(p, a);
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return PointD.Distance(p, a + ab * t);
    }

    // ------------------------------------------------------------------ handle drags

    /// <summary>
    /// Result of dragging <paramref name="h"/> of the original annotation <paramref name="a"/> from
    /// <paramref name="from"/> to <paramref name="to"/>. <paramref name="shift"/> snaps angles to 15° and
    /// toggles proportional resizing (proportional by default for steps, stamps and magnifiers).
    /// </summary>
    public static Annotation Drag(Annotation a, AnnotationHandle h, PointD from, PointD to, bool shift)
    {
        if (a.Locked) return a;
        switch (h.Kind)
        {
            case HandleKind.Start when a is LineAnnotation l:
                return WithLine(l, SnapEnd(l.End, to, shift), l.End, l.Control);
            case HandleKind.End when a is LineAnnotation l:
                return WithLine(l, l.Start, SnapEnd(l.Start, to, shift), l.Control);
            case HandleKind.Bend when a is LineAnnotation l:
                {
                    var straightMid = new PointD((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);
                    // Dragging back onto the straight line removes the curve.
                    if (PointD.Distance(to, straightMid) < Math.Max(2, l.StrokeWidth)) return WithLine(l, l.Start, l.End, null);
                    // Curve passes through the dragged point at t = 0.5.
                    var c = new PointD(2 * to.X - straightMid.X, 2 * to.Y - straightMid.Y);
                    return WithLine(l, l.Start, l.End, c);
                }
            case HandleKind.Rotate when a is LineAnnotation l:
                {
                    var c = l.CurveMid;
                    double delta = Rotation2D.AngleOf(c, to) - Rotation2D.AngleOf(c, from);
                    if (shift)
                    {
                        double cur = Rotation2D.AngleOf(l.Start, l.End);
                        delta = Rotation2D.Snap(cur + delta, SnapDegrees) - cur;
                    }
                    var s = Rotation2D.Rotate(l.Start, c, delta);
                    var e = Rotation2D.Rotate(l.End, c, delta);
                    PointD? k = l.Control is { } cc ? Rotation2D.Rotate(cc, c, delta) : null;
                    return WithLine(l, s, e, k);
                }
            case HandleKind.Rotate when a.CanRotate:
                {
                    var c = a.Bounds.Center;
                    double r = a.Rotation + Rotation2D.AngleOf(c, to) - Rotation2D.AngleOf(c, from);
                    if (shift) r = Rotation2D.Snap(r, SnapDegrees);
                    return a with { Rotation = Rotation2D.Normalize(r) };
                }
            case HandleKind.Tail when a is CalloutAnnotation co:
                return co with { Tail = to };
            case HandleKind.Tail when a is StepAnnotation st:
                return st with { Tail = to };
            case HandleKind.Resize:
                return Resize(a, h.Corner, from, to, shift);
        }
        return a;
    }

    private static PointD SnapEnd(PointD anchor, PointD p, bool shift)
    {
        if (!shift) return p;
        double len = PointD.Distance(anchor, p);
        double ang = Rotation2D.Snap(Rotation2D.AngleOf(anchor, p), SnapDegrees) * Math.PI / 180;
        return new PointD(anchor.X + Math.Cos(ang) * len, anchor.Y + Math.Sin(ang) * len);
    }

    private static LineAnnotation WithLine(LineAnnotation l, PointD s, PointD e, PointD? c) =>
        l with { Start = s, End = e, Control = c, Bounds = RectD.FromPoints(s, e) };

    private static bool DefaultProportional(Annotation a) => a is StepAnnotation or StampAnnotation or MagnifierAnnotation;

    private static Annotation Resize(Annotation a, ResizeHandle corner, PointD from, PointD to, bool shift)
    {
        bool rotatable = a.CanRotate && a.Rotation != 0;
        var box = a is FreehandAnnotation ? FreehandAnnotation.BoundsOf(((FreehandAnnotation)a).Points) : a.Bounds;
        var c = box.Center;
        // Work in the annotation's unrotated frame.
        var lf = rotatable ? Rotation2D.Rotate(from, c, -a.Rotation) : from;
        var lt = rotatable ? Rotation2D.Rotate(to, c, -a.Rotation) : to;
        bool keep = DefaultProportional(a) ? !shift : shift;
        var nb = ResizeRect(box, corner, lt.X - lf.X, lt.Y - lf.Y, keep);
        if (rotatable)
        {
            // The opposite handle must stay fixed on screen: rotate the new centre back into document space.
            var newCenter = Rotation2D.Rotate(nb.Center, c, a.Rotation);
            nb = new RectD(newCenter.X - nb.Width / 2, newCenter.Y - nb.Height / 2, nb.Width, nb.Height);
        }
        return a switch
        {
            FreehandAnnotation f => ScaleInto(f, box, nb),
            _ => a with { Bounds = nb },
        };
    }

    private static Annotation ScaleInto(FreehandAnnotation f, RectD from, RectD to)
    {
        double sx = from.Width > 1e-6 ? to.Width / from.Width : 1, sy = from.Height > 1e-6 ? to.Height / from.Height : 1;
        return f.MapGeometry(p => new PointD(to.X + (p.X - from.X) * sx, to.Y + (p.Y - from.Y) * sy));
    }

    /// <summary>Floating-point resize of a rectangle by a handle; the opposite side stays fixed, minimum 1×1.</summary>
    public static RectD ResizeRect(RectD start, ResizeHandle h, double dx, double dy, bool keepAspect)
    {
        double l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        bool left = h is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        bool right = h is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight;
        bool top = h is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        bool bottom = h is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;
        if (left) l = Math.Min(l + dx, r - 1);
        if (right) r = Math.Max(r + dx, l + 1);
        if (top) t = Math.Min(t + dy, b - 1);
        if (bottom) b = Math.Max(b + dy, t + 1);
        if (keepAspect && start.Width > 0 && start.Height > 0)
        {
            double aspect = start.Width / start.Height, w = r - l, ht = b - t;
            bool corner = (left || right) && (top || bottom);
            if (corner) { if (w / start.Width >= ht / start.Height) ht = w / aspect; else w = ht * aspect; }
            else if (left || right) ht = w / aspect;
            else w = ht * aspect;
            if (corner || top || bottom) { if (left) l = r - w; else if (right) r = l + w; else { double cx = start.Center.X; l = cx - w / 2; r = cx + w / 2; } }
            if (corner || left || right) { if (top) t = b - ht; else if (bottom) b = t + ht; else { double cy = start.Center.Y; t = cy - ht / 2; b = cy + ht / 2; } }
        }
        return RectD.FromEdges(l, t, r, b);
    }

    // ------------------------------------------------------------------ align / distribute

    /// <summary>Offsets that align the given annotation extents to the union of the selection.</summary>
    public static IReadOnlyList<(Guid Id, double Dx, double Dy)> Align(IReadOnlyList<(Guid Id, RectD Box)> items, AlignMode mode)
    {
        if (items.Count < 2) return [];
        double l = items.Min(i => i.Box.X), t = items.Min(i => i.Box.Y), r = items.Max(i => i.Box.Right), b = items.Max(i => i.Box.Bottom);
        double cx = (l + r) / 2, cy = (t + b) / 2;
        return items.Select(i => mode switch
        {
            AlignMode.Left => (i.Id, l - i.Box.X, 0.0),
            AlignMode.CenterX => (i.Id, cx - i.Box.Center.X, 0.0),
            AlignMode.Right => (i.Id, r - i.Box.Right, 0.0),
            AlignMode.Top => (i.Id, 0.0, t - i.Box.Y),
            AlignMode.Middle => (i.Id, 0.0, cy - i.Box.Center.Y),
            _ => (i.Id, 0.0, b - i.Box.Bottom),
        }).ToList();
    }

    /// <summary>Offsets giving equal gaps between items (first and last stay put).</summary>
    public static IReadOnlyList<(Guid Id, double Dx, double Dy)> Distribute(IReadOnlyList<(Guid Id, RectD Box)> items, bool horizontal)
    {
        if (items.Count < 3) return [];
        var sorted = items.OrderBy(i => horizontal ? i.Box.X : i.Box.Y).ToList();
        double start = horizontal ? sorted[0].Box.X : sorted[0].Box.Y;
        double end = horizontal ? sorted[^1].Box.Right : sorted[^1].Box.Bottom;
        double total = sorted.Sum(i => horizontal ? i.Box.Width : i.Box.Height);
        double gap = (end - start - total) / (sorted.Count - 1);
        var result = new List<(Guid, double, double)>();
        double pos = start;
        foreach (var i in sorted)
        {
            double cur = horizontal ? i.Box.X : i.Box.Y;
            result.Add(horizontal ? (i.Id, pos - cur, 0) : (i.Id, 0, pos - cur));
            pos += (horizontal ? i.Box.Width : i.Box.Height) + gap;
        }
        return result;
    }
}
