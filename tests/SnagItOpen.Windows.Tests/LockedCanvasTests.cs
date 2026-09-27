using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;

namespace SnagItOpen.Windows.Tests;

public class LockedCanvasTests
{
    private static readonly Rgba32 Blue = new(0, 0, 255, 255);

    [Fact]
    public void Locked_canvas_detects_content_outside_and_auto_canvas_does_not()
    {
        var doc = DocumentState.CreateEmpty();
        doc = DocumentOps.AddAnnotation(doc, new RectangleAnnotation { Bounds = new RectD(0, 0, 40, 40), Fill = Blue, StrokeWidth = 0 });
        Assert.False(DocumentBounds.HasContentOutside(doc)); // auto canvas grew to fit

        var locked = DocumentOps.SetExportArea(doc, new PixelRect(0, 0, 20, 20));
        Assert.False(locked.AutoCanvas);
        Assert.True(DocumentBounds.HasContentOutside(locked));

        // Adding more content does not grow a locked canvas.
        var more = DocumentOps.AddAnnotation(locked, new RectangleAnnotation { Bounds = new RectD(100, 100, 10, 10), Fill = Blue, StrokeWidth = 0 });
        Assert.Equal(new PixelRect(0, 0, 20, 20), more.ExportArea);
    }

    [Fact]
    public async Task Export_of_locked_canvas_contains_only_pixels_inside_it()
    {
        using var s = new Sandbox();
        var doc = DocumentState.CreateEmpty() with { Background = Rgba32.White };
        doc = DocumentOps.AddAnnotation(doc, new RectangleAnnotation { Bounds = new RectD(0, 0, 100, 50), Fill = Blue, StrokeWidth = 0 });
        doc = DocumentOps.SetExportArea(doc, new PixelRect(10, 10, 30, 20));
        var px = await s.RenderAsync(doc);
        Assert.Equal(30, px.Width);
        Assert.Equal(20, px.Height);
        Assert.True(Fixtures.Near(px[15, 10], Blue));
    }

    [Fact]
    public void Fit_canvas_unlocks_and_covers_everything()
    {
        var doc = DocumentState.CreateEmpty();
        doc = DocumentOps.AddAnnotation(doc, new RectangleAnnotation { Bounds = new RectD(0, 0, 40, 40), Fill = Blue, StrokeWidth = 0 });
        var locked = DocumentOps.SetExportArea(doc, new PixelRect(0, 0, 10, 10));
        var fit = DocumentOps.FitCanvas(locked);
        Assert.True(fit.AutoCanvas);
        Assert.False(DocumentBounds.HasContentOutside(fit));
    }
}
