using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Layout;

namespace SnagItOpen.Core.Tests;

public class LayoutEngineTests
{
    private static ImageLayer[] Pair() => [TestData.Layer(TestData.Asset(100, 50)), TestData.Layer(TestData.Asset(80, 30))];

    [Fact]
    public void Vertical_center_gap10_padding5_matches_oracle()
    {
        var r = LayoutEngine.Arrange(Pair(), TestData.Vertical());
        Assert.Equal(new PixelRect(0, 0, 110, 100), r.ExportArea);
        Assert.Equal(new PixelRect(5, 5, 100, 50), r.Images[0].Bounds);
        Assert.Equal(new PixelRect(15, 65, 80, 30), r.Images[1].Bounds);
    }

    [Fact]
    public void Horizontal_center_gap10_padding5_matches_oracle()
    {
        var r = LayoutEngine.Arrange(Pair(), TestData.Vertical() with { Mode = LayoutMode.Horizontal });
        Assert.Equal(new PixelRect(0, 0, 200, 60), r.ExportArea);
        Assert.Equal(new PixelRect(115, 15, 80, 30), r.Images[1].Bounds);
    }

    [Fact]
    public void Match_width_with_upscale_gives_110x108()
    {
        var r = LayoutEngine.Arrange(Pair(), TestData.Vertical() with { Scale = ScaleMode.MatchCrossAxis, TargetCrossPixels = 100, AllowUpscale = true });
        Assert.Equal(new PixelRect(0, 0, 110, 108), r.ExportArea);
        Assert.Equal(new PixelSize(100, 38), r.Images[1].Bounds.Size);
    }

    [Fact]
    public void Match_width_without_upscale_keeps_small_image()
    {
        var r = LayoutEngine.Arrange(Pair(), TestData.Vertical() with { Scale = ScaleMode.MatchCrossAxis, TargetCrossPixels = 100 });
        Assert.Equal(new PixelSize(80, 30), r.Images[1].Bounds.Size);
    }

    [Theory]
    [InlineData(CrossAlignment.Start, 5)]
    [InlineData(CrossAlignment.Center, 15)]
    [InlineData(CrossAlignment.End, 25)]
    public void Alignments(CrossAlignment align, int expectedX)
    {
        var r = LayoutEngine.Arrange(Pair(), TestData.Vertical(align: align));
        Assert.Equal(expectedX, r.Images[1].Bounds.X);
    }

    [Fact]
    public void Zero_and_one_images()
    {
        var empty = LayoutEngine.Arrange([], TestData.Vertical(pad: 0));
        Assert.Equal(new PixelRect(0, 0, 1, 1), empty.ExportArea);
        var one = LayoutEngine.Arrange([TestData.Layer(TestData.Asset(100, 50))], TestData.Vertical());
        Assert.Equal(new PixelRect(0, 0, 110, 60), one.ExportArea);
    }

    [Fact]
    public void Invisible_images_consume_no_space()
    {
        var p = Pair();
        p[0] = p[0] with { Visible = false };
        var r = LayoutEngine.Arrange(p, TestData.Vertical());
        Assert.Equal(new PixelRect(5, 5, 80, 30), r.Images[1].Bounds with { X = 5 });
        Assert.Equal(40, r.ExportArea.Height);
    }

    [Fact]
    public void Quarter_turn_swaps_dimensions()
    {
        var l = TestData.Layer(TestData.Asset(30, 70)) with { QuarterTurns = 1 };
        var r = LayoutEngine.Arrange([l], TestData.Vertical(pad: 0));
        Assert.Equal(new PixelSize(70, 30), r.Images[0].Bounds.Size);
    }

    [Fact]
    public void Input_is_not_mutated_and_order_is_stable()
    {
        var p = Pair();
        var before = p.Select(x => x.Bounds).ToArray();
        var r = LayoutEngine.Arrange(p, TestData.Vertical());
        Assert.Equal(before, p.Select(x => x.Bounds).ToArray());
        Assert.Equal(p.Select(x => x.Id), r.Images.Select(x => x.Id));
    }

    [Fact]
    public void Over_limit_result_is_rejected()
    {
        var big = Enumerable.Range(0, 3).Select(_ => TestData.Layer(TestData.Asset(100, 15000))).ToArray();
        Assert.Throws<LayoutLimitException>(() => LayoutEngine.Arrange(big, TestData.Vertical()));
    }

    [Fact]
    public void Free_mode_and_bad_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => LayoutEngine.Arrange(Pair(), LayoutOptions.Default with { Mode = LayoutMode.Free }));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutEngine.Arrange(Pair(), TestData.Vertical(gap: -1)));
    }
}
