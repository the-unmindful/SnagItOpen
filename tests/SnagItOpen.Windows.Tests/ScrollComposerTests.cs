using SnagItOpen.Imaging;

namespace SnagItOpen.Windows.Tests;

/// <summary>Plan F-SCR: scrolling frames become one seamless image; sticky headers/footers are detected.</summary>
public class ScrollComposerTests
{
    /// <summary>A tall "page" whose row y has the colour y, and a viewport frame of it starting at <paramref name="top"/>.</summary>
    private static PixelBuffer Frame(int top, int height = 50, int width = 8, int header = 0, int footer = 0)
    {
        var f = new PixelBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            // Sticky bands show fixed varied content; the middle shows the page at its scroll position.
            uint row = y < header ? 0xFF000000u | (uint)(0xA00000 + y) : y >= height - footer ? 0xFF000000u | (uint)(0xB00000 + y) : 0xFF000000u | (uint)(top + y);
            for (int x = 0; x < width; x++) f[x, y] = x == 0 && (y < header || y >= height - footer) ? row ^ 0x00FF00u : row;
        }
        return f;
    }

    [Fact]
    public void Frames_are_stacked_without_their_overlap()
    {
        var a = Frame(0); var b = Frame(30); var c = Frame(60); // each new frame overlaps the previous by 20 rows
        var output = ScrollComposer.Compose([(a, 0), (b, 20), (c, 20)]);
        Assert.Equal(110, output.Height);
        for (int y = 0; y < output.Height; y++) Assert.Equal(0xFF000000u | (uint)y, output[3, y]);
    }

    [Fact]
    public void Sticky_header_and_footer_appear_once_and_never_at_a_seam()
    {
        var a = Frame(0, header: 6, footer: 4); var b = Frame(20, header: 6, footer: 4);
        var output = ScrollComposer.Compose([(a, 0), (b, 30)], header: 6, footer: 4); // matcher overlap = 20 content + 6 + 4
        Assert.Equal(70, output.Height);
        for (int y = 0; y < 6; y++) Assert.Equal(a[3, y], output[3, y]);              // header once, at the top
        for (int y = 6; y < 66; y++) Assert.Equal(0xFF000000u | (uint)y, output[3, y]); // continuous page, no repeated bands
        for (int y = 66; y < 70; y++) Assert.Equal(b[3, y - 20], output[3, y]);       // footer once, at the bottom
    }

    [Fact]
    public void Overlap_is_clamped_and_widths_must_match()
    {
        var a = Frame(0); var b = Frame(49);
        Assert.Equal(51, ScrollComposer.Compose([(a, 99), (b, 500)]).Height); // first overlap ignored, second clamped to height-1
        Assert.Throws<ArgumentException>(() => ScrollComposer.Compose([(a, 0), (Frame(10, width: 9), 5)]));
        Assert.Throws<ArgumentException>(() => ScrollComposer.Compose([]));
    }
}
