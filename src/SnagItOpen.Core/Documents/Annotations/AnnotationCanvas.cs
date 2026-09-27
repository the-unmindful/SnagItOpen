using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Documents.Annotations;

/// <summary>
/// Annotations are composition (canvas) objects in document pixels. Older documents could link an
/// annotation to an image (<see cref="Annotation.ImageLayerId"/>, geometry in that image's source pixels);
/// this converts such annotations once to document coordinates at their current on-screen position.
/// </summary>
public static class AnnotationCanvas
{
    /// <summary>Returns the annotation in document coordinates with no image link.</summary>
    public static Annotation ToDocument(DocumentState doc, Annotation a)
    {
        if (a.ImageLayerId is not { } lid) return a;
        var layer = doc.FindImage(lid);
        if (layer is null) return a with { ImageLayerId = null };

        var m = ImageTransform.SourceToDocument(layer);
        double s = ImageTransform.AverageScale(layer);
        var mapped = a.MapGeometry(m.Transform) with { ImageLayerId = null, StrokeWidth = a.StrokeWidth * s };
        return mapped switch
        {
            // Magnifier source regions were already document pixels.
            MagnifierAnnotation mg when a is MagnifierAnnotation orig => mg with { SourceRegion = orig.SourceRegion },
            TextAnnotation t => t with { FontSize = Math.Clamp(t.FontSize * s, 1, 1000) },
            _ => mapped,
        };
    }

    /// <summary>Converts every image-linked annotation; returns the same instance when none are linked.</summary>
    public static DocumentState Normalize(DocumentState doc)
    {
        if (doc.Annotations is null || !doc.Annotations.Any(a => a?.ImageLayerId is not null)) return doc;
        return doc with { Annotations = doc.Annotations.Select(a => a is null ? a! : ToDocument(doc, a)).ToArray() };
    }
}
