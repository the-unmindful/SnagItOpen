using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Documents.Annotations;

/// <summary>
/// Copies appearance between annotations while keeping the target's identity, geometry and content.
/// Used for tool prototypes, quick styles and "paste style".
/// </summary>
public static class AnnotationStyle
{
    /// <summary>
    /// Returns <paramref name="target"/> restyled like <paramref name="style"/>. Same kind: every style
    /// property is copied. Different kinds: only colour, stroke, opacity and shadow.
    /// </summary>
    public static Annotation Transfer(Annotation style, Annotation target)
    {
        if (target is RedactionAnnotation r) return r with { Color = style.Color with { A = 255 } };
        if (style.GetType() != target.GetType())
        {
            var common = target with { Color = style.Color, Alpha = style.Alpha, Shadow = style.Shadow };
            return target is HighlightAnnotation or StepAnnotation or StampAnnotation ? common : common with { StrokeWidth = style.StrokeWidth };
        }
        // Start from the style and put back what belongs to the target.
        var s = style with
        {
            Id = target.Id, ImageLayerId = null, Bounds = target.Bounds, Rotation = target.Rotation, Locked = target.Locked, GroupId = target.GroupId,
        };
        return (s, target) switch
        {
            (LineAnnotation l, LineAnnotation t) => l with { Start = t.Start, End = t.End, Control = t.Control },
            (CalloutAnnotation c, CalloutAnnotation t) => c with { Text = t.Text, Tail = t.Tail, Sizing = t.Sizing },
            (TextAnnotation x, TextAnnotation t) => x with { Text = t.Text, Sizing = t.Sizing },
            (StepAnnotation st, StepAnnotation t) => st with { Number = t.Number, Tail = t.Tail, CustomText = t.CustomText },
            (FreehandAnnotation f, FreehandAnnotation t) => f with { Points = t.Points },
            (MagnifierAnnotation m, MagnifierAnnotation t) => m with { SourceRegion = t.SourceRegion },
            (StampAnnotation sp, StampAnnotation t) => sp with { AssetId = t.AssetId, Symbol = t.AssetId is null ? sp.Symbol : t.Symbol },
            _ => s,
        };
    }

    /// <summary>Instantiates a tool prototype at new geometry with a fresh ID.</summary>
    public static T Instantiate<T>(T prototype) where T : Annotation =>
        (T)(prototype with { Id = Guid.NewGuid(), ImageLayerId = null, Locked = false, Rotation = 0 });

    /// <summary>Default prototype for each tool kind (by <see cref="Annotation.Kind"/>).</summary>
    public static Annotation? DefaultPrototype(string kind) => kind switch
    {
        "Rectangle" => new RectangleAnnotation { Color = Rgba32.Red, StrokeWidth = 3 },
        "Ellipse" => new EllipseAnnotation { Color = Rgba32.Red, StrokeWidth = 3 },
        "Arrow" => new ArrowAnnotation { Color = Rgba32.Red, StrokeWidth = 4 },
        "Line" => new LineAnnotation { Color = Rgba32.Red, StrokeWidth = 3 },
        "Text" => new TextAnnotation { Color = Rgba32.Red, FontSize = 24, StrokeWidth = 0, Text = "" },
        "Callout" => new CalloutAnnotation { Color = Rgba32.Red, FontSize = 20, StrokeWidth = 2, Fill = Rgba32.White, Text = "" },
        "Highlight" => new HighlightAnnotation(),
        "Step" => new StepAnnotation { Color = Rgba32.Red, StrokeWidth = 2 },
        "Freehand" => new FreehandAnnotation { Color = Rgba32.Red, StrokeWidth = 3 },
        "Redaction" => new RedactionAnnotation(),
        "Magnifier" => new MagnifierAnnotation { StrokeWidth = 3 },
        "Stamp" => new StampAnnotation { Color = new Rgba32(30, 150, 60, 255), StrokeWidth = 0 },
        _ => null,
    };

    /// <summary>Whether a candidate prototype is structurally valid (used when loading saved styles).</summary>
    public static bool IsValidPrototype(Annotation a)
    {
        var probe = a with { Id = Guid.NewGuid(), ImageLayerId = null, Bounds = new RectD(0, 0, 10, 10) };
        if (probe is StampAnnotation { AssetId: not null } st) probe = st with { AssetId = null };
        return DocumentValidator.IsValid(DocumentState.CreateEmpty() with { Annotations = [probe] });
    }
}
