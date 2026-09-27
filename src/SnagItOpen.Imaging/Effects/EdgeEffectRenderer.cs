using System.Windows;
using System.Windows.Media;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;

namespace SnagItOpen.Imaging.Effects;

/// <summary>
/// Border, shadow, rounded-corner and torn-edge rendering for image layers. Order:
/// shadow → image clipped to edge shape → border. Torn geometry is deterministic from the stored seed.
/// </summary>
public static class EdgeEffectRenderer
{
    public const int MaxShadow = 64;

    /// <summary>True when the image must be clipped to a non-rectangular shape.</summary>
    public static bool ClipsImage(EdgeStyle? e) => e is not null && (e.CornerRadius > 0 || e.TornSides != TornSides.None);

    public static Geometry Shape(PixelRect bounds, EdgeStyle? e)
    {
        var r = bounds.ToRect();
        Geometry g;
        if (e is not null && e.TornSides != TornSides.None) g = Polygon(TornPoints(r, e));
        else if (e is not null && e.CornerRadius > 0)
        {
            double rad = Math.Min(e.CornerRadius, Math.Min(r.Width, r.Height) / 2);
            g = new RectangleGeometry(r, rad, rad);
        }
        else g = new RectangleGeometry(r);
        g.Freeze();
        return g;
    }

    /// <summary>Deterministic torn outline: jagged inward insets on the selected sides.</summary>
    public static IReadOnlyList<Point> TornPoints(Rect r, EdgeStyle e)
    {
        double depth = Math.Clamp(e.TornDepth, 2, 64);
        depth = Math.Min(depth, Math.Min(r.Width, r.Height) / 3);
        double step = Math.Clamp(depth * 1.6, 6, 48);
        var pts = new List<Point>();
        var sides = new (TornSides Side, Point A, Point B, Vector Inward)[]
        {
            (TornSides.Top, r.TopLeft, r.TopRight, new Vector(0, 1)),
            (TornSides.Right, r.TopRight, r.BottomRight, new Vector(-1, 0)),
            (TornSides.Bottom, r.BottomRight, r.BottomLeft, new Vector(0, -1)),
            (TornSides.Left, r.BottomLeft, r.TopLeft, new Vector(1, 0)),
        };
        for (int s = 0; s < sides.Length; s++)
        {
            var (side, a, b, inward) = sides[s];
            bool torn = (e.TornSides & side) != 0;
            double len = (b - a).Length;
            int n = torn ? Math.Max(1, (int)(len / step)) : 1;
            uint state = unchecked((uint)e.TornSeed * 2654435761u ^ (uint)(s + 1) * 0x9E3779B9u);
            if (state == 0) state = 0x12345678;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                var p = a + (b - a) * t;
                if (torn && i > 0) p += inward * (Next(ref state) * depth);
                pts.Add(p);
            }
        }
        return pts;
    }

    private static double Next(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return (s & 0xFFFFFF) / (double)0x1000000;
    }

    private static Geometry Polygon(IReadOnlyList<Point> pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            c.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        return g;
    }

    /// <summary>Soft shadow approximated with stacked translucent strokes (deterministic, no bitmap effects).</summary>
    public static void DrawShadow(DrawingContext dc, Geometry shape, EdgeStyle e)
    {
        int s = Math.Clamp(e.ShadowSize, 1, MaxShadow);
        double target = e.ShadowColor.A / 255.0;
        double layer = 1 - Math.Pow(1 - Math.Min(target, 0.999), 1.0 / (s + 1));
        var brush = WpfConvert.Brush(e.ShadowColor with { A = (byte)Math.Clamp(Math.Round(layer * 255), 1, 255) });
        dc.PushTransform(new TranslateTransform(e.ShadowOffsetX, e.ShadowOffsetY));
        dc.DrawGeometry(brush, null, shape);
        for (int k = 1; k <= s; k++)
        {
            var pen = new Pen(brush, 2 * k) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, shape);
        }
        dc.Pop();
    }

    public static void DrawBorder(DrawingContext dc, Geometry shape, EdgeStyle e)
    {
        if (e.BorderWidth <= 0) return;
        dc.DrawGeometry(null, WpfConvert.Pen(e.BorderColor, e.BorderWidth, cap: PenLineCap.Flat), shape);
    }
}
