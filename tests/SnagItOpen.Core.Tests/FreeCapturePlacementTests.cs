using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Layout;

namespace SnagItOpen.Core.Tests;

/// <summary>A capture appended to a hand-made Free arrangement must not re-stack or move existing images (user bug 2026-10-02).</summary>
public class FreeCapturePlacementTests
{
    private static ImageAsset Asset(int w, int h) => new(Guid.NewGuid().ToString("N"), w, h, "x.png");

    private static DocumentState Arranged()
    {
        var a1 = Asset(200, 100); var a2 = Asset(150, 150);
        var d = DocumentOps.SetMode(DocumentState.CreateEmpty(), LayoutMode.Free);
        d = DocumentOps.AddImages(d, [(a1, ImageLayer.ForAsset(a1, 50, 300, "a")), (a2, ImageLayer.ForAsset(a2, 400, 40, "b"))]);
        return DocumentOps.SetExportArea(d, new PixelRect(0, 0, 800, 600));
    }

    [Fact]
    public void Appending_keeps_mode_canvas_and_every_existing_position()
    {
        var d = Arranged();
        var before = d.Images.Select(i => i.Bounds).ToArray();
        var asset = Asset(300, 80);
        var placed = DocumentOps.PlaceAppended(d, [asset], right: false);
        var after = DocumentOps.AddImages(d, [(asset, placed[0])]);
        Assert.Equal(LayoutMode.Free, after.Layout.Mode);
        Assert.False(after.AutoCanvas);
        Assert.Equal(new PixelRect(0, 0, 800, 600), after.ExportArea);
        Assert.Equal(before, after.Images.Take(2).Select(i => i.Bounds).ToArray());
        Assert.Equal(new PixelRect(50, 416, 300, 80), placed[0].Bounds); // below all content (bottom 400) + 16, at its left edge
    }

    [Fact]
    public void Placement_follows_the_anchor_and_chains_several_captures()
    {
        var d = Arranged();
        var placed = DocumentOps.PlaceAppended(d, [Asset(100, 50), Asset(100, 50)], right: true, anchor: new PixelRect(400, 40, 150, 150), gap: 10);
        Assert.Equal(new PixelRect(560, 40, 100, 50), placed[0].Bounds);
        Assert.Equal(new PixelRect(670, 40, 100, 50), placed[1].Bounds);
    }
}
