namespace SnagItOpen.Core.Stitching;

/// <summary>Grayscale luminance image, values 0..1, row-major.</summary>
public sealed class LumaImage
{
    public LumaImage(int width, int height, float[] data)
    {
        if (width <= 0 || height <= 0 || data.Length != (long)width * height) throw new ArgumentException("Bad luma dimensions.");
        Width = width; Height = height; Data = data;
    }
    public int Width { get; }
    public int Height { get; }
    public float[] Data { get; }
    public float this[int x, int y] => Data[y * Width + x];
}

public enum OverlapConfidence { Confident, Ambiguous, LowTexture, NoMatch }

/// <summary>
/// Suggested vertical overlap (rows of <c>next</c> repeating the bottom of <c>prev</c>).
/// Error is mean absolute luminance difference; Margin is the gap to the best distinct peak.
/// These are heuristics, not probabilities.
/// </summary>
public sealed record OverlapSuggestion(int Overlap, double Error, double Margin, double Texture, OverlapConfidence Confidence)
{
    public bool IsConfident => Confidence == OverlapConfidence.Confident;
}

public sealed record OverlapOptions
{
    public int MinOverlap { get; init; } = 8;
    /// <summary>Maximum overlap as a fraction of frame height.</summary>
    public double MaxOverlapFraction { get; init; } = 0.95;
    /// <summary>Rows excluded at top of each frame (sticky header).</summary>
    public int HeaderRows { get; init; }
    /// <summary>Rows excluded at bottom of each frame (sticky footer).</summary>
    public int FooterRows { get; init; }
    public double MaxError { get; init; } = 0.03;
    public double MinMargin { get; init; } = 0.01;
    public double MinTexture { get; init; } = 0.02;
    public int DistinctPeakRows { get; init; } = 3;
    public int Strips { get; init; } = 5;
}

/// <summary>
/// Conservative vertical overlap finder. Compares prev's bottom rows with next's top rows across
/// several central column strips; coarse ranking on downsampled rows, then full-resolution refinement.
/// </summary>
public static class OverlapMatcher
{
    public static OverlapSuggestion FindVertical(LumaImage prev, LumaImage next, OverlapOptions? options = null)
    {
        var o = options ?? new OverlapOptions();
        if (prev.Width != next.Width) return new(0, 1, 0, 0, OverlapConfidence.NoMatch);
        int w = prev.Width;
        int header = Math.Max(0, o.HeaderRows), footer = Math.Max(0, o.FooterRows);
        // Usable content rows: [header, H - footer)
        int prevRows = prev.Height - header - footer, nextRows = next.Height - header - footer;
        if (prevRows <= o.MinOverlap || nextRows <= o.MinOverlap) return new(0, 1, 0, 0, OverlapConfidence.NoMatch);

        // Column sample set: several central strips.
        var cols = SampleColumns(w, o.Strips);
        var prevRow = RowSignatures(prev, cols, header, prevRows);
        var nextRow = RowSignatures(next, cols, header, nextRows);

        double texture = StdDev(nextRow);
        int maxOverlap = Math.Min((int)(Math.Min(prevRows, nextRows) * o.MaxOverlapFraction), Math.Min(prevRows, nextRows) - 1);
        if (maxOverlap < o.MinOverlap) return new(0, 1, 0, texture, OverlapConfidence.NoMatch);

        // Evaluate every candidate overlap k: prev content rows [prevRows-k, prevRows) vs next [0,k).
        var errors = new double[maxOverlap + 1];
        for (int k = 0; k <= maxOverlap; k++) errors[k] = double.MaxValue;
        // Coarse pass using row signatures (cheap), then refine top candidates at full resolution.
        var coarse = new List<(int K, double E)>();
        for (int k = o.MinOverlap; k <= maxOverlap; k++)
        {
            double sum = 0;
            int step = Math.Max(1, k / 64);
            int n = 0;
            for (int r = 0; r < k; r += step) { sum += Math.Abs(prevRow[prevRows - k + r] - nextRow[r]); n++; }
            coarse.Add((k, sum / n));
        }
        coarse.Sort((a, b) => a.E.CompareTo(b.E));
        foreach (var (k, _) in coarse.Take(12))
            errors[k] = FullError(prev, next, cols, header, prevRows, k);

        int best = -1; double bestErr = double.MaxValue;
        for (int k = o.MinOverlap; k <= maxOverlap; k++)
            if (errors[k] < bestErr || (errors[k] == bestErr && k > best)) { bestErr = errors[k]; best = k; }
        if (best < 0) return new(0, 1, 0, texture, OverlapConfidence.NoMatch);

        double second = double.MaxValue;
        for (int k = o.MinOverlap; k <= maxOverlap; k++)
            if (Math.Abs(k - best) > o.DistinctPeakRows && errors[k] < second) second = errors[k];
        // If only one refined candidate exists outside the window, use coarse ranking to bound margin.
        if (second == double.MaxValue)
            foreach (var (k, e) in coarse) if (Math.Abs(k - best) > o.DistinctPeakRows) { second = e; break; }
        double margin = second == double.MaxValue ? 1 : second - bestErr;

        OverlapConfidence conf;
        if (texture < o.MinTexture) conf = OverlapConfidence.LowTexture;
        else if (bestErr > o.MaxError) conf = OverlapConfidence.NoMatch;
        else if (margin < o.MinMargin) conf = OverlapConfidence.Ambiguous;
        else conf = OverlapConfidence.Confident;

        // Report overlap in full-frame rows: content overlap plus the excluded footer of prev and header of next.
        int overlapRows = best + footer + header;
        return new OverlapSuggestion(overlapRows, bestErr, margin, texture, conf);
    }

