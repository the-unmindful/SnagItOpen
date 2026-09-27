using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public class ColorMathTests
{
    [Theory]
    [InlineData(255, 0, 0, 0, 1, 1)]
    [InlineData(0, 255, 0, 120, 1, 1)]
    [InlineData(0, 0, 255, 240, 1, 1)]
    [InlineData(255, 255, 255, 0, 0, 1)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    public void Known_colours_convert_to_hsv(byte r, byte g, byte b, double h, double s, double v)
    {
        var (hh, ss, vv) = ColorMath.ToHsv(new Rgba32(r, g, b, 255));
        Assert.Equal(h, hh, 3);
        Assert.Equal(s, ss, 3);
        Assert.Equal(v, vv, 3);
    }

    [Fact]
    public void Round_trip_is_exact_for_every_sampled_colour()
    {
        for (int r = 0; r < 256; r += 17)
            for (int g = 0; g < 256; g += 17)
                for (int b = 0; b < 256; b += 17)
                {
                    var c = new Rgba32((byte)r, (byte)g, (byte)b, 200);
                    var (h, s, v) = ColorMath.ToHsv(c);
                    Assert.Equal(c, ColorMath.FromHsv(h, s, v, 200));
                }
    }

    [Fact]
    public void Out_of_range_inputs_are_clamped()
    {
        Assert.Equal(new Rgba32(255, 0, 0, 255), ColorMath.FromHsv(720, 5, 9));
        Assert.Equal(new Rgba32(0, 0, 0, 255), ColorMath.FromHsv(double.NaN, -1, -1));
    }
}
