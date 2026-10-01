using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;

namespace SnagItOpen.App.Capture;

/// <summary>Bounded joined preview: previous frame tail followed by the first retained next rows.</summary>
public static class ScrollingSeamPreview
{
    /// <param name="overlap">Full-frame rows, as the matcher reports them (repeated content + header + footer).</param>
    /// <param name="header">Sticky header rows; with <paramref name="footer"/> the seam matches <c>ScrollComposer.Compose</c> exactly.</param>
    public static PixelBuffer Build(PixelBuffer previous, PixelBuffer pending, int overlap, int rowsPerFrame = 120, int header = 0, int footer = 0)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(pending);
        if (overlap < 0 || overlap >= pending.Height) throw new ArgumentOutOfRangeException(nameof(overlap));
        if (rowsPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(rowsPerFrame));
        header = Math.Clamp(header, 0, previous.Height / 3); footer = Math.Clamp(footer, 0, previous.Height / 3);
        int prevEnd = previous.Height - footer;                                       // final output drops the previous footer
        int from = Math.Clamp(overlap - footer, header, pending.Height - footer - 1);  // ...and the next header + repeated rows
        int tailRows = Math.Min(rowsPerFrame, prevEnd);
        int headRows = Math.Min(rowsPerFrame, pending.Height - from);
        var preview = new PixelBuffer(Math.Max(previous.Width, pending.Width), tailRows + headRows);
        preview.Blit(previous.Crop(new PixelRect(0, prevEnd - tailRows, previous.Width, tailRows)), 0, 0);
        preview.Blit(pending.Crop(new PixelRect(0, from, pending.Width, headRows)), 0, tailRows);
        return preview;
    }
}
