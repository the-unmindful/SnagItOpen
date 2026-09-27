namespace SnagItOpen.Core.Geometry;

/// <summary>Rotation helpers. Angles are clockwise degrees in y-down document space.</summary>
public static class Rotation2D
{
    public static double Normalize(double degrees)
    {
        if (!double.IsFinite(degrees)) return 0;
        var d = degrees % 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return Math.Abs(d) < 1e-9 ? 0 : d;
    }

    /// <summary>Rotates <paramref name="p"/> about <paramref name="c"/> clockwise by <paramref name="degrees"/>.</summary>
    public static PointD Rotate(PointD p, PointD c, double degrees)
    {
        if (degrees == 0) return p;
        double r = degrees * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
        double dx = p.X - c.X, dy = p.Y - c.Y;
        return new PointD(c.X + dx * cos - dy * sin, c.Y + dx * sin + dy * cos);
    }

    /// <summary>The four corners of <paramref name="r"/> rotated about its centre (TL, TR, BR, BL).</summary>
    public static PointD[] Corners(RectD r, double degrees)
    {
        var c = r.Center;
        return
        [
            Rotate(new PointD(r.X, r.Y), c, degrees),
            Rotate(new PointD(r.Right, r.Y), c, degrees),
            Rotate(new PointD(r.Right, r.Bottom), c, degrees),
            Rotate(new PointD(r.X, r.Bottom), c, degrees),
        ];
    }

    /// <summary>Axis-aligned bounding box of <paramref name="r"/> rotated about its centre.</summary>
    public static RectD RotateRect(RectD r, double degrees)
    {
        if (degrees == 0) return r;
        var pts = Corners(r, degrees);
        double l = pts.Min(p => p.X), t = pts.Min(p => p.Y), rr = pts.Max(p => p.X), b = pts.Max(p => p.Y);
        return RectD.FromEdges(l, t, rr, b);
    }

    /// <summary>Clockwise angle in degrees of the vector from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static double AngleOf(PointD a, PointD b) => Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;

    public static double Snap(double degrees, double step) => step <= 0 ? degrees : Math.Round(degrees / step) * step;
}
