using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using static SnagItOpen.Imaging.Rendering.WpfConvert;

namespace SnagItOpen.Imaging.Rendering;

/// <summary>
/// Draws vector annotations in document pixels. Handles rotation (about the bounds centre), overall
/// opacity and drop shadow for every kind except redaction, which is always an axis-aligned opaque fill.
/// Magnifiers are drawn by <see cref="DocumentRenderer"/>.
/// </summary>
public static class AnnotationRenderer
{
    /// <summary>Default text padding (older documents).</summary>
    public const double TextPadding = 6;
    private static readonly Rgba32 ShadowColor = new(0, 0, 0, 90);

    public static void Draw(DrawingContext dc, Annotation a, Func<string, BitmapSource?>? resolveAsset = null)
    {
        if (a is RedactionAnnotation rd)
        {
            dc.DrawRectangle(Brush(rd.Color with { A = 255 }), null, rd.Bounds.ToRect());
            return;
        }
        var group = new DrawingGroup();
        using (var gc = group.Open()) DrawCore(gc, a, resolveAsset);
        if (group.Bounds.IsEmpty) return;
        group.Freeze();
        bool fade = a.Alpha < 0.999;
        if (fade) dc.PushOpacity(Math.Clamp(a.Alpha, 0, 1));
        if (a.Shadow) DrawShadow(dc, group);
        dc.DrawDrawing(group);
        if (fade) dc.Pop();
    }

