using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Layout;

namespace SnagItOpen.Core.Tests;

public class ViewportTransformTests
{
    [Theory]
    [InlineData(0.1)]
    [InlineData(1.0)]
    [InlineData(8.0)]
    public void Roundtrip_points_including_negative(double zoom)
    {
        var v = new ViewportTransform(zoom, 37.5, -12.25);
        foreach (var p in new[] { new PointD(0, 0), new PointD(-250, -13), new PointD(1234.5, 88) })
        {
            var back = v.ToDocument(v.ToViewport(p));
            Assert.Equal(p.X, back.X, 9);
            Assert.Equal(p.Y, back.Y, 9);
        }
    }

    [Fact]
    public void Zoom_about_pointer_keeps_anchor_stable()
    {
        var v = new ViewportTransform(1, 10, 20);
        var anchor = new PointD(300, 200);
        var docBefore = v.ToDocument(anchor);
        var z = v.ZoomAbout(anchor, 3.5);
        var docAfter = z.ToDocument(anchor);
        Assert.Equal(docBefore.X, docAfter.X, 9);
        Assert.Equal(docBefore.Y, docAfter.Y, 9);
        Assert.Equal(ViewportTransform.MaxZoom, v.ZoomAbout(anchor, 100).Zoom);
    }
}

public class ResizeGeometryTests
{
    [Fact]
    public void Corner_resize_keeps_aspect_and_opposite_corner()
    {
        var start = new PixelRect(10, 10, 100, 50);
        var r = ResizeGeometry.Resize(start, ResizeHandle.BottomRight, 100, 0, keepAspect: true);
        Assert.Equal(new PixelRect(10, 10, 200, 100), r);

        var tl = ResizeGeometry.Resize(start, ResizeHandle.TopLeft, -100, 0, keepAspect: true);
        Assert.Equal(start.Right, tl.Right);
        Assert.Equal(start.Bottom, tl.Bottom);
        Assert.Equal(new PixelSize(200, 100), tl.Size);
    }

    [Fact]
    public void Unlocked_resize_is_independent_and_minimum_1()
    {
        var start = new PixelRect(0, 0, 100, 50);
        Assert.Equal(new PixelRect(0, 0, 150, 80), ResizeGeometry.Resize(start, ResizeHandle.BottomRight, 50, 30, keepAspect: false));
        var tiny = ResizeGeometry.Resize(start, ResizeHandle.BottomRight, -500, -500, keepAspect: false);
        Assert.True(tiny.Width >= 1 && tiny.Height >= 1);
    }

    [Fact]
    public void Numeric_width_matches_handle_result()
    {
        Assert.Equal(new PixelRect(10, 10, 200, 100), ResizeGeometry.WithWidth(new PixelRect(10, 10, 100, 50), 200, keepAspect: true));
    }
}

public class SnapEngineTests
{
    private static readonly SnapEngine.Candidate[] Targets = [new("b", new RectD(100, 0, 50, 50))];

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(4.0)]
    public void Tolerance_is_constant_in_viewport_dips(double zoom)
    {
        // Moving rect's right edge at 95 + offset. 5 DIPs away snaps; 7 DIPs away does not.
        var moving = new RectD(0, 200, 95, 10);
        double inside = 5 / zoom, outside = 7 / zoom;
        var s1 = SnapEngine.SnapMove(moving, 100 - 95 - inside, 0, Targets, zoom);
        Assert.Equal(5, s1.Dx + 0, 6); // snapped so right edge == 100
        var s2 = SnapEngine.SnapMove(moving, 5 - outside, 0, Targets, zoom);
        Assert.Equal(5 - outside, s2.Dx, 6);
        Assert.DoesNotContain(s2.Guides, g => g.Vertical);
    }

    [Fact]
    public void Ties_break_by_id()
    {
        var t = new[] { new SnapEngine.Candidate("z", new RectD(10, 0, 5, 5)), new SnapEngine.Candidate("a", new RectD(10, 50, 5, 5)) };
        var (_, g) = SnapEngine.SnapValue(9, true, t, 1);
        Assert.Equal("a", g!.Value.SourceId);
    }
}

