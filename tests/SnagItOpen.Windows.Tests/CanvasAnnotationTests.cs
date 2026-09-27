using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;

namespace SnagItOpen.Windows.Tests;

/// <summary>Annotations are canvas objects: drawn above all images, never clipped, and grow the auto canvas.</summary>
public class CanvasAnnotationTests
{
    private static readonly Rgba32 Green = Fixtures.Green;

    private static RectangleAnnotation Box(RectD b) =>
        new() { Bounds = b, Color = Green, Fill = Green, StrokeWidth = 0 };

    [Fact]
    public async Task Annotation_is_drawn_above_an_image_added_later()
    {
        using var s = new Sandbox();
        // Red image, annotation on it, then a blue image added and moved underneath the annotation.
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Red100x50());
        doc = DocumentOps.AddAnnotation(doc, Box(new RectD(10, 10, 20, 20)));
        var blue = await s.AddAsync(Fixtures.Blue80x30());
        doc = DocumentOps.AddImages(doc, [(blue, ImageLayer.ForAsset(blue, 0, 0))]);
        Assert.Equal(blue.Id, doc.Images[^1].AssetId); // blue is topmost image

        var px = await s.RenderAsync(doc);
        int ox = -doc.ExportArea.X, oy = -doc.ExportArea.Y;
        Assert.True(Fixtures.Near(px[ox + 20, oy + 20], Green), "annotation hidden under later image");
        Assert.True(Fixtures.Near(px[ox + 60, oy + 20], Fixtures.Blue), "later image should be visible elsewhere");
    }

    [Fact]
    public async Task Annotation_outside_its_image_is_not_clipped_and_grows_auto_canvas()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Red100x50());
        doc = DocumentOps.AddAnnotation(doc, Box(new RectD(90, 40, 30, 30))); // extends to 120×70
        Assert.True(doc.AutoCanvas);
        Assert.True(doc.ExportArea.Right >= 120 && doc.ExportArea.Bottom >= 70, $"canvas {doc.ExportArea} did not grow");

        var px = await s.RenderAsync(doc);
        int ox = -doc.ExportArea.X, oy = -doc.ExportArea.Y;
        Assert.True(Fixtures.Near(px[ox + 115, oy + 65], Green), "part outside the image was clipped");
        Assert.True(Fixtures.Near(px[ox + 95, oy + 45], Green));
    }

    [Fact]
    public async Task Cropping_an_image_leaves_annotations_untouched()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Red100x50());
        var box = Box(new RectD(70, 10, 20, 20));
        doc = DocumentOps.AddAnnotation(doc, box);
        var cropped = DocumentOps.Crop(doc, doc.Images[0].Id, new PixelRect(0, 0, 50, 50));
        Assert.Equal(box.Bounds, cropped.Annotations[0].Bounds);

        var px = await s.RenderAsync(cropped);
        int ox = -cropped.ExportArea.X, oy = -cropped.ExportArea.Y;
        Assert.True(Fixtures.Near(px[ox + 80, oy + 20], Green), "annotation over the cropped-away area disappeared");
    }

    [Fact]
    public async Task Legacy_linked_annotation_renders_at_its_previous_position_without_clipping()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Red100x50());
        var layer = doc.Images[0];
        doc = DocumentOps.SetBounds(doc, layer.Id, new PixelRect(10, 10, 200, 100)); // scale 2
        layer = doc.Images[0];
        // Old format: source-pixel geometry linked to the image, sticking out past its right edge.
        var legacy = Box(new RectD(90, 5, 20, 10)) with { ImageLayerId = layer.Id };
        doc = doc with { Annotations = [legacy] };

        var normalized = AnnotationCanvas.Normalize(doc);
        var a = Assert.Single(normalized.Annotations);
        Assert.Null(a.ImageLayerId);
        Assert.Equal(new RectD(190, 20, 40, 20), a.Bounds); // 10 + 90*2, 10 + 5*2, 20*2, 10*2

        var fitted = DocumentOps.Reflow(normalized);
        var px = await s.RenderAsync(fitted);
        int ox = -fitted.ExportArea.X, oy = -fitted.ExportArea.Y;
        Assert.True(Fixtures.Near(px[ox + 225, oy + 30], Green), "legacy annotation part beyond the image is missing");
    }

    [Fact]
    public async Task Redaction_outside_any_image_stays_fully_opaque()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Red100x50());
        var red = new RedactionAnnotation { Bounds = new RectD(80.4, 30.6, 40.2, 30.1) }; // half outside the image
        doc = DocumentOps.AddAnnotation(doc, red);
        var px = await s.RenderAsync(doc);
        var cover = Imaging.Rendering.DocumentRenderer.RedactionRect(doc, red).Translate(-doc.ExportArea.X, -doc.ExportArea.Y);
        Assert.Equal(new PixelRect(80, 30, 41, 31), cover.Translate(doc.ExportArea.X, doc.ExportArea.Y));
        for (int y = cover.Y; y < cover.Bottom; y++)
            for (int x = cover.X; x < cover.Right; x++)
                Assert.True(Fixtures.Near(px[x, y], Rgba32.Black), $"pixel {x},{y} not covered");
    }
}
