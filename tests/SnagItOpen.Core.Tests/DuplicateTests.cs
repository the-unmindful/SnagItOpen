using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public class DuplicateTests
{
    [Fact]
    public void Drag_duplicate_places_image_copies_at_the_drag_offset_and_keeps_originals()
    {
        var d = TestData.FreeDoc(new PixelRect(10, 10, 20, 20));
        var id = d.Images[0].Id;
        var (nd, ids) = DocumentOps.Duplicate(d, [id], 37, -5);
        Assert.Equal(new PixelRect(10, 10, 20, 20), nd.FindImage(id)!.Bounds);
        Assert.Equal(new PixelRect(47, 5, 20, 20), nd.FindImage(ids[0])!.Bounds);
    }

    [Fact]
    public void Drag_duplicate_in_layout_mode_switches_to_free_so_copies_stay_where_dropped()
    {
        var d = TestData.Doc(TestData.Vertical(0, 0), (10, 10));
        var (nd, _) = DocumentOps.Duplicate(d, [d.Images[0].Id], 50, 0);
        Assert.Equal(LayoutMode.Free, nd.Layout.Mode);
    }

    [Fact]
    public void Duplicated_annotations_are_unlocked_visible_and_offset()
    {
        var a = new RectangleAnnotation { Bounds = new RectD(0, 0, 10, 10), Locked = true, Hidden = true };
        var d = DocumentOps.AddAnnotation(TestData.FreeDoc(new PixelRect(0, 0, 50, 50)), a);
        var (nd, ids) = DocumentOps.DuplicateAnnotations(d, [a.Id], 20, 30);
        var c = nd.FindAnnotation(ids[0])!;
        Assert.False(c.Locked);
        Assert.False(c.Hidden);
        Assert.Equal(new RectD(20, 30, 10, 10), c.Bounds);
        Assert.True(nd.FindAnnotation(a.Id)!.Locked);
    }

    [Fact]
    public void Pasted_style_keeps_geometry_and_text_of_the_target()
    {
        var src = new TextAnnotation { Bounds = new RectD(0, 0, 50, 20), Text = "src", FontSize = 40, Bold = true, Color = Rgba32.Black };
        var dst = new TextAnnotation { Bounds = new RectD(100, 100, 80, 30), Text = "keep me", FontSize = 12 };
        var r = (TextAnnotation)AnnotationStyle.Transfer(src, dst);
        Assert.Equal("keep me", r.Text);
        Assert.Equal(dst.Bounds, r.Bounds);
        Assert.Equal(dst.Id, r.Id);
        Assert.Equal(40, r.FontSize);
        Assert.True(r.Bold);
        Assert.Equal(Rgba32.Black, r.Color);
    }
}
