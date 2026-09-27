namespace SnagItOpen.Core.Geometry;

public static class MathUtil
{
    public static int RoundAway(double v) => checked((int)Math.Round(v, MidpointRounding.AwayFromZero));

    public static int ClampToInt(long v) => v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;

    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
}
