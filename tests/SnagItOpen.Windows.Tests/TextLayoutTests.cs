using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;

namespace SnagItOpen.Windows.Tests;

/// <summary>Text box sizing and vertical alignment (plan F-TXT).</summary>
public class TextLayoutTests
{
    private static readonly Rgba32 Blue = new(0, 0, 200, 255);

    [Fact]
    public void Files_without_the_new_fields_keep_their_old_layout()
    {
        var legacy = new TextAnnotation { Padding = 9 };
        Assert.Equal(TextSizing.Fixed, legacy.Sizing);
        Assert.Equal(TextVAlign.Top, legacy.VerticalAlign);
        Assert.Equal(9, legacy.PadX); Assert.Equal(9, legacy.PadY);
    }

    [Fact]
    public void Auto_width_box_grows_with_font_size_and_auto_height_follows_wrapping()
    {
        ThemeTokenTests.RunSta<bool>(() =>
        {
            var t = new TextAnnotation { Text = "Hello world", FontSize = 20, Sizing = TextSizing.AutoWidth, VerticalAlign = TextVAlign.Middle, Bounds = new RectD(10, 10, 5, 5) };
            var small = AnnotationRenderer.Fit(t); var big = AnnotationRenderer.Fit(t with { FontSize = 40 });
            Assert.True(big.Bounds.Width > small.Bounds.Width * 1.8 && big.Bounds.Height > small.Bounds.Height * 1.5);
            Assert.Equal(10, big.Bounds.X); Assert.Equal(10, big.Bounds.Y);
            var narrow = AnnotationRenderer.Fit(t with { Sizing = TextSizing.AutoHeight, Bounds = new RectD(10, 10, 60, 5) });
            Assert.Equal(60, narrow.Bounds.Width);
            Assert.True(narrow.Bounds.Height > small.Bounds.Height * 1.5, "wrapped text needs two lines");
            return true;
        });
    }

    [Fact]
    public async Task Middle_aligned_label_has_equal_space_above_and_below_its_capitals()
    {
        using var s = new Sandbox();
        var label = AnnotationRenderer.Fit(new TextAnnotation
        {
            Text = "HEH", FontSize = 40, Color = Rgba32.White, Fill = Blue, StrokeWidth = 0, CornerRadius = 0,
            PaddingX = 12, PaddingY = 8, Sizing = TextSizing.AutoWidth, VerticalAlign = TextVAlign.Middle, Bounds = new RectD(20, 20, 10, 10),
        });
        var d = DocumentState.CreateEmpty() with { Background = Rgba32.White, Layout = LayoutOptions.Default with { Mode = LayoutMode.Free } };
        d = DocumentOps.AddAnnotation(DocumentOps.SetExportArea(d, new PixelRect(0, 0, 200, 120)), label);
        var px = await s.RenderAsync(d);
        int top = (int)label.Bounds.Y, bottom = (int)Math.Ceiling(label.Bounds.Bottom) - 1, x0 = (int)label.Bounds.X + 2, x1 = (int)label.Bounds.Right - 2;
        bool Ink(int y) { for (int x = x0; x < x1; x++) if (Fixtures.Near(px[x, y], Rgba32.White, 60)) return true; return false; }
        int first = Enumerable.Range(top + 1, bottom - top - 1).First(Ink), last = Enumerable.Range(top + 1, bottom - top - 1).Last(Ink);
        int above = first - top, below = bottom - last;
        Assert.True(Math.Abs(above - below) <= 3, $"space above {above}px, below {below}px");
    }
}
