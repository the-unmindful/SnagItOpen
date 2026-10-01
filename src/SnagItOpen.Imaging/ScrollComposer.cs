namespace SnagItOpen.Imaging;

/// <summary>Stitches scrolling-capture frames into one image (plan F-SCR1).</summary>
public static class ScrollComposer
{
    /// <summary>
    /// Stacks frames top to bottom. Overlaps are full-frame rows as <c>OverlapMatcher</c> reports them: repeated content
    /// plus the sticky <paramref name="header"/> and <paramref name="footer"/> bands. The header is kept once at the top and
    /// the footer once at the bottom; seams never repeat them and no new content is dropped.
    /// </summary>
    public static PixelBuffer Compose(IReadOnlyList<(PixelBuffer Frame, int Overlap)> frames, int header = 0, int footer = 0)
    {
        if (frames.Count == 0) throw new ArgumentException("No frames to stitch.", nameof(frames));
        int width = frames[0].Frame.Width;
        if (frames.Any(f => f.Frame.Width != width)) throw new ArgumentException("Scrolling frames must all have the same width.", nameof(frames));
        int h0 = frames[0].Frame.Height;
        header = Math.Clamp(header, 0, h0 / 3); footer = Math.Clamp(footer, 0, h0 / 3);
        var bands = new List<(PixelBuffer Frame, int From, int To)>();
        for (int i = 0; i < frames.Count; i++)
        {
            var (frame, overlap) = frames[i];
            int contentEnd = frame.Height - footer;
            // Skip the header and the repeated content (= overlap - header - footer) of every frame after the first.
            int from = i == 0 ? 0 : Math.Clamp(overlap - footer, header, contentEnd - 1);
            int to = i == frames.Count - 1 ? frame.Height : contentEnd;
            bands.Add((frame, from, to));
        }
        int height = checked(bands.Sum(b => b.To - b.From));
        var output = new PixelBuffer(width, height);
        int y = 0;
        foreach (var (frame, from, to) in bands)
        {
            Array.Copy(frame.Data, from * width, output.Data, y * width, (to - from) * width);
            y += to - from;
        }
        return output;
    }
}
