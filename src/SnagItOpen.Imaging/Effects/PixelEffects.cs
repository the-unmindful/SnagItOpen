using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Stitching;

namespace SnagItOpen.Imaging.Effects;

/// <summary>
/// Blur and pixelate processors operating on straight-alpha pixels in source coordinates.
/// Averaging is done in premultiplied space so transparent pixels do not darken neighbours.
/// Writes are clipped to the effect region; blur reads a sampling margin around it.
/// These are visual effects, not secure redaction.
/// </summary>
public static class ObscureProcessor
{
    public static PixelBuffer Apply(PixelBuffer src, IReadOnlyList<ImageEffect> effects)
    {
        if (effects.Count == 0) return src;
        var o = src.Clone();
        foreach (var e in effects)
        {
            var r = e.Region.Intersect(o.FullRect);
            if (r.IsEmpty) continue;
            if (e.Kind == ImageEffectKind.Pixelate) Pixelate(o, r, e.Strength);
            else Blur(o, r, e.Strength);
        }
        return o;
    }

    /// <summary>Replaces each block (anchored at region origin) with its premultiplied average.</summary>
    public static void Pixelate(PixelBuffer b, PixelRect region, int block)
    {
        block = Math.Clamp(block, ImageEffect.MinBlock, ImageEffect.MaxBlock);
        var r = region.Intersect(b.FullRect);
        for (int by = r.Y; by < r.Bottom; by += block)
            for (int bx = r.X; bx < r.Right; bx += block)
            {
                int w = Math.Min(block, r.Right - bx), h = Math.Min(block, r.Bottom - by);
                long sa = 0, sr = 0, sg = 0, sb = 0;
                for (int y = by; y < by + h; y++)
                    for (int x = bx; x < bx + w; x++)
                    {
                        uint p = b.Data[y * b.Width + x];
                        long a = p >> 24;
                        sa += a; sr += ((p >> 16) & 0xFF) * a; sg += ((p >> 8) & 0xFF) * a; sb += (p & 0xFF) * a;
                    }
                long n = (long)w * h;
                uint v;
                if (sa == 0) v = 0;
                else
                {
                    byte A = (byte)((sa + n / 2) / n);
                    v = PixelBuffer.Pack((byte)((sr + sa / 2) / sa), (byte)((sg + sa / 2) / sa), (byte)((sb + sa / 2) / sa), A);
                }
                for (int y = by; y < by + h; y++)
                    Array.Fill(b.Data, v, y * b.Width + bx, w);
            }
    }

    /// <summary>Three-pass separable box blur (≈ Gaussian) with radius 1–32.</summary>
    public static void Blur(PixelBuffer b, PixelRect region, int radius)
    {
        radius = Math.Clamp(radius, ImageEffect.MinBlur, ImageEffect.MaxBlur);
        var r = region.Intersect(b.FullRect);
        if (r.IsEmpty) return;
        const int passes = 3;
        // Work area: region plus sampling margin, clamped to image.
        var work = r.Inflate(radius * passes).Intersect(b.FullRect);
        int w = work.Width, h = work.Height;
        var ch = new float[4][];
        for (int c = 0; c < 4; c++) ch[c] = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint p = b.Data[(work.Y + y) * b.Width + work.X + x];
                float a = (p >> 24) / 255f;
                int i = y * w + x;
                ch[0][i] = a;
                ch[1][i] = ((p >> 16) & 0xFF) * a;
                ch[2][i] = ((p >> 8) & 0xFF) * a;
                ch[3][i] = (p & 0xFF) * a;
            }
        var tmp = new float[w * h];
        for (int pass = 0; pass < passes; pass++)
            for (int c = 0; c < 4; c++)
            {
                BoxH(ch[c], tmp, w, h, radius);
                BoxV(tmp, ch[c], w, h, radius);
            }
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                int i = (y - work.Y) * w + (x - work.X);
                float a = ch[0][i];
                uint v;
                if (a <= 1e-6f) v = 0;
                else
                {
                    v = PixelBuffer.Pack(ToByte(ch[1][i] / a), ToByte(ch[2][i] / a), ToByte(ch[3][i] / a), ToByte(a * 255f));
                }
                b.Data[y * b.Width + x] = v;
            }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    private static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            double sum = 0;
            // Clamp-to-edge sampling.
            for (int k = -r; k <= r; k++) sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = (float)(sum / (2 * r + 1));
                sum += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        }
    }

    private static void BoxV(float[] src, float[] dst, int w, int h, int r)
    {
        for (int x = 0; x < w; x++)
        {
            double sum = 0;
            for (int k = -r; k <= r; k++) sum += src[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (float)(sum / (2 * r + 1));
                sum += src[Math.Min(y + r + 1, h - 1) * w + x] - src[Math.Max(y - r, 0) * w + x];
            }
        }
    }
}