public class ImageTransformTests
{
    [Fact]
    public void Quarter_turn_maps_top_left_to_top_right()
    {
        var a = TestData.Asset(2, 3);
        var l = TestData.Layer(a) with { QuarterTurns = 1, Bounds = new PixelRect(0, 0, 3, 2) };
        var m = ImageTransform.SourceToDocument(l);
        var p = m.Transform(new PointD(0.5, 0.5)); // center of source top-left pixel
        Assert.Equal(2.5, p.X, 9);
        Assert.Equal(0.5, p.Y, 9);
        var inv = ImageTransform.DocumentToSource(l).Transform(p);
        Assert.Equal(0.5, inv.X, 9);
        Assert.Equal(0.5, inv.Y, 9);
    }

    [Fact]
    public void Flip_horizontal_mirrors_within_crop()
    {
        var a = TestData.Asset(10, 10);
        var l = TestData.Layer(a) with { SourceCrop = new PixelRect(2, 0, 4, 10), Bounds = new PixelRect(0, 0, 4, 10), FlipHorizontal = true };
        var p = ImageTransform.SourceToDocument(l).Transform(new PointD(2.5, 0));
        Assert.Equal(3.5, p.X, 9);
    }
}

public class MonitorGeometryTests
{
    private static MonitorInfo M(string id, int x, int y, int w, int h, int dpi = 96) => new(id, new PixelRect(x, y, w, h), new PixelRect(x, y, w, h), dpi, dpi, x == 0 && y == 0);

    [Fact]
    public void Monitor_left_and_above_primary_union()
    {
        var ms = new[] { M("p", 0, 0, 1920, 1080), M("l", -1280, 0, 1280, 1024), M("t", 0, -1080, 1920, 1080, 144) };
        Assert.Equal(new PixelRect(-1280, -1080, 3200, 2160), MonitorTopology.VirtualBounds(ms));
        Assert.Equal("l", MonitorTopology.At(ms, new PixelPoint(-1, 10))!.Id);
        Assert.Null(MonitorTopology.At(ms, new PixelPoint(-1, -1))); // gap
    }

    [Fact]
    public void Gap_is_not_covered_and_fingerprint_changes_with_dpi()
    {
        var ms = new[] { M("p", 0, 0, 100, 100), M("s", 100, 50, 100, 100, 144) };
        var r = new PixelRect(50, 0, 100, 100);
        Assert.Equal(5000 + 2500, MonitorTopology.CoveredArea(ms, r));
        var f1 = MonitorTopology.Fingerprint(ms);
        var f2 = MonitorTopology.Fingerprint([ms[0], ms[1] with { DpiX = 96 }]);
        Assert.NotEqual(f1, f2);
        Assert.Equal("p", MonitorTopology.Dominant(ms, r)!.Id);
    }
}

public class SelectionConstraintTests
{
    [Fact]
    public void Aspect_16_9_and_reverse_drag()
    {
        var c = new SelectionConstraint { AspectRatio = 16.0 / 9 };
        Assert.Equal(new PixelRect(0, 0, 160, 90), c.Apply(new PixelPoint(0, 0), new PixelPoint(160, 10)));
        Assert.Equal(new PixelRect(-160, -90, 160, 90), c.Apply(new PixelPoint(0, 0), new PixelPoint(-160, -10)));
    }

    [Fact]
    public void Fixed_size_is_exact()
    {
        var c = new SelectionConstraint { FixedSize = new PixelSize(640, 480) };
        var r = c.Apply(default, new PixelPoint(1000, 1000));
        Assert.Equal(new PixelSize(640, 480), r.Size);
    }

    [Fact]
    public void Unconstrained_reverse_drag_normalizes()
    {
        Assert.Equal(new PixelRect(5, 5, 10, 10), SelectionConstraint.None.Apply(new PixelPoint(15, 15), new PixelPoint(5, 5)));
    }
}