    /// <summary>True when two frames are (nearly) identical: probable end of scrolling.</summary>
    /// <summary>
    /// Rows at the top and bottom identical in two consecutive frames (a sticky header/footer that does not scroll), each
    /// capped at a third of the height. Bands without any detail (blank margins) do not count; (0, 0) if nothing scrolled.
    /// </summary>
    public static (int Header, int Footer) StickyRows(LumaImage a, LumaImage b, float tolerance = 0.004f)
    {
        if (a.Width != b.Width || a.Height != b.Height) return (0, 0);
        int w = a.Width, h = a.Height, cap = h / 3;
        bool Same(int y) { for (int x = 0; x < w; x++) if (Math.Abs(a[x, y] - b[x, y]) > tolerance) return false; return true; }
        bool Varied(int y) { float first = a[0, y]; for (int x = 1; x < w; x++) if (Math.Abs(a[x, y] - first) > 0.02f) return true; return false; }
        int top = 0; while (top < cap && Same(top)) top++;
        int bottom = 0; while (bottom < cap && Same(h - 1 - bottom)) bottom++;
        if (top == cap && bottom == cap) return (0, 0); // probably did not scroll at all
        if (!Enumerable.Range(0, top).Any(Varied)) top = 0;
        if (!Enumerable.Range(h - bottom, bottom).Any(Varied)) bottom = 0;
        return (top, bottom);
    }

    public static bool AreSame(LumaImage a, LumaImage b, double tolerance = 0.004)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        double sum = 0;
        int step = Math.Max(1, a.Data.Length / 200_000);
        int n = 0;
        for (int i = 0; i < a.Data.Length; i += step) { sum += Math.Abs(a.Data[i] - b.Data[i]); n++; }
        return sum / n <= tolerance;
    }

    private static int[] SampleColumns(int width, int strips)
    {
        // Central 80% of width, split into strips, 8 columns each.
        var list = new List<int>();
        int l = width / 10, r = width - width / 10;
        if (r - l < 4) { l = 0; r = width; }
        strips = Math.Max(1, strips);
        for (int s = 0; s < strips; s++)
        {
            int c0 = l + (r - l) * s / strips, c1 = l + (r - l) * (s + 1) / strips;
            int step = Math.Max(1, (c1 - c0) / 8);
            for (int c = c0; c < c1; c += step) list.Add(c);
        }
        return list.Distinct().ToArray();
    }

    private static float[] RowSignatures(LumaImage img, int[] cols, int start, int rows)
    {
        var sig = new float[rows];
        for (int r = 0; r < rows; r++)
        {
            double s = 0;
            int y = start + r;
            foreach (var c in cols) s += img[c, y];
            // Mix in horizontal gradient so equal-mean rows with different content differ.
            double g = 0;
            for (int i = 1; i < cols.Length; i++) g += Math.Abs(img[cols[i], y] - img[cols[i - 1], y]);
            sig[r] = (float)(s / cols.Length * 0.5 + g / Math.Max(1, cols.Length - 1) * 0.5);
        }
        return sig;
    }

    private static double FullError(LumaImage prev, LumaImage next, int[] cols, int header, int prevRows, int k)
    {
        double sum = 0; long n = 0;
        for (int r = 0; r < k; r++)
        {
            int py = header + prevRows - k + r, ny = header + r;
            foreach (var c in cols) { sum += Math.Abs(prev[c, py] - next[c, ny]); n++; }
        }
        return n == 0 ? 1 : sum / n;
    }

    private static double StdDev(float[] v)
    {
        if (v.Length == 0) return 0;
        double m = v.Average(x => (double)x);
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Length);
    }
}
