using SnagItOpen.App.Capture;
using SnagItOpen.Imaging;

namespace SnagItOpen.Windows.Tests;

public sealed class ScrollingSeamPreviewTests
{
    [Fact]
    public void Before_and_after_preview_show_the_actual_join_and_remove_repeated_rows()
    {
        var previous = new PixelBuffer(1, 4, [1, 2, 3, 4]);
        var pending = new PixelBuffer(1, 4, [3, 4, 5, 6]);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 3, 4, 5, 6 }, ScrollingSeamPreview.Build(previous, pending, 0).Data);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, ScrollingSeamPreview.Build(previous, pending, 2).Data);
        Assert.Equal(new uint[] { 3, 4, 5, 6 }, ScrollingSeamPreview.Build(previous, pending, 2, rowsPerFrame: 2).Data);
    }

    [Fact]
    public void Sticky_footer_preview_shows_the_same_pixels_as_final_composition()
    {
        var previous = new PixelBuffer(1, 10, [100, 101, 2, 3, 4, 5, 6, 7, 200, 201]);
        var pending = new PixelBuffer(1, 10, [100, 101, 5, 6, 7, 8, 9, 10, 200, 201]);
        // Full overlap: three repeated content rows plus the two-row header and footer.
        var composed = ScrollComposer.Compose([(previous, 0), (pending, 7)], header: 2, footer: 2);
        var preview = ScrollingSeamPreview.Build(previous, pending, 7, header: 2, footer: 2);
        Assert.Equal(composed.Data, preview.Data);
    }

    [Fact]
    public void Preview_keeps_at_least_one_next_row_and_does_not_mutate_source_frames()
    {
        var previous = new PixelBuffer(1, 2, [1, 2]);
        var pending = new PixelBuffer(1, 2, [2, 3]);
        Assert.Equal(new uint[] { 1, 2, 3 }, ScrollingSeamPreview.Build(previous, pending, 1).Data);
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollingSeamPreview.Build(previous, pending, 2));
        Assert.Equal(new uint[] { 1, 2 }, previous.Data); Assert.Equal(new uint[] { 2, 3 }, pending.Data);
    }
}
