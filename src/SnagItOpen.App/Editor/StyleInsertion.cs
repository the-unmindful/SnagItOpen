using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;

namespace SnagItOpen.App.Editor;

/// <summary>Builds a ready-to-place annotation from a saved style (double-click or drag from the gallery, F-STY1).</summary>
public static class StyleInsertion
{
    public static Annotation Create(Annotation style, PointD c, DocumentState doc)
    {
        var a = style with { Id = Guid.NewGuid(), ImageLayerId = null, Locked = false, Hidden = false, Name = null, Rotation = 0 };
        switch (a)
        {
            case LineAnnotation line:
                {
                    var start = new PointD(c.X - 70, c.Y + 35); var end = new PointD(c.X + 70, c.Y - 35);
                    PointD? control = line.Control is null ? null : new PointD(c.X - 20, c.Y - 40);
                    return line with { Start = start, End = end, Control = control, Bounds = RectD.FromPoints(start, end) };
                }
            case CalloutAnnotation callout:
                {
                    var box = (CalloutAnnotation)AnnotationRenderer.Fit(callout with { Text = "Callout", Sizing = TextSizing.AutoWidth, Bounds = new RectD(c.X, c.Y, 10, 10) });
                    var placed = box.Bounds with { X = c.X - box.Bounds.Width / 2, Y = c.Y - box.Bounds.Height - 40 };
                    return box with { Bounds = placed, Tail = new PointD(c.X - placed.Width / 4, c.Y + 20) };
                }
            case TextAnnotation text:
                {
                    var fit = AnnotationRenderer.Fit(text with { Text = "Text", Sizing = TextSizing.AutoWidth, Bounds = new RectD(c.X, c.Y, 10, 10) });
                    return fit with { Bounds = fit.Bounds with { X = c.X - fit.Bounds.Width / 2, Y = c.Y - fit.Bounds.Height / 2 } };
                }
            case StepAnnotation step:
                {
                    double size = step.Bounds.Width > 4 ? step.Bounds.Width : 32;
                    int next = doc.Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
                    return step with { Number = next, Tail = null, Bounds = new RectD(c.X - size / 2, c.Y - size / 2, size, size) };
                }
            case FreehandAnnotation pen:
                {
                    var pts = Enumerable.Range(0, 9).Select(i => new PointD(c.X - 80 + i * 20, c.Y + (i % 2 == 0 ? 20 : -20))).ToArray();
                    return pen with { Points = pts, Bounds = RectD.FromPoints(pts[0], pts[^1]) with { Y = c.Y - 20, Height = 40 } };
                }
            case MagnifierAnnotation lens:
                return lens with { Bounds = new RectD(c.X + 20, c.Y - 60, 120, 120), SourceRegion = new RectD(c.X - 80, c.Y - 30, 60, 60) };
            case StampAnnotation stamp:
                return stamp with { Bounds = new RectD(c.X - 24, c.Y - 24, 48, 48) };
            default:
                return a with { Bounds = new RectD(c.X - 80, c.Y - 50, 160, 100) };
        }
    }
}
