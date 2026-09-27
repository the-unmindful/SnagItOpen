using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Stitching;

public enum SeamAxis { Vertical, Horizontal }

/// <summary>
/// Manual overlap join: removes <see cref="Overlap"/> repeated rows (vertical) or columns (horizontal)
/// from the start of the second image, then places it directly after the first. Originals stay editable.
/// </summary>
public static class SeamGeometry
{
    /// <summary>Validates overlap against the second image's cropped main dimension.</summary>
    public static string? Validate(ImageLayer first, ImageLayer second, SeamAxis axis, int overlap, bool requireEqualCross = true)
    {
        int secondMain = axis == SeamAxis.Vertical ? second.SourceCrop.Height : second.SourceCrop.Width;
        if (overlap < 0) return "Overlap cannot be negative.";
        if (overlap >= secondMain) return $"Overlap must be smaller than {secondMain}px.";
        if (requireEqualCross)
        {
            int a = axis == SeamAxis.Vertical ? first.SourceCrop.Width : first.SourceCrop.Height;
            int b = axis == SeamAxis.Vertical ? second.SourceCrop.Width : second.SourceCrop.Height;
            if (a != b) return $"Images must have equal {(axis == SeamAxis.Vertical ? "width" : "height")} ({a} vs {b}). Crop to common size first.";
        }
        return null;
    }

    /// <summary>Returns the new crop for the second image and its document bounds (unscaled, 1:1).</summary>
    public static (PixelRect Crop, PixelRect Bounds) Compute(ImageLayer first, ImageLayer second, SeamAxis axis, int overlap)
    {
        var err = Validate(first, second, axis, overlap, requireEqualCross: false);
        if (err is not null) throw new ArgumentOutOfRangeException(nameof(overlap), err);
        var c = second.SourceCrop;
        var fb = first.Bounds;
        if (axis == SeamAxis.Vertical)
        {
            var crop = new PixelRect(c.X, c.Y + overlap, c.Width, c.Height - overlap);
            return (crop, new PixelRect(fb.X, fb.Bottom, crop.Width, crop.Height));
        }
        else
        {
            var crop = new PixelRect(c.X + overlap, c.Y, c.Width - overlap, c.Height);
            return (crop, new PixelRect(fb.Right, fb.Y, crop.Width, crop.Height));
        }
    }

    /// <summary>
    /// Applies the join as one document change: second image's crop + placement. Switches to Free
    /// so the seam is preserved exactly.
    /// </summary>
    public static DocumentState Join(DocumentState doc, Guid firstId, Guid secondId, SeamAxis axis, int overlap)
    {
        var first = doc.FindImage(firstId) ?? throw new ArgumentException("Unknown first image.");
        var second = doc.FindImage(secondId) ?? throw new ArgumentException("Unknown second image.");
        // Measure overlap relative to the second image's full top/left, so re-joining is idempotent.
        var asset = doc.FindAsset(second.AssetId)!;
        var baseCrop = axis == SeamAxis.Vertical
            ? second.SourceCrop with { Y = 0, Height = second.SourceCrop.Bottom }
            : second.SourceCrop with { X = 0, Width = second.SourceCrop.Right };
        baseCrop = baseCrop.Intersect(asset.FullRect);
        var baseLayer = second with { SourceCrop = baseCrop };
        var (crop, bounds) = Compute(first, baseLayer, axis, overlap);
        var d = doc with
        {
            Layout = doc.Layout with { Mode = LayoutMode.Free },
            Images = doc.Images.Select(i => i.Id == secondId ? i with { SourceCrop = crop, Bounds = bounds } : i).ToArray(),
        };
        return DocumentOps.Reflow(d);
    }
}
