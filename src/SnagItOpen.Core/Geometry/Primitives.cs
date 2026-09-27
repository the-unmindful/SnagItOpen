using System.Globalization;
using System.Text.Json.Serialization;

namespace SnagItOpen.Core.Geometry;

/// <summary>Integer pixel dimensions.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    [JsonIgnore] public long Area => IsEmpty ? 0 : (long)Width * Height;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;
    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>Integer pixel point.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>
/// Half-open integer rectangle: pixels X..X+Width-1 and Y..Y+Height-1 are inside,
/// right/bottom edges are excluded.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Right => checked(X + Width);
    [JsonIgnore] public int Bottom => checked(Y + Height);
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;
    [JsonIgnore] public PixelSize Size => new(Width, Height);
    [JsonIgnore] public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, checked(right - left), checked(bottom - top));

    /// <summary>Normalizes a drag between two points into a rectangle spanning both (exclusive of max).</summary>
    public static PixelRect FromPoints(PixelPoint a, PixelPoint b) =>
        FromEdges(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public bool Contains(int x, int y) =>
        x >= X && y >= Y && x < (long)X + Width && y < (long)Y + Height;

    public bool Contains(PixelRect r) =>
        !r.IsEmpty && r.X >= X && r.Y >= Y && (long)r.X + r.Width <= (long)X + Width && (long)r.Y + r.Height <= (long)Y + Height;

    public PixelRect Intersect(PixelRect other)
    {
        long l = Math.Max(X, other.X), t = Math.Max(Y, other.Y);
        long r = Math.Min((long)X + Width, (long)other.X + other.Width);
        long b = Math.Min((long)Y + Height, (long)other.Y + other.Height);
        if (r <= l || b <= t) return default;
        return new PixelRect((int)l, (int)t, (int)(r - l), (int)(b - t));
    }

    public bool IntersectsWith(PixelRect other) => !Intersect(other).IsEmpty;

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        long l = Math.Min(X, other.X), t = Math.Min(Y, other.Y);
        long r = Math.Max((long)X + Width, (long)other.X + other.Width);
        long b = Math.Max((long)Y + Height, (long)other.Y + other.Height);
        return new PixelRect((int)l, (int)t, checked((int)(r - l)), checked((int)(b - t)));
    }

    public PixelRect Translate(int dx, int dy) => new(checked(X + dx), checked(Y + dy), Width, Height);
    public PixelRect Inflate(int n) => new(checked(X - n), checked(Y - n), checked(Width + 2 * n), checked(Height + 2 * n));
    public RectD ToRectD() => new(X, Y, Width, Height);
    public override string ToString() => $"({X},{Y},{Width},{Height})";
}

/// <summary>Straight (non-premultiplied) RGBA color. Serialized as "#RRGGBBAA".</summary>
[JsonConverter(typeof(Rgba32JsonConverter))]
public readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    public static readonly Rgba32 Transparent = new(0, 0, 0, 0);
    public static readonly Rgba32 White = new(255, 255, 255, 255);
    public static readonly Rgba32 Black = new(0, 0, 0, 255);
    public static readonly Rgba32 Red = new(230, 40, 40, 255);

    public Rgba32 WithAlpha(byte a) => this with { A = a };

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public static bool TryParse(string? text, out Rgba32 color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        if (s.Length == 6) v = (v << 8) | 0xFF;
        color = new Rgba32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}

/// <summary>Floating point point, used for pointer math and vector annotation geometry.</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public static PointD operator *(PointD a, double s) => new(a.X * s, a.Y * s);
    [JsonIgnore] public double Length => Math.Sqrt(X * X + Y * Y);
    public static double Distance(PointD a, PointD b) => (a - b).Length;
}

/// <summary>Floating point rectangle.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;
    [JsonIgnore] public bool IsEmpty => !(Width > 0) || !(Height > 0);
    [JsonIgnore] public PointD Center => new(X + Width / 2, Y + Height / 2);
    [JsonIgnore] public PointD TopLeft => new(X, Y);
    [JsonIgnore] public PointD BottomRight => new(Right, Bottom);

    public static RectD FromPoints(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static RectD FromEdges(double l, double t, double r, double b) => new(l, t, r - l, b - t);

    public bool Contains(PointD p) => p.X >= X && p.Y >= Y && p.X <= Right && p.Y <= Bottom;

    public RectD Union(RectD o)
    {
        if (Width <= 0 && Height <= 0 && X == 0 && Y == 0) return o;
        if (o.Width <= 0 && o.Height <= 0 && o.X == 0 && o.Y == 0) return this;
        return FromEdges(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    }

    public RectD Intersect(RectD o)
    {
        double l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? default : FromEdges(l, t, r, b);
    }

    public RectD Inflate(double n) => new(X - n, Y - n, Width + 2 * n, Height + 2 * n);
    public RectD Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>Smallest integer rectangle containing this one (floor min, ceil max).</summary>
    public PixelRect ToPixelRectOutward() =>
        PixelRect.FromEdges((int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Ceiling(Right), (int)Math.Ceiling(Bottom));

    /// <summary>Rounds each edge away from zero once.</summary>
    public PixelRect ToPixelRectRounded() =>
        PixelRect.FromEdges(Round(X), Round(Y), Round(Right), Round(Bottom));

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}

/// <summary>
/// 2D affine matrix using the WPF convention for row vectors:
/// x' = x*M11 + y*M21 + OffsetX; y' = x*M12 + y*M22 + OffsetY.
/// </summary>
public readonly record struct Affine(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);
    public static Affine Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);
    public static Affine Scaling(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>Returns a transform that applies this, then <paramref name="next"/>.</summary>
    public Affine Then(Affine next) => new(
        M11 * next.M11 + M12 * next.M21,
        M11 * next.M12 + M12 * next.M22,
        M21 * next.M11 + M22 * next.M21,
        M21 * next.M12 + M22 * next.M22,
        OffsetX * next.M11 + OffsetY * next.M21 + next.OffsetX,
        OffsetX * next.M12 + OffsetY * next.M22 + next.OffsetY);

    public PointD Transform(PointD p) => new(p.X * M11 + p.Y * M21 + OffsetX, p.X * M12 + p.Y * M22 + OffsetY);

    public Affine Inverse()
    {
        var det = M11 * M22 - M12 * M21;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Transform is not invertible.");
        return new Affine(
            M22 / det, -M12 / det, -M21 / det, M11 / det,
            (M21 * OffsetY - M22 * OffsetX) / det,
            (M12 * OffsetX - M11 * OffsetY) / det);
    }

    public RectD TransformBounds(RectD r)
    {
        var a = Transform(new PointD(r.X, r.Y));
        var b = Transform(new PointD(r.Right, r.Y));
        var c = Transform(new PointD(r.X, r.Bottom));
        var d = Transform(new PointD(r.Right, r.Bottom));
        return RectD.FromEdges(
            Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
            Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
            Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
            Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
    }
}
