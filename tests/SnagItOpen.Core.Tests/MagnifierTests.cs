using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

/// <summary>Plan F-MAG: the lens and what it shows move together; the source is edited separately.</summary>
public class MagnifierTests
{
    private static readonly MagnifierAnnotation Lens = new() { Bounds = new RectD(200, 200, 100, 100), SourceRegion = new RectD(10, 10, 50, 50) };

    [Fact]
    public void Moving_the_lens_keeps_its_content()
    {
        var doc = DocumentState.CreateEmpty() with { Annotations = [Lens] };
        var moved = (MagnifierAnnotation)DocumentOps.Move(doc, [], [Lens.Id], 30, -20).Annotations[0];
        Assert.Equal(new RectD(230, 180, 100, 100), moved.Bounds);
        Assert.Equal(Lens.SourceRegion, moved.SourceRegion);
        Assert.Equal(2, moved.Zoom, 3);
    }

    [Fact]
    public void Document_wide_transforms_still_map_the_source()
    {
        var scaled = (MagnifierAnnotation)Lens.MapGeometry(p => new PointD(p.X * 2, p.Y * 2));
        Assert.Equal(new RectD(20, 20, 100, 100), scaled.SourceRegion);
        Assert.Equal(new RectD(10, 10, 50, 50), ((MagnifierAnnotation)Lens.Offset(0, 0)).SourceRegion);
    }

    [Fact]
    public void Changing_the_source_leaves_the_lens_in_place()
    {
        var edited = Lens.WithSource(new RectD(0, 0, 25, 25));
        Assert.Equal(Lens.Bounds, edited.Bounds);
        Assert.Equal(4, edited.Zoom, 3);
    }
}
