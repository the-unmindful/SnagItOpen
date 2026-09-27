namespace SnagItOpen.Core.Geometry;

/// <summary>HSV conversions for the colour picker. Hue 0–360, saturation and value 0–1.</summary>
public static class ColorMath
{
    public static (double H, double S, double V) ToHsv(Rgba32 c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max <= 0 ? 0 : d / max, max);
    }

    public static Rgba32 FromHsv(double h, double s, double v, byte a = 255)
    {
        h = double.IsFinite(h) ? ((h % 360) + 360) % 360 : 0;
        s = Math.Clamp(double.IsFinite(s) ? s : 0, 0, 1);
        v = Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return new Rgba32(B(r + m), B(g + m), B(b + m), a);
    }
}
