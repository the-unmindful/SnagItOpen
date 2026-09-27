using SnagItOpen.Core.Documents;

namespace SnagItOpen.Core.Geometry;

/// <summary>
/// Central source-pixel → document-pixel transform for an image layer.
/// Order: crop, flip (in cropped axes), clockwise quarter-turns, scale into Bounds, translate.
/// </summary>
public static class ImageTransform
{
    /// <summary>Maps normalized source-image pixels to document pixels.</summary>
    public static Affine SourceToDocument(ImageLayer layer)
    {
        var c = layer.SourceCrop;
        double cw = c.Width, ch = c.Height;
        // 1. crop: move crop origin to 0,0
        var m = Affine.Translation(-c.X, -c.Y);
        // 2. flips within cropped axes
        if (layer.FlipHorizontal) m = m.Then(new Affine(-1, 0, 0, 1, cw, 0));
        if (layer.FlipVertical) m = m.Then(new Affine(1, 0, 0, -1, 0, ch));
        // 3. quarter turns clockwise (y-down): (x,y) -> (h - y, x), size (w,h) -> (h,w)
        double w = cw, h = ch;
        int q = ((layer.QuarterTurns % 4) + 4) % 4;
        for (int i = 0; i < q; i++)
        {
            m = m.Then(new Affine(0, 1, -1, 0, h, 0));
            (w, h) = (h, w);
        }
        // 4. scale into bounds, 5. translate
        var b = layer.Bounds;
        double sx = w > 0 ? b.Width / w : 1, sy = h > 0 ? b.Height / h : 1;
        return m.Then(Affine.Scaling(sx, sy)).Then(Affine.Translation(b.X, b.Y));
    }

    public static Affine DocumentToSource(ImageLayer layer) => SourceToDocument(layer).Inverse();

    /// <summary>Document-space rectangle of a source-space rectangle (axis-aligned under quarter turns).</summary>
    public static RectD SourceRectToDocument(ImageLayer layer, RectD sourceRect) =>
        SourceToDocument(layer).TransformBounds(sourceRect);

    public static RectD DocumentRectToSource(ImageLayer layer, RectD docRect) =>
        DocumentToSource(layer).TransformBounds(docRect);

    /// <summary>Scale factors of the source → document mapping along the document axes.</summary>
    public static (double Sx, double Sy) Scale(ImageLayer layer)
    {
        var o = layer.OrientedSize;
        return (o.Width > 0 ? (double)layer.Bounds.Width / o.Width : 1, o.Height > 0 ? (double)layer.Bounds.Height / o.Height : 1);
    }

    /// <summary>Uniform-ish scale used for converting a document length into source pixels.</summary>
    public static double AverageScale(ImageLayer layer)
    {
        var (sx, sy) = Scale(layer);
        return (sx + sy) / 2;
    }
}
