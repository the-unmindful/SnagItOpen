using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using static SnagItOpen.Imaging.Rendering.WpfConvert;

namespace SnagItOpen.Imaging.Rendering;

/// <summary>
/// Draws vector annotations in their own coordinate space (the caller pushes the image transform for
/// linked annotations). Redactions and magnifiers are handled by <see cref="DocumentRenderer"/>.
/// </summary>
public static class AnnotationRenderer
{
    public const double TextPadding = 6;

    public static void Draw(DrawingContext dc, Annotation a, Func<string, BitmapSource?>? resolveAsset = null)
    {
        switch (a)
        {
            case RectangleAnnotation r:
                {
                    var rect = r.Bounds.ToRect();
                    dc.DrawRoundedRectangle(r.Fill is { } f ? Brush(f) : null, r.StrokeWidth > 0 ? Pen(r.Color, r.StrokeWidth) : null,
                        rect, r.CornerRadius, r.CornerRadius);
                    break;
                }
            case EllipseAnnotation e:
                {
                    var rect = e.Bounds.ToRect();
                    dc.DrawEllipse(e.Fill is { } f ? Brush(f) : null, e.StrokeWidth > 0 ? Pen(e.Color, e.StrokeWidth) : null,
                        new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                    break;
                }
            case ArrowAnnotation ar:
                DrawLine(dc, ar, arrow: true, doubleHeaded: ar.DoubleHeaded);
                break;
            case LineAnnotation l:
                DrawLine(dc, l, arrow: false, doubleHeaded: false);
                break;
            case CalloutAnnotation c:
                DrawCallout(dc, c);
                break;
            case TextAnnotation t:
                DrawText(dc, t);
                break;
            case HighlightAnnotation h:
                dc.DrawRectangle(Brush(h.Color with { A = h.Opacity }), null, h.Bounds.ToRect());
                break;
            case StepAnnotation s:
                DrawStep(dc, s);
                break;
            case FreehandAnnotation fh:
                DrawFreehand(dc, fh);
                break;
            case StampAnnotation st:
                DrawStamp(dc, st, resolveAsset);
                break;
            case RedactionAnnotation rd:
                dc.DrawRectangle(Brush(rd.Color with { A = 255 }), null, rd.Bounds.ToRect());
                break;
        }
    }

    // ------------------------------------------------------------------ lines

    private static void DrawLine(DrawingContext dc, LineAnnotation l, bool arrow, bool doubleHeaded)
    {
        var s = l.Start.ToPoint();
        var e = l.End.ToPoint();
        var pen = Pen(l.Color, l.StrokeWidth, l.Dashed);
        var v = e - s;
        double len = v.Length;
        if (len < 0.5)
        {
            dc.DrawEllipse(pen.Brush, null, s, l.StrokeWidth / 2, l.StrokeWidth / 2);
            return;
        }
        v.Normalize();
        double head = Math.Max(8, l.StrokeWidth * 3.5);
        head = Math.Min(head, len * (doubleHeaded ? 0.45 : 0.7));
        var lineStart = doubleHeaded && arrow ? s + v * head * 0.8 : s;
        var lineEnd = arrow ? e - v * head * 0.8 : e;
        dc.DrawLine(pen, lineStart, lineEnd);
        if (arrow)
        {
            DrawHead(dc, pen.Brush, e, v, head);
            if (doubleHeaded) DrawHead(dc, pen.Brush, s, -v, head);
        }
    }

    private static void DrawHead(DrawingContext dc, Brush brush, Point tip, Vector dir, double size)
    {
        var n = new Vector(-dir.Y, dir.X);
        var b = tip - dir * size;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(tip, true, true);
            c.LineTo(b + n * size * 0.5, true, true);
            c.LineTo(b - n * size * 0.5, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
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
        return ft;
    }

    /// <summary>Height needed to show all text inside a box of the annotation's width.</summary>
    public static double MeasureTextHeight(TextAnnotation t)
    {
        var ft = Format(t, t.Color, Math.Max(1, t.Bounds.Width - 2 * TextPadding));
        return ft.Height + 2 * TextPadding;
    }

    private static void DrawText(DrawingContext dc, TextAnnotation t)
    {
        var rect = t.Bounds.ToRect();
        if (t.Fill is { } fill) dc.DrawRoundedRectangle(Brush(fill), null, rect, 4, 4);
        var ft = Format(t, t.Color, Math.Max(1, rect.Width - 2 * TextPadding));
        var origin = new Point(rect.X + TextPadding, rect.Y + TextPadding);
        if (t.Outline is { } outline)
        {
            var g = ft.BuildGeometry(origin);
            dc.DrawGeometry(null, Pen(outline, Math.Max(1.5, t.FontSize / 10)), g);
            dc.DrawGeometry(Brush(t.Color), null, g);
        }
        else dc.DrawText(ft, origin);
    }

    private static void DrawCallout(DrawingContext dc, CalloutAnnotation c)
    {
        var rect = c.Bounds.ToRect();
        var body = new RectangleGeometry(rect, 8, 8);
        Geometry shape = body;
        var tail = c.Tail.ToPoint();
        if (!rect.Contains(tail) && rect.Width > 0 && rect.Height > 0)
        {
            var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var dir = tail - center;
            dir.Normalize();
            var n = new Vector(-dir.Y, dir.X);
            double half = Math.Min(14, Math.Min(rect.Width, rect.Height) / 4);
            var tri = new StreamGeometry();
            using (var ctx = tri.Open())
            {
                ctx.BeginFigure(center + n * half, true, true);
                ctx.LineTo(tail, true, true);
                ctx.LineTo(center - n * half, true, true);
            }
            shape = new CombinedGeometry(GeometryCombineMode.Union, body, tri);
        }
        dc.DrawGeometry(Brush(c.Fill ?? Rgba32.White), c.StrokeWidth > 0 ? Pen(c.Color, c.StrokeWidth) : null, shape);
        var ft = Format(c, c.TextColor, Math.Max(1, rect.Width - 2 * TextPadding));
        dc.DrawText(ft, new Point(rect.X + TextPadding, rect.Y + TextPadding));
    }

    // ------------------------------------------------------------------ steps / freehand

    private static void DrawStep(DrawingContext dc, StepAnnotation s)
    {
        var r = s.Bounds.ToRect();
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        dc.DrawEllipse(Brush(s.Color), s.StrokeWidth > 0 ? Pen(Rgba32.White, Math.Min(s.StrokeWidth, r.Width / 8)) : null, c, r.Width / 2, r.Height / 2);
        double size = Math.Max(1, Math.Min(r.Width, r.Height) * 0.55);
        var ft = new FormattedText(s.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, Brush(s.TextColor), 1.0);
        dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
    }

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
