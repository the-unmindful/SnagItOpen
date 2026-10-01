using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;

namespace SnagItOpen.App.Capture;

/// <summary>Bounded joined preview: previous frame tail followed by the first retained next rows.</summary>
public static class ScrollingSeamPreview
{
    public static PixelBuffer Build(PixelBuffer previous, PixelBuffer pending, int overlap, int rowsPerFrame = 120)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(pending);
        if (overlap < 0 || overlap >= pending.Height) throw new ArgumentOutOfRangeException(nameof(overlap));
        if (rowsPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(rowsPerFrame));
        int tailRows = Math.Min(rowsPerFrame, previous.Height);
        int headRows = Math.Min(rowsPerFrame, pending.Height - overlap);
        var preview = new PixelBuffer(Math.Max(previous.Width, pending.Width), tailRows + headRows);
        preview.Blit(previous.Crop(new PixelRect(0, previous.Height - tailRows, previous.Width, tailRows)), 0, 0);
        preview.Blit(pending.Crop(new PixelRect(0, overlap, pending.Width, headRows)), 0, tailRows);
        return preview;
    }
}
