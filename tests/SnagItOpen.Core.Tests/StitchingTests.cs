using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Stitching;

namespace SnagItOpen.Core.Tests;

public class SeamGeometryTests
{
    [Fact]
    public void Vertical_overlap80_gives_100x520()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 100, 300), new PixelRect(0, 400, 100, 300));
        var j = SeamGeometry.Join(d, d.Images[0].Id, d.Images[1].Id, SeamAxis.Vertical, 80);
        Assert.Equal(new PixelRect(0, 300, 100, 220), j.Images[1].Bounds);
        Assert.Equal(new PixelRect(0, 80, 100, 220), j.Images[1].SourceCrop);
        var fit = Editing.DocumentOps.FitCanvas(j, 0);
        Assert.Equal(new PixelSize(100, 520), fit.ExportArea.Size);
    }

    [Fact]
    public void Rejoin_is_idempotent_and_editable()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 100, 300), new PixelRect(0, 400, 100, 300));
        var j1 = SeamGeometry.Join(d, d.Images[0].Id, d.Images[1].Id, SeamAxis.Vertical, 80);
        var j2 = SeamGeometry.Join(j1, d.Images[0].Id, d.Images[1].Id, SeamAxis.Vertical, 50);
        Assert.Equal(new PixelRect(0, 50, 100, 250), j2.Images[1].SourceCrop);
    }

    [Fact]
    public void Horizontal_transposes()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 300, 100), new PixelRect(0, 200, 300, 100));
        var j = SeamGeometry.Join(d, d.Images[0].Id, d.Images[1].Id, SeamAxis.Horizontal, 80);
        Assert.Equal(new PixelRect(300, 0, 220, 100), j.Images[1].Bounds);
    }

    [Fact]
    public void Invalid_overlaps()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 100, 300), new PixelRect(0, 0, 100, 300));
        Assert.NotNull(SeamGeometry.Validate(d.Images[0], d.Images[1], SeamAxis.Vertical, -1));
        Assert.NotNull(SeamGeometry.Validate(d.Images[0], d.Images[1], SeamAxis.Vertical, 300));
        Assert.Null(SeamGeometry.Validate(d.Images[0], d.Images[1], SeamAxis.Vertical, 0));
        var e = TestData.FreeDoc(new PixelRect(0, 0, 100, 300), new PixelRect(0, 0, 90, 300));
        Assert.NotNull(SeamGeometry.Validate(e.Images[0], e.Images[1], SeamAxis.Vertical, 10));
    }
}

public class OverlapMatcherTests
{
    private const int W = 100;

    /// <summary>Deterministic "page": strong per-row variation plus small column texture.</summary>
    private static float Page(int x, int y)
    {
        double r = Math.Sin(y * 12.9898 + 78.233) * 43758.5453;
        r -= Math.Floor(r);
        return (float)(r * 0.8 + (x % 7) / 70.0);
    }

    private static LumaImage Frame(Func<int, int, float> f, int top, int height, int header = 0)
    {
        var data = new float[W * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < W; x++)
                data[y * W + x] = y < header ? 0.5f + (x % 3) * 0.1f : f(x, top + y - header);
        return new LumaImage(W, height, data);
    }

    [Fact]
    public void Known_overlap80_is_confident()
    {
        var s = OverlapMatcher.FindVertical(Frame(Page, 0, 300), Frame(Page, 220, 300));
        Assert.Equal(OverlapConfidence.Confident, s.Confidence);
        Assert.Equal(80, s.Overlap);
    }

    [Fact]
    public void Fixed_header_is_excluded()
    {
        var prev = Frame(Page, 0, 300, header: 30);   // content rows 0..269
        var next = Frame(Page, 190, 300, header: 30); // content rows 190..459 → content overlap 80
        var s = OverlapMatcher.FindVertical(prev, next, new OverlapOptions { HeaderRows = 30 });
        Assert.Equal(OverlapConfidence.Confident, s.Confidence);
        Assert.Equal(110, s.Overlap);
    }

    [Fact]
    public void Uniform_blank_is_low_texture()
    {
        var s = OverlapMatcher.FindVertical(Frame((_, _) => 1f, 0, 300), Frame((_, _) => 1f, 100, 300));
        Assert.False(s.IsConfident);
        Assert.Equal(OverlapConfidence.LowTexture, s.Confidence);
    }

    [Fact]
    public void Repeated_rows_are_ambiguous()
    {
        float Rows(int x, int y) => Page(x, y % 20);
        var s = OverlapMatcher.FindVertical(Frame(Rows, 0, 300), Frame(Rows, 220, 300));
        Assert.False(s.IsConfident);
    }

    [Fact]
    public void No_overlap_is_not_confident()
    {
        var s = OverlapMatcher.FindVertical(Frame(Page, 0, 300), Frame(Page, 5000, 300));
        Assert.False(s.IsConfident);
    }

    [Fact]
    public void Identical_frames_detected_as_same()
    {
        Assert.True(OverlapMatcher.AreSame(Frame(Page, 0, 300), Frame(Page, 0, 300)));
        Assert.False(OverlapMatcher.AreSame(Frame(Page, 0, 300), Frame(Page, 10, 300)));
    }
}
