using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public class AnnotationGeometryTests
{
    private static AnnotationHandle H(Annotation a, HandleKind k, ResizeHandle? corner = null) =>
        AnnotationGeometry.Handles(a, 20).First(h => h.Kind == k && (corner is null || h.Corner == corner));

    [Fact]
    public void Arrow_end_handle_moves_only_the_end()
    {
        var a = new ArrowAnnotation { Start = new(0, 0), End = new(100, 0) };
        var r = (LineAnnotation)AnnotationGeometry.Drag(a, H(a, HandleKind.End), new(100, 0), new(50, 80), shift: false);
        Assert.Equal(new PointD(0, 0), r.Start);
        Assert.Equal(new PointD(50, 80), r.End);
    }

    [Fact]
    public void Shift_snaps_line_end_to_15_degrees_keeping_length()
    {
        var a = new LineAnnotation { Start = new(0, 0), End = new(100, 0) };
        var r = (LineAnnotation)AnnotationGeometry.Drag(a, H(a, HandleKind.End), a.End, new(100, 8), shift: true);
        Assert.Equal(0, Rotation2D.AngleOf(r.Start, r.End), 6);
        Assert.Equal(Math.Sqrt(100 * 100 + 64), PointD.Distance(r.Start, r.End), 6);
    }

    [Fact]
    public void Rotating_arrow_turns_both_ends_about_the_midpoint()
    {
        var a = new ArrowAnnotation { Start = new(0, 0), End = new(100, 0) };
        var mid = new PointD(50, 0);
        // Drag the rotate handle a quarter turn clockwise around the midpoint.
        var from = new PointD(50, -20);
        var to = new PointD(70, 0);
        var r = (LineAnnotation)AnnotationGeometry.Drag(a, H(a, HandleKind.Rotate), from, to, false);
        Assert.Equal(50, r.Start.X, 6); Assert.Equal(-50, r.Start.Y, 6);
        Assert.Equal(50, r.End.X, 6); Assert.Equal(50, r.End.Y, 6);
        Assert.Equal(mid.X, r.CurveMid.X, 6);
    }

    [Fact]
    public void Bend_makes_curve_pass_through_the_dragged_point_and_straightens_back()
    {
        var a = new ArrowAnnotation { Start = new(0, 0), End = new(100, 0) };
        var curved = (LineAnnotation)AnnotationGeometry.Drag(a, H(a, HandleKind.Bend), new(50, 0), new(50, 30), false);
        Assert.NotNull(curved.Control);
        Assert.Equal(30, curved.CurveMid.Y, 6);
        Assert.True(AnnotationGeometry.HitTest(curved, new(50, 30), 1));
        Assert.False(AnnotationGeometry.HitTest(curved, new(50, 0), 1));
        var straight = (LineAnnotation)AnnotationGeometry.Drag(curved, H(curved, HandleKind.Bend), curved.CurveMid, new(50, 0.5), false);
        Assert.Null(straight.Control);
    }

    [Fact]
    public void Line_hit_test_follows_the_stroke_not_the_box()
    {
        var a = new LineAnnotation { Start = new(0, 0), End = new(100, 100), StrokeWidth = 2 };
        Assert.True(AnnotationGeometry.HitTest(a, new(50, 51), 2));
        Assert.False(AnnotationGeometry.HitTest(a, new(90, 10), 2));
    }

    [Fact]
    public void Rotate_handle_sets_rotation_with_shift_snap()
    {
        var t = new TextAnnotation { Bounds = new RectD(0, 0, 100, 40) };
        var c = t.Bounds.Center;
        var r = AnnotationGeometry.Drag(t, H(t, HandleKind.Rotate), new(c.X, -20), new(c.X + 100, c.Y + 12), shift: true);
        Assert.Equal(90, r.Rotation, 6);
    }

    [Fact]
    public void Rotated_box_hit_test_uses_the_rotated_shape()
    {
        var t = new RectangleAnnotation { Bounds = new RectD(0, 0, 100, 10), Rotation = 90, StrokeWidth = 0 };
        // Rotated 90° about (50,5): now spans x 45..55, y -45..55.
        Assert.True(AnnotationGeometry.HitTest(t, new(50, 40), 0.5));
        Assert.False(AnnotationGeometry.HitTest(t, new(90, 5), 0.5));
        var ext = t.Extent();
        Assert.True(ext.Y <= -45 && ext.Bottom >= 55);
    }

    [Fact]
    public void Resizing_rotated_box_keeps_opposite_corner_fixed()
    {
        var r = new RectangleAnnotation { Bounds = new RectD(0, 0, 100, 50), Rotation = 30 };
        var opposite = Rotation2D.Corners(r.Bounds, 30)[0]; // top-left stays put when dragging bottom-right
        var br = H(r, HandleKind.Resize, ResizeHandle.BottomRight);
        var moved = br.Position + new PointD(20, 10);
        var res = AnnotationGeometry.Drag(r, br, br.Position, moved, shift: false);
        var tl = Rotation2D.Corners(res.Bounds, res.Rotation)[0];
        Assert.Equal(opposite.X, tl.X, 6);
        Assert.Equal(opposite.Y, tl.Y, 6);
    }

    [Fact]
    public void Step_resize_is_square_unless_shift()
    {
        var s = new StepAnnotation { Bounds = new RectD(0, 0, 30, 30) };
        var br = H(s, HandleKind.Resize, ResizeHandle.BottomRight);
        var sq = AnnotationGeometry.Drag(s, br, br.Position, br.Position + new PointD(30, 5), false);
        Assert.Equal(sq.Bounds.Width, sq.Bounds.Height, 6);
        var free = AnnotationGeometry.Drag(s, br, br.Position, br.Position + new PointD(30, 5), true);
        Assert.NotEqual(free.Bounds.Width, free.Bounds.Height);
    }

    [Fact]
    public void Callout_tail_handle_moves_tail_only()
    {
        var c = new CalloutAnnotation { Bounds = new RectD(0, 0, 100, 40), Tail = new(150, 100) };
        var r = (CalloutAnnotation)AnnotationGeometry.Drag(c, H(c, HandleKind.Tail), c.Tail, new(-40, 90), false);
        Assert.Equal(new PointD(-40, 90), r.Tail);
        Assert.Equal(c.Bounds, r.Bounds);
    }

    [Fact]
    public void Locked_annotations_have_no_handles_and_ignore_drags()
    {
        var a = new ArrowAnnotation { Start = new(0, 0), End = new(10, 0), Locked = true };
        Assert.Empty(AnnotationGeometry.Handles(a, 20));
        Assert.Same(a, AnnotationGeometry.Drag(a, new AnnotationHandle(HandleKind.End, a.End), a.End, new(50, 50), false));
    }

    [Fact]
    public void Align_and_distribute()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var items = new[] { (a, new RectD(0, 0, 10, 10)), (b, new RectD(30, 5, 10, 10)), (c, new RectD(100, 20, 20, 10)) };
        var left = AnnotationGeometry.Align(items, AlignMode.Left);
        Assert.All(left, o => Assert.Equal(0, items.First(i => i.Item1 == o.Id).Item2.X + o.Dx, 6));
        var dist = AnnotationGeometry.Distribute(items, horizontal: true).ToDictionary(o => o.Id);
        // span 0..120, widths 40 → gap 40: b lands at x=50.
        Assert.Equal(20, dist[b].Dx, 6);
        Assert.Equal(0, dist[a].Dx, 6);
        Assert.Equal(0, dist[c].Dx, 6);
    }

    [Theory]
    [InlineData(StepLabelStyle.Numbers, 3, "3")]
    [InlineData(StepLabelStyle.UpperLetters, 1, "A")]
    [InlineData(StepLabelStyle.UpperLetters, 27, "AA")]
    [InlineData(StepLabelStyle.LowerLetters, 2, "b")]
    [InlineData(StepLabelStyle.Roman, 14, "XIV")]
    public void Step_labels(StepLabelStyle style, int n, string expected) =>
        Assert.Equal(expected, StepLabels.Format(style, n, null, "", ""));

    [Fact]
    public void Step_label_prefix_suffix_and_custom()
    {
        Assert.Equal("(4.)", StepLabels.Format(StepLabelStyle.Numbers, 4, null, "(", ".)"));
        Assert.Equal("Go", StepLabels.Format(StepLabelStyle.Custom, 4, "Go", "", ""));
    }

    [Fact]
    public void Shadow_extends_extent()
    {
        var r = new RectangleAnnotation { Bounds = new RectD(0, 0, 10, 10) };
        Assert.True((r with { Shadow = true }).Extent().Right > r.Extent().Right);
    }
}
