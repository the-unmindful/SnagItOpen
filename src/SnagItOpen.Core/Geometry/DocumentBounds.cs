using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;

namespace SnagItOpen.Core.Geometry;

/// <summary>Content extent calculations used by Fit canvas and automatic canvas sizing.</summary>
public static class DocumentBounds
{
    /// <summary>Document-space rectangle drawn by a layer, including edge effects (border/shadow).</summary>
    public static RectD LayerExtent(ImageLayer layer)
    {
        var b = layer.Bounds.ToRectD();
        if (layer.Edge is { IsEmpty: false } e)
        {
            var (l, t, r, bt) = e.Expansion();
            b = RectD.FromEdges(b.X - l, b.Y - t, b.Right + r, b.Bottom + bt);
        }
        return b;
    }

    /// <summary>Full document-space extent of an annotation (annotations are canvas objects, never clipped).</summary>
    public static RectD AnnotationExtent(DocumentState doc, Annotation a) =>
        AnnotationCanvas.ToDocument(doc, a).Extent();

    /// <summary>Union of visible images and annotations, in document pixels. Empty when nothing is visible.</summary>
    public static RectD ContentBounds(DocumentState doc)
    {
        RectD u = default;
        bool any = false;
        foreach (var l in doc.Images)
        {
            if (!l.Visible) continue;
            u = any ? u.Union(LayerExtent(l)) : LayerExtent(l);
            any = true;
        }
        foreach (var a in doc.Annotations)
        {
            var e = AnnotationExtent(doc, a);
            if (e.IsEmpty) continue;
            u = any ? u.Union(e) : e;
            any = true;
        }
        return any ? u : default;
    }

    /// <summary>Fit rectangle: floor(min) / ceil(max) of content, plus padding. 1×1 at origin if empty.</summary>
    public static PixelRect Fit(DocumentState doc, int padding)
    {
        var c = ContentBounds(doc);
        if (c.IsEmpty) return new PixelRect(0, 0, 1, 1);
        var r = c.ToPixelRectOutward();
        return r.Inflate(Math.Max(0, padding));
    }
}
