namespace SnagItOpen.Imaging;

/// <summary>Stitches scrolling-capture frames into one image (plan F-SCR1).</summary>
public static class ScrollComposer
{
    /// <summary>
    /// Stacks frames top to bottom. Overlaps are in content rows, i.e. excluding the sticky <paramref name="header"/> and
    /// <paramref name="footer"/> bands (as <c>OverlapMatcher</c> measures them). The header is kept once at the top and
    /// the footer once at the bottom; seams never repeat them. All frames must have the same width and height.
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
            int from = i == 0 ? 0 : Math.Clamp(header + overlap, header, contentEnd - 1);
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

    /// <summary>
    /// Rows at the top and bottom that are identical in two consecutive frames (a sticky header or footer that does not
    /// scroll). Capped at a third of the height each; (0, 0) when the frames are identical (nothing scrolled).
    /// </summary>
    public static (int Header, int Footer) StickyRows(PixelBuffer a, PixelBuffer b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return (0, 0);
        int w = a.Width, h = a.Height, cap = h / 3;
        bool Same(int y) => a.Data.AsSpan(y * w, w).SequenceEqual(b.Data.AsSpan(y * w, w));
        int top = 0; while (top < cap && Same(top)) top++;
        int bottom = 0; while (bottom < cap && Same(h - 1 - bottom)) bottom++;
        if (top == cap && bottom == cap && Enumerable.Range(cap, h - 2 * cap).All(Same)) return (0, 0);
        // Blank margins are identical in both frames too but are not a header: require real content in the band.
        bool Varied(int y) { var row = a.Data.AsSpan(y * w, w); return row.IndexOfAnyExcept(row[0]) >= 0; }
        if (!Enumerable.Range(0, top).Any(Varied)) top = 0;
        if (!Enumerable.Range(h - bottom, bottom).Any(Varied)) bottom = 0;
        return (top, bottom);
    }
}