    /// <summary>Offset silhouette of the drawing, masked by its own alpha.</summary>
    private static void DrawShadow(DrawingContext dc, Drawing d)
    {
        var b = d.Bounds;
        double o = Annotation.ShadowOffset;
        var target = new Rect(b.X + o, b.Y + o, b.Width, b.Height);
        var mask = new DrawingBrush(d)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = b,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = target,
        };
        mask.Freeze();
        dc.PushOpacityMask(mask);
        dc.DrawRectangle(Brush(ShadowColor), null, target);
        dc.Pop();
    }

    private static bool PushRotation(DrawingContext dc, Annotation a)
    {
        if (!a.CanRotate || a.Rotation == 0) return false;
        var c = a.Bounds.Center;
        dc.PushTransform(new RotateTransform(a.Rotation, c.X, c.Y));
        return true;
    }

    private static void DrawCore(DrawingContext dc, Annotation a, Func<string, BitmapSource?>? resolve)
    {
        switch (a)
        {
            case LineAnnotation l: DrawLine(dc, l); return;
            case CalloutAnnotation c: DrawCallout(dc, c); return;
            case StepAnnotation s: DrawStep(dc, s); return;
            case FreehandAnnotation f: DrawFreehand(dc, f); return;
        }
        bool rotated = PushRotation(dc, a);
        switch (a)
        {
            case RectangleAnnotation r:
                dc.DrawRoundedRectangle(r.Fill is { } rf ? Brush(rf) : null, r.StrokeWidth > 0 ? LinePen(r.Color, r.StrokeWidth, r.Dash) : null,
                    r.Bounds.ToRect(), r.CornerRadius, r.CornerRadius);
                break;
            case EllipseAnnotation e:
                {
                    var rect = e.Bounds.ToRect();
                    dc.DrawEllipse(e.Fill is { } ef ? Brush(ef) : null, e.StrokeWidth > 0 ? Pen(e.Color, e.StrokeWidth) : null,
                        new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                    break;
                }
            case TextAnnotation t:
                DrawText(dc, t);
                break;
            case HighlightAnnotation h:
                dc.DrawRectangle(Brush(h.Color with { A = h.Opacity }), null, h.Bounds.ToRect());
                break;
            case StampAnnotation st:
                DrawStamp(dc, st, resolve);
                break;
        }
        if (rotated) dc.Pop();
    }

    // ------------------------------------------------------------------ lines and arrows

    private static Pen LinePen(Rgba32 color, double width, LineDash dash)
    {
        var p = new Pen(Brush(color), Math.Max(0.01, width))
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, DashCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        if (dash == LineDash.Dashed) p.DashStyle = new DashStyle([3, 2], 0);
        else if (dash == LineDash.Dotted) p.DashStyle = new DashStyle([0, 2], 0);
        p.Freeze();
        return p;
    }

    private static void DrawLine(DrawingContext dc, LineAnnotation l)
    {
        var pts = AnnotationGeometry.Sample(l, l.Control is null ? 1 : 32).Select(p => p.ToPoint()).ToList();
        double len = 0;
        for (int i = 1; i < pts.Count; i++) len += (pts[i] - pts[i - 1]).Length;
        if (len < 0.5)
        {
            dc.DrawEllipse(Brush(l.Color), null, pts[0], l.StrokeWidth / 2, l.StrokeWidth / 2);
            return;
        }
        var startCap = l.EffectiveStartCap;
        var endCap = l.EffectiveEndCap;
        bool both = startCap != ArrowCap.None && endCap != ArrowCap.None;
        double size = Math.Min(l.CapSize, len * (both ? 0.45 : 0.7));

        // Outward directions at each end (tangents).
        var endDir = pts[^1] - pts[^2]; endDir.Normalize();
        var startDir = pts[0] - pts[1]; startDir.Normalize();

        var trimmed = Trim(pts, startCap == ArrowCap.Filled ? size * 0.8 : 0, endCap == ArrowCap.Filled ? size * 0.8 : 0);
        var path = new StreamGeometry();
        using (var c = path.Open())
        {
            c.BeginFigure(trimmed[0], false, false);
            c.PolyLineTo(trimmed.Skip(1).ToList(), true, true);
        }
        path.Freeze();

        var caps = new List<(Geometry G, bool Filled)>();
        if (CapGeometry(startCap, pts[0], startDir, size, l.StrokeWidth) is { } sg) caps.Add(sg);
        if (CapGeometry(endCap, pts[^1], endDir, size, l.StrokeWidth) is { } eg) caps.Add(eg);

        if (l.Outline is { } outline)
        {
            double ow = Math.Max(2, l.StrokeWidth * 0.6);
            var opLine = LinePen(outline, l.StrokeWidth + 2 * ow, l.EffectiveDash);
            dc.DrawGeometry(null, opLine, path);
            var opCap = LinePen(outline, 2 * ow, LineDash.Solid);
            var opStroke = LinePen(outline, l.StrokeWidth + 2 * ow, LineDash.Solid);
            foreach (var (g, filled) in caps) dc.DrawGeometry(null, filled ? opCap : opStroke, g);
        }
        dc.DrawGeometry(null, LinePen(l.Color, l.StrokeWidth, l.EffectiveDash), path);
        var solid = LinePen(l.Color, l.StrokeWidth, LineDash.Solid);
        foreach (var (g, filled) in caps)
            if (filled) dc.DrawGeometry(Brush(l.Color), null, g); else dc.DrawGeometry(null, solid, g);
    }

    /// <summary>Removes <paramref name="fromStart"/>/<paramref name="fromEnd"/> of arc length from a polyline.</summary>
    private static List<Point> Trim(List<Point> pts, double fromStart, double fromEnd)
    {
        var list = new List<Point>(pts);
        if (fromEnd > 0) list = TrimEnd(list, fromEnd);
        if (fromStart > 0) { list.Reverse(); list = TrimEnd(list, fromStart); list.Reverse(); }
        return list.Count >= 2 ? list : pts;
    }

    private static List<Point> TrimEnd(List<Point> pts, double amount)
    {
        var list = new List<Point>(pts);
        while (list.Count >= 2 && amount > 0)
        {
            var a = list[^2]; var b = list[^1];
            double seg = (b - a).Length;
            if (seg > amount)
            {
                var d = a - b; d.Normalize();
                list[^1] = b + d * amount;
                return list;
            }
            amount -= seg;
            list.RemoveAt(list.Count - 1);
        }
        return list;
    }

    private static (Geometry, bool)? CapGeometry(ArrowCap cap, Point tip, Vector dir, double size, double stroke)
    {
        if (cap == ArrowCap.None) return null;
        var n = new Vector(-dir.Y, dir.X);
        Geometry g;
        bool filled = true;
        switch (cap)
        {
            case ArrowCap.Filled:
                {
                    var b = tip - dir * size;
                    g = Polygon(true, tip, b + n * size * 0.5, b - n * size * 0.5);
                    break;
                }
            case ArrowCap.Open:
                {
                    var b = tip - dir * size;
                    g = Polygon(false, b + n * size * 0.5, tip, b - n * size * 0.5);
                    filled = false;
                    break;
                }
            case ArrowCap.Circle:
                {
                    double r = Math.Max(stroke * 1.3, size * 0.28);
                    g = new EllipseGeometry(tip, r, r);
                    break;
                }
            case ArrowCap.Square:
                {
                    double h = Math.Max(stroke * 1.2, size * 0.25);
                    g = Polygon(true, tip + dir * h + n * h, tip + dir * h - n * h, tip - dir * h - n * h, tip - dir * h + n * h);
                    break;
                }
            default: // Bar
                g = Polygon(false, tip + n * size * 0.5, tip - n * size * 0.5);
                filled = false;
                break;
        }
        g.Freeze();
        return (g, filled);
    }

    private static StreamGeometry Polygon(bool closed, params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], closed, closed);
            c.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    // ------------------------------------------------------------------ text

    public static FormattedText Format(TextAnnotation t, Rgba32 color, double maxWidth)
    {
        var tf = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(t.FontFamily) ? "Segoe UI" : t.FontFamily),
            t.Italic ? FontStyles.Italic : FontStyles.Normal,
            t.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        var ft = new FormattedText(string.IsNullOrEmpty(t.Text) ? " " : t.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            tf, Math.Clamp(t.FontSize, 1, 1000), Brush(color), 1.0)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            TextAlignment = t.Alignment switch
            {
                TextAlign.Center => TextAlignment.Center,
                TextAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
            Trimming = TextTrimming.None,
        };
        if (t.Underline) ft.SetTextDecorations(TextDecorations.Underline);
        return ft;
    }

    private static bool IsEllipse(TextAnnotation t) => t is CalloutAnnotation { Shape: CalloutShape.Ellipse };

    /// <summary>Text area inside the box (ellipse callouts use the inscribed rectangle).</summary>
    public static Rect TextArea(TextAnnotation t)
    {
        var r = t.Bounds.ToRect();
        double ix = t.PadX, iy = t.PadY;
        if (IsEllipse(t))
        {
            ix += r.Width * (1 - Math.Sqrt(0.5)) / 2;
            iy += r.Height * (1 - Math.Sqrt(0.5)) / 2;
        }
        return new Rect(r.X + ix, r.Y + iy, Math.Max(1, r.Width - 2 * ix), Math.Max(1, r.Height - 2 * iy));
    }

    /// <summary>Box height needed to show all text at the annotation's width.</summary>
    public static double MeasureTextHeight(TextAnnotation t)
    {
        var area = TextArea(t);
        var ft = Format(t, t.Color, area.Width);
        double extra = t.Bounds.Height - area.Height;
        return BlockHeight(t, ft) + Math.Max(0, extra);
    }

    private static (double LineHeight, double Cap, double Descent) Metrics(TextAnnotation t)
    {
        var family = new FontFamily(string.IsNullOrWhiteSpace(t.FontFamily) ? "Segoe UI" : t.FontFamily);
        var tf = new Typeface(family, t.Italic ? FontStyles.Italic : FontStyles.Normal, t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        double size = Math.Clamp(t.FontSize, 1, 1000), line = family.LineSpacing * size, baseline = family.Baseline * size;
        return (line, tf.CapsHeight * size, Math.Max(0, line - baseline));
    }

    /// <summary>
    /// Height the text occupies inside the box. Top-aligned (legacy) text uses the full line boxes. Middle/Bottom use the
    /// optical block from the first cap height to the last baseline, padded by the descent on both sides so descenders
    /// stay inside and the block stays centred.
    /// </summary>
    private static double BlockHeight(TextAnnotation t, FormattedText ft)
    {
        if (t.VerticalAlign == TextVAlign.Top) return ft.Height;
        var (line, cap, descent) = Metrics(t);
        return Math.Max(0, ft.Height - line + cap) + 2 * descent;
    }

    /// <summary>Top-left origin for <see cref="FormattedText"/> so the text block sits per <see cref="TextAnnotation.VerticalAlign"/>.</summary>
    public static Point GlyphOrigin(TextAnnotation t, FormattedText ft, Rect area)
    {
        if (t.VerticalAlign == TextVAlign.Top) return area.TopLeft;
        var (line, cap, descent) = Metrics(t);
        double block = BlockHeight(t, ft);
        double blockTop = t.VerticalAlign == TextVAlign.Middle ? area.Y + (area.Height - block) / 2 : area.Bottom - block;
        // The block starts one descent above the cap height of the first line.
        double capTop = blockTop + descent, baselineOffset = ft.Baseline;
        return new Point(area.X, capTop - (baselineOffset - cap));
    }

    /// <summary>
    /// Resizes the box to its text per <see cref="TextAnnotation.Sizing"/> (anchored at the top-left).
    /// Fixed boxes only grow in height so no text is ever hidden.
    /// </summary>
    public static TextAnnotation Fit(TextAnnotation t)
    {
        var fit = FitAnchoredTopLeft(t);
        // Rotation is about the box centre: keep that centre so a rotated box grows in place instead of drifting.
        if (t.Rotation == 0 || fit.Bounds == t.Bounds) return fit;
        var c = t.Bounds.Center;
        return fit with { Bounds = fit.Bounds with { X = c.X - fit.Bounds.Width / 2, Y = c.Y - fit.Bounds.Height / 2 } };
    }

    private static TextAnnotation FitAnchoredTopLeft(TextAnnotation t)
    {
        if (t.Bounds.IsEmpty) return t;
        double k = IsEllipse(t) ? Math.Sqrt(0.5) : 1;
        if (t.Sizing == TextSizing.AutoWidth)
        {
            var free = Format(t, t.Color, 100000);
            double contentW = Math.Ceiling(free.WidthIncludingTrailingWhitespace) + 1;
            double width = (contentW + 2 * t.PadX) / k;
            var sized = t with { Bounds = t.Bounds with { Width = width } };
            double height = (BlockHeight(t, Format(sized, t.Color, TextArea(sized).Width)) + 2 * t.PadY) / k;
            return sized with { Bounds = sized.Bounds with { Height = Math.Ceiling(height) } };
        }
        var area = TextArea(t);
        double need = (BlockHeight(t, Format(t, t.Color, area.Width)) + 2 * t.PadY) / k;
        need = Math.Ceiling(need);
        if (t.Sizing == TextSizing.AutoHeight) return t with { Bounds = t.Bounds with { Height = need } };
        double legacy = MeasureTextHeight(t);
        return legacy > t.Bounds.Height ? t with { Bounds = t.Bounds with { Height = legacy } } : t;
    }

    private static void DrawText(DrawingContext dc, TextAnnotation t)
    {
        var rect = t.Bounds.ToRect();
        double rad = Math.Clamp(t.CornerRadius, 0, Math.Min(rect.Width, rect.Height) / 2);
        Pen? border = t.Border is { } bc && t.StrokeWidth > 0 ? Pen(bc, t.StrokeWidth) : null;
        if (t.Fill is not null || border is not null)
            dc.DrawRoundedRectangle(t.Fill is { } fill ? Brush(fill) : null, border, rect, rad, rad);
        DrawGlyphs(dc, t, t.Color, TextArea(t));
    }

    private static void DrawGlyphs(DrawingContext dc, TextAnnotation t, Rgba32 color, Rect area)
    {
        var ft = Format(t, color, area.Width);
        var origin = GlyphOrigin(t, ft, area);
        if (t.Outline is { } outline)
        {
            var g = ft.BuildGeometry(origin);
            dc.DrawGeometry(null, Pen(outline, Math.Max(1.5, t.FontSize / 10)), g);
            dc.DrawGeometry(Brush(color), null, g);
        }
        else dc.DrawText(ft, origin);
    }

    private static void DrawCallout(DrawingContext dc, CalloutAnnotation c)
    {
        var rect = c.Bounds.ToRect();
        if (rect.Width <= 0 || rect.Height <= 0) return;
        Geometry body = c.Shape switch
        {
            CalloutShape.Ellipse => new EllipseGeometry(rect),
            CalloutShape.Rectangle => new RectangleGeometry(rect),
            _ => new RectangleGeometry(rect, Math.Min(Math.Max(c.CornerRadius, 8), Math.Min(rect.Width, rect.Height) / 2), Math.Min(Math.Max(c.CornerRadius, 8), Math.Min(rect.Width, rect.Height) / 2)),
        };
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        if (c.CanRotate && c.Rotation != 0)
        {
            body = body.Clone();
            body.Transform = new RotateTransform(c.Rotation, center.X, center.Y);
        }
        Geometry shape = body;
        var tail = c.Tail.ToPoint();
        if (!body.FillContains(tail))
        {
            var dir = tail - center;
            if (dir.Length > 1e-6)
            {
                dir.Normalize();
                var n = new Vector(-dir.Y, dir.X);
                double half = Math.Clamp(c.TailWidth / 2, 1, Math.Max(1, Math.Min(rect.Width, rect.Height) / 2));
                var tri = Polygon(true, center + n * half, tail, center - n * half);
                shape = new CombinedGeometry(GeometryCombineMode.Union, body, tri);
            }
        }
        dc.DrawGeometry(Brush(c.Fill ?? Rgba32.White), c.StrokeWidth > 0 ? Pen(c.Color, c.StrokeWidth) : null, shape);
        bool rotated = PushRotation(dc, c);
        DrawGlyphs(dc, c, c.TextColor, TextArea(c));
        if (rotated) dc.Pop();
    }

    // ------------------------------------------------------------------ steps

    public static Geometry StepShapeGeometry(StepShape shape, Rect r) => shape switch
    {
        StepShape.RoundedSquare => new RectangleGeometry(r, Math.Min(r.Width, r.Height) * 0.22, Math.Min(r.Width, r.Height) * 0.22),
        StepShape.Square => new RectangleGeometry(r),
        StepShape.Diamond => Polygon(true, new Point(r.X + r.Width / 2, r.Y), new Point(r.Right, r.Y + r.Height / 2),
            new Point(r.X + r.Width / 2, r.Bottom), new Point(r.X, r.Y + r.Height / 2)),
        _ => new EllipseGeometry(r),
    };

    private static void DrawStep(DrawingContext dc, StepAnnotation s)
    {
        var r = s.Bounds.ToRect();
        if (r.Width <= 0 || r.Height <= 0) return;
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var fill = Brush(s.Color);
        if (s.Tail is { } tp)
        {
            var tip = tp.ToPoint();
            var dir = tip - c;
            if (dir.Length > Math.Min(r.Width, r.Height) / 2)
            {
                dir.Normalize();
                var n = new Vector(-dir.Y, dir.X);
                double half = Math.Min(r.Width, r.Height) * 0.28;
                dc.DrawGeometry(fill, null, Polygon(true, c + n * half, tip, c - n * half));
            }
        }
        bool rotated = PushRotation(dc, s);
        var shape = StepShapeGeometry(s.Shape, r);
        Pen? pen = s.StrokeWidth > 0 ? Pen(s.Border ?? Rgba32.White, Math.Min(s.StrokeWidth, r.Width / 6)) : null;
        dc.DrawGeometry(fill, pen, shape);

        var label = s.Label;
        if (label.Length > 0)
        {
            var tf = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(s.FontFamily) ? "Segoe UI" : s.FontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            double size = Math.Max(1, Math.Min(r.Width, r.Height) * 0.55);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, Brush(s.TextColor), 1.0);
            double maxW = r.Width * (s.Shape == StepShape.Diamond ? 0.55 : 0.8);
            if (ft.Width > maxW && ft.Width > 0)
            {
                size = Math.Max(1, size * maxW / ft.Width);
                ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, Brush(s.TextColor), 1.0);
            }
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }
        if (rotated) dc.Pop();
    }

    // ------------------------------------------------------------------ freehand

    private static void DrawFreehand(DrawingContext dc, FreehandAnnotation f)
    {
        var pts = f.Points;
        if (pts.Length == 0) return;
        var pen = Pen(f.Color, f.StrokeWidth);
        if (pts.Length == 1)
        {
            dc.DrawEllipse(pen.Brush, null, pts[0].ToPoint(), f.StrokeWidth / 2, f.StrokeWidth / 2);
            return;
        }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0].ToPoint(), false, false);
            c.PolyLineTo(pts.Skip(1).Select(p => p.ToPoint()).ToList(), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    // ------------------------------------------------------------------ stamps

    private static void DrawStamp(DrawingContext dc, StampAnnotation s, Func<string, BitmapSource?>? resolve)
    {
        var r = s.Bounds.ToRect();
        if (r.Width <= 0 || r.Height <= 0) return;
        if (s.AssetId is { } id)
        {
            var bmp = resolve?.Invoke(id);
            if (bmp is null)
            {
                dc.DrawRectangle(Brush(new Rgba32(200, 200, 200, 255)), Pen(Rgba32.Red, 2), r);
                return;
            }
            int q = ((s.QuarterTurns % 4) + 4) % 4;
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            dc.PushTransform(new RotateTransform(90 * q, c.X, c.Y));
            var inner = (q & 1) == 1 ? new Rect(c.X - r.Height / 2, c.Y - r.Width / 2, r.Height, r.Width) : r;
            dc.DrawImage(bmp, inner);
            dc.Pop();
            return;
        }
        DrawSymbol(dc, s.Symbol ?? StampSymbols.Check, r, s.Color);
    }

    public static void DrawSymbol(DrawingContext dc, string symbol, Rect r, Rgba32 color)
    {
        Point P(double x, double y) => new(r.X + x * r.Width, r.Y + y * r.Height);
        double m = Math.Min(r.Width, r.Height);
        var brush = Brush(color);
        switch (symbol)
        {
            case StampSymbols.Check:
                Poly(dc, null, Pen(color, m * 0.14), false, P(0.12, 0.55), P(0.4, 0.82), P(0.9, 0.18));
                break;
            case StampSymbols.Cross:
                {
                    var pen = Pen(color, m * 0.14);
                    dc.DrawLine(pen, P(0.18, 0.18), P(0.82, 0.82));
                    dc.DrawLine(pen, P(0.82, 0.18), P(0.18, 0.82));
                    break;
                }
            case StampSymbols.Star:
                {
                    var pts = new Point[10];
                    for (int i = 0; i < 10; i++)
                    {
                        double ang = -Math.PI / 2 + i * Math.PI / 5;
                        double rad = i % 2 == 0 ? 0.5 : 0.2;
                        pts[i] = P(0.5 + rad * Math.Cos(ang), 0.53 + rad * Math.Sin(ang));
                    }
                    Poly(dc, brush, null, true, pts);
                    break;
                }
            case StampSymbols.Heart:
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(P(0.5, 0.9), true, true);
                        c.BezierTo(P(0.1, 0.62), P(-0.05, 0.3), P(0.25, 0.14), true, true);
                        c.BezierTo(P(0.4, 0.07), P(0.5, 0.2), P(0.5, 0.28), true, true);
                        c.BezierTo(P(0.5, 0.2), P(0.6, 0.07), P(0.75, 0.14), true, true);
                        c.BezierTo(P(1.05, 0.3), P(0.9, 0.62), P(0.5, 0.9), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(brush, null, g);
                    break;
                }
            case StampSymbols.Warning:
                Poly(dc, brush, Pen(color, m * 0.06), true, P(0.5, 0.06), P(0.95, 0.9), P(0.05, 0.9));
                Glyph(dc, "!", r, 0.6, 0.58);
                break;
            case StampSymbols.Info:
                dc.DrawEllipse(brush, null, P(0.5, 0.5), r.Width / 2, r.Height / 2);
                Glyph(dc, "i", r, 0.62, 0.5);
                break;
            case StampSymbols.Question:
                dc.DrawEllipse(brush, null, P(0.5, 0.5), r.Width / 2, r.Height / 2);
                Glyph(dc, "?", r, 0.62, 0.5);
                break;
            case StampSymbols.ThumbUp:
                dc.DrawEllipse(brush, null, P(0.5, 0.5), r.Width / 2, r.Height / 2);
                Glyph(dc, "+1", r, 0.5, 0.5);
                break;
            default:
                dc.DrawRectangle(brush, null, r);
                break;
        }
    }

    private static void Poly(DrawingContext dc, Brush? fill, Pen? pen, bool closed, params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], fill is not null, closed);
            c.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(fill, pen, g);
    }

    private static void Glyph(DrawingContext dc, string text, Rect r, double sizeFraction, double centerY)
    {
        double size = Math.Max(1, Math.Min(r.Width, r.Height) * sizeFraction);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, Brushes.White, 1.0);
        dc.DrawText(ft, new Point(r.X + r.Width / 2 - ft.Width / 2, r.Y + r.Height * centerY - ft.Height / 2));
    }
}
