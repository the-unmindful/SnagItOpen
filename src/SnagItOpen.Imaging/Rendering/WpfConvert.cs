using System.Windows;
using System.Windows.Media;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Imaging.Rendering;

/// <summary>Conversions between Core geometry/colors and WPF types, plus frozen brush/pen helpers.</summary>
public static class WpfConvert
{
    public static Color ToColor(this Rgba32 c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public static Rgba32 ToRgba(this Color c) => new(c.R, c.G, c.B, c.A);

    public static Rect ToRect(this RectD r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));
    public static Rect ToRect(this PixelRect r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));
    public static RectD ToRectD(this Rect r) => new(r.X, r.Y, r.Width, r.Height);
    public static Point ToPoint(this PointD p) => new(p.X, p.Y);
    public static PointD ToPointD(this Point p) => new(p.X, p.Y);
    public static Matrix ToMatrix(this Affine a) => new(a.M11, a.M12, a.M21, a.M22, a.OffsetX, a.OffsetY);

    public static SolidColorBrush Brush(Rgba32 c)
    {
        var b = new SolidColorBrush(c.ToColor());
        b.Freeze();
        return b;
    }

    public static Pen Pen(Rgba32 c, double width, bool dashed = false, PenLineCap cap = PenLineCap.Round)
    {
        var p = new Pen(Brush(c), Math.Max(0.01, width))
        {
            StartLineCap = cap,
            EndLineCap = cap,
            DashCap = cap,
            LineJoin = PenLineJoin.Round,
        };
        if (dashed) p.DashStyle = DashStyles.Dash;
        p.Freeze();
        return p;
    }

    public static bool IsFontAvailable(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return false;
        foreach (var f in Fonts.SystemFontFamilies)
            if (string.Equals(f.Source, family, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
