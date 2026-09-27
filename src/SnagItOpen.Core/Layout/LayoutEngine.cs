using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Layout;

/// <summary>Result of an automatic arrangement.</summary>
public sealed record LayoutResult(PixelRect ExportArea, ImageLayer[] Images);

/// <summary>Thrown when a layout would exceed configured limits.</summary>
public sealed class LayoutLimitException(string message) : Exception(message);

/// <summary>Deterministic vertical/horizontal stacking (spec §5).</summary>
public static class LayoutEngine
{
    /// <summary>
    /// Arranges <paramref name="ordered"/> images. Invisible images keep their bounds and consume no space.
    /// Returns new records in the same order as the input; input is never mutated.
    /// </summary>
    public static LayoutResult Arrange(IReadOnlyList<ImageLayer> ordered, LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode == LayoutMode.Free)
            throw new ArgumentException("Free layout does not use automatic arrangement.", nameof(options));
        ValidateOptions(options);

        bool vertical = options.Mode == LayoutMode.Vertical;
        var visible = new List<int>();
        for (int i = 0; i < ordered.Count; i++) if (ordered[i].Visible) visible.Add(i);

        var result = ordered.ToArray();
        int pad = options.Padding;
        if (visible.Count == 0)
            return new LayoutResult(new PixelRect(0, 0, Math.Max(1, 2 * pad), Math.Max(1, 2 * pad)), result);

        // Source sizes along cross (perpendicular to stacking) and main axes.
        var srcCross = new long[visible.Count];
        var srcMain = new long[visible.Count];
        for (int k = 0; k < visible.Count; k++)
        {
            var sz = ordered[visible[k]].OrientedSize;
            srcCross[k] = vertical ? sz.Width : sz.Height;
            srcMain[k] = vertical ? sz.Height : sz.Width;
        }

        long target = 0;
        if (options.Scale == ScaleMode.MatchCrossAxis)
            target = options.TargetCrossPixels ?? srcCross.Max();

        var dCross = new long[visible.Count];
        var dMain = new long[visible.Count];
        for (int k = 0; k < visible.Count; k++)
        {
            double s = 1;
            if (options.Scale == ScaleMode.MatchCrossAxis)
            {
                s = (double)target / srcCross[k];
                if (!options.AllowUpscale) s = Math.Min(s, 1);
            }
            dCross[k] = Math.Max(1, (long)Math.Round(srcCross[k] * s, MidpointRounding.AwayFromZero));
            dMain[k] = Math.Max(1, (long)Math.Round(srcMain[k] * s, MidpointRounding.AwayFromZero));
        }

        long c = dCross.Max();
        long mainPos = pad;
        for (int k = 0; k < visible.Count; k++)
        {
            long off = options.Alignment switch
            {
                CrossAlignment.Start => 0,
                CrossAlignment.Center => (c - dCross[k]) / 2,
                _ => c - dCross[k],
            };
            long crossPos = pad + off;
            var bounds = vertical
                ? new PixelRect(ToInt(crossPos), ToInt(mainPos), ToInt(dCross[k]), ToInt(dMain[k]))
                : new PixelRect(ToInt(mainPos), ToInt(crossPos), ToInt(dMain[k]), ToInt(dCross[k]));
            result[visible[k]] = ordered[visible[k]] with { Bounds = bounds };
            mainPos = checked(mainPos + dMain[k] + options.Gap);
        }

        long exportCross = checked(2L * pad + c);
        long exportMain = checked(2L * pad + dMain.Sum() + (long)options.Gap * (visible.Count - 1));
        long w = vertical ? exportCross : exportMain;
        long h = vertical ? exportMain : exportCross;
        if (!Limits.IsAcceptableExportSize(w, h))
            throw new LayoutLimitException($"Combined result {w}×{h} exceeds the limit of {Limits.MaxDimension}px per side / {Limits.MaxExportPixels / 1_000_000} MP.");
        return new LayoutResult(new PixelRect(0, 0, (int)w, (int)h), result);
    }

    public static void ValidateOptions(LayoutOptions o)
    {
        if (o.Gap is < 0 or > Limits.MaxGap) throw new ArgumentOutOfRangeException(nameof(o), $"Gap must be 0–{Limits.MaxGap}.");
        if (o.Padding is < 0 or > Limits.MaxPadding) throw new ArgumentOutOfRangeException(nameof(o), $"Padding must be 0–{Limits.MaxPadding}.");
        if (o.TargetCrossPixels is { } t && (t <= 0 || t > Limits.MaxDimension))
            throw new ArgumentOutOfRangeException(nameof(o), "Target size must be positive and within limits.");
    }

    private static int ToInt(long v) => v is > int.MaxValue or < int.MinValue
        ? throw new LayoutLimitException("Layout coordinate overflow.")
        : (int)v;
}
