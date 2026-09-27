using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public class SelectionStackTests
{
    private static readonly RectD Box = new(0, 0, 100, 100);

    [Fact]
    public void Unfilled_rectangle_is_see_through_inside_but_hit_on_border()
    {
        var r = new RectangleAnnotation { Bounds = Box, StrokeWidth = 2 };
        Assert.False(AnnotationGeometry.HitTest(r, new PointD(50, 50), 3));
        Assert.True(AnnotationGeometry.HitTest(r, new PointD(1, 50), 3));
        Assert.True(AnnotationGeometry.HitTest(r, new PointD(50, 99), 3));
    }

    [Fact]
    public void Filled_rectangle_is_hit_everywhere_inside()
    {
        var r = new RectangleAnnotation { Bounds = Box, Fill = Rgba32.White };
        Assert.True(AnnotationGeometry.HitTest(r, new PointD(50, 50), 3));
    }

    [Fact]
    public void Unfilled_ellipse_is_see_through_inside()
    {
        var e = new EllipseAnnotation { Bounds = Box, StrokeWidth = 2 };
        Assert.False(AnnotationGeometry.HitTest(e, new PointD(50, 50), 3));
        Assert.True(AnnotationGeometry.HitTest(e, new PointD(50, 1), 3));
        Assert.False(AnnotationGeometry.HitTest(e, new PointD(2, 2), 3)); // outside the ellipse corner
    }

    [Fact]
    public void Locked_unfilled_shape_stays_selectable_inside()
    {
        var r = new RectangleAnnotation { Bounds = Box, Locked = true };
        Assert.True(AnnotationGeometry.HitTest(r, new PointD(50, 50), 3));
    }

    [Fact]
    public void Click_inside_empty_box_reaches_item_beneath()
    {
        var under = new RectangleAnnotation { Bounds = new RectD(40, 40, 20, 20), Fill = Rgba32.Red };
        var over = new RectangleAnnotation { Bounds = Box };
        var stack = AnnotationGeometry.HitStack([under, over], new PointD(50, 50), 3);
        Assert.Equal(under.Id, Assert.Single(stack).Id);
    }

    [Fact]
    public void Hit_stack_is_topmost_first_and_skips_hidden()
    {
        var a = new RectangleAnnotation { Bounds = Box, Fill = Rgba32.Red };
        var b = new RectangleAnnotation { Bounds = Box, Fill = Rgba32.White };
        var c = new RectangleAnnotation { Bounds = Box, Fill = Rgba32.Black, Hidden = true };
        var stack = AnnotationGeometry.HitStack([a, b, c], new PointD(50, 50), 3);
        Assert.Equal([b.Id, a.Id], stack.Select(s => s.Id));
    }

    [Fact]
    public void Inside_box_covers_see_through_interior_for_dragging()
    {
        var r = new RectangleAnnotation { Bounds = Box };
        Assert.True(AnnotationGeometry.InsideBox(r, new PointD(50, 50), 3));
    }

    private static (DocumentState Doc, Guid[] Ids) FourStacked()
    {
        var anns = Enumerable.Range(0, 4).Select(_ => (Annotation)new RectangleAnnotation { Bounds = Box }).ToArray();
        return (DocumentState.CreateEmpty() with { Annotations = anns }, anns.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void Forward_moves_one_step_not_to_top()
    {
        var (d, ids) = FourStacked();
        var r = DocumentOps.ChangeAnnotationZOrder(d, [ids[0]], DocumentOps.ZMove.Forward);
        Assert.Equal([ids[1], ids[0], ids[2], ids[3]], r.Annotations.Select(a => a.Id));
    }

    [Fact]
    public void Backward_moves_one_step_not_to_bottom()
    {
        var (d, ids) = FourStacked();
        var r = DocumentOps.ChangeAnnotationZOrder(d, [ids[3]], DocumentOps.ZMove.Backward);
        Assert.Equal([ids[0], ids[1], ids[3], ids[2]], r.Annotations.Select(a => a.Id));
    }

    [Fact]
    public void Front_and_back_jump_and_keep_group_order()
    {
        var (d, ids) = FourStacked();
        var f = DocumentOps.ChangeAnnotationZOrder(d, [ids[0], ids[1]], DocumentOps.ZMove.ToFront);
        Assert.Equal([ids[2], ids[3], ids[0], ids[1]], f.Annotations.Select(a => a.Id));
        var b = DocumentOps.ChangeAnnotationZOrder(d, [ids[2], ids[3]], DocumentOps.ZMove.ToBack);
        Assert.Equal([ids[2], ids[3], ids[0], ids[1]], b.Annotations.Select(a => a.Id));
    }

    [Fact]
    public void Moving_topmost_forward_is_a_no_op()
    {
        var (d, ids) = FourStacked();
        Assert.Same(d, DocumentOps.ChangeAnnotationZOrder(d, [ids[3]], DocumentOps.ZMove.Forward));
    }
}