/// <summary>Removes a horizontal or vertical strip and joins the remaining parts.</summary>
public static class StripCutout
{
    /// <summary>Removes rows [start, end). Rejects removing the entire height.</summary>
    public static PixelBuffer RemoveRows(PixelBuffer src, int start, int end)
    {
        (start, end) = Normalize(start, end, src.Height, "rows");
        var o = new PixelBuffer(src.Width, src.Height - (end - start));
        Array.Copy(src.Data, 0, o.Data, 0, start * src.Width);
        Array.Copy(src.Data, end * src.Width, o.Data, start * src.Width, (src.Height - end) * src.Width);
        return o;
    }

    /// <summary>Removes columns [start, end). Rejects removing the entire width.</summary>
    public static PixelBuffer RemoveColumns(PixelBuffer src, int start, int end)
    {
        (start, end) = Normalize(start, end, src.Width, "columns");
        int nw = src.Width - (end - start);
        var o = new PixelBuffer(nw, src.Height);
        for (int y = 0; y < src.Height; y++)
        {
            Array.Copy(src.Data, y * src.Width, o.Data, y * nw, start);
            Array.Copy(src.Data, y * src.Width + end, o.Data, y * nw + start, src.Width - end);
        }
        return o;
    }

    private static (int, int) Normalize(int start, int end, int size, string what)
    {
        if (end < start) (start, end) = (end, start);
        start = Math.Clamp(start, 0, size);
        end = Math.Clamp(end, 0, size);
        if (end - start <= 0) throw new ArgumentOutOfRangeException(nameof(start), $"Select at least one of the {what} to remove.");
        if (end - start >= size) throw new ArgumentOutOfRangeException(nameof(start), $"Cannot remove all {what}.");
        return (start, end);
    }
}

/// <summary>Alpha masks applied to rectangular captures.</summary>
public static class CaptureMask
{
    /// <summary>Makes pixels whose centers fall outside the inscribed ellipse transparent.</summary>
    public static PixelBuffer ApplyEllipse(PixelBuffer src)
    {
        var o = src.Clone();
        double rx = src.Width / 2.0, ry = src.Height / 2.0;
        for (int y = 0; y < src.Height; y++)
        {
            double dy = (y + 0.5 - ry) / ry;
            for (int x = 0; x < src.Width; x++)
            {
                double dx = (x + 0.5 - rx) / rx;
                if (dx * dx + dy * dy > 1) o.Data[y * o.Width + x] = 0;
            }
        }
        return o;
    }

    /// <summary>
    /// Polygon mask (even-odd, pixel centers). Points are relative to the buffer's top-left.
    /// Throws for degenerate polygons.
    /// </summary>
    public static PixelBuffer ApplyPolygon(PixelBuffer src, IReadOnlyList<PointD> pts)
    {
        if (pts.Count < 3 || Math.Abs(SignedArea(pts)) < 1) throw new ArgumentException("Freehand shape is too small.", nameof(pts));
        var o = src.Clone();
        var xs = new List<double>();
        for (int y = 0; y < src.Height; y++)
        {
            double cy = y + 0.5;
            xs.Clear();
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                var a = pts[i]; var b = pts[j];
                if ((a.Y > cy) != (b.Y > cy))
                    xs.Add(a.X + (cy - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            xs.Sort();
            int row = y * o.Width;
            int k = 0;
            for (int x = 0; x < src.Width; x++)
            {
                double cx = x + 0.5;
                while (k < xs.Count && xs[k] <= cx) k++;
                if ((k & 1) == 0) o.Data[row + x] = 0;
            }
        }
        return o;
    }

    /// <summary>Reduces point count (Ramer–Douglas–Peucker) within <paramref name="tolerance"/>, keeping endpoints.</summary>
    public static PointD[] Simplify(IReadOnlyList<PointD> pts, double tolerance, int maxPoints = 4000)
    {
        if (pts.Count <= 2) return pts.ToArray();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            double best = -1; int idx = -1;
            for (int i = s + 1; i < e; i++)
            {
                double d = SegDist(pts[i], pts[s], pts[e]);
                if (d > best) { best = d; idx = i; }
            }
            if (idx >= 0 && best > tolerance) { keep[idx] = true; stack.Push((s, idx)); stack.Push((idx, e)); }
        }
        var list = new List<PointD>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) list.Add(pts[i]);
        if (list.Count > maxPoints) return Simplify(list, tolerance * 2 + 0.5, maxPoints);
        return list.ToArray();
    }

    private static double SegDist(PointD p, PointD a, PointD b)
    {
        var ab = b - a; double len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 < 1e-12) return PointD.Distance(p, a);
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return PointD.Distance(p, a + ab * t);
    }

    private static double SignedArea(IReadOnlyList<PointD> p)
    {
        double s = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) s += (p[j].X * p[i].Y) - (p[i].X * p[j].Y);
        return s / 2;
    }
}

public static class LumaExtensions
{
    /// <summary>Rec. 601 luminance 0..1 (alpha ignored).</summary>
    public static LumaImage ToLuma(this PixelBuffer b)
    {
        var d = new float[b.Data.Length];
        for (int i = 0; i < d.Length; i++)
        {
            uint p = b.Data[i];
            d[i] = (0.299f * ((p >> 16) & 0xFF) + 0.587f * ((p >> 8) & 0xFF) + 0.114f * (p & 0xFF)) / 255f;
        }
        return new LumaImage(b.Width, b.Height, d);
    }
}
