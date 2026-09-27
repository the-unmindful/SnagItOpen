using System.Text.Json.Serialization;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Documents.Annotations;

/// <summary>
/// Base annotation. When <see cref="ImageLayerId"/> is set, geometry is in normalized source-image
/// pixels of that layer (before crop/orientation/scale) and the annotation follows the image.
/// Otherwise geometry is in document pixels.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RectangleAnnotation), "rectangle")]
[JsonDerivedType(typeof(EllipseAnnotation), "ellipse")]
[JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(CalloutAnnotation), "callout")]
[JsonDerivedType(typeof(HighlightAnnotation), "highlight")]
[JsonDerivedType(typeof(StepAnnotation), "step")]
[JsonDerivedType(typeof(FreehandAnnotation), "freehand")]
[JsonDerivedType(typeof(RedactionAnnotation), "redaction")]
[JsonDerivedType(typeof(MagnifierAnnotation), "magnifier")]
[JsonDerivedType(typeof(StampAnnotation), "stamp")]
public abstract record Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? ImageLayerId { get; init; }
    public RectD Bounds { get; init; }
    public Rgba32 Color { get; init; } = Rgba32.Red;
    public double StrokeWidth { get; init; } = 3;

    /// <summary>Tool name shown in UI.</summary>
    [JsonIgnore] public abstract string Kind { get; }

    /// <summary>Geometry extent including stroke, tails and handles, in the annotation's own space.</summary>
    public virtual RectD Extent() => Bounds.Inflate(StrokeWidth / 2 + 1);

    /// <summary>Returns a copy with geometry mapped through <paramref name="map"/> (used when moving/re-linking).</summary>
    public virtual Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var a = map(Bounds.TopLeft);
        var b = map(Bounds.BottomRight);
        return this with { Bounds = RectD.FromPoints(a, b) };
    }

    public Annotation Offset(double dx, double dy) => MapGeometry(p => new PointD(p.X + dx, p.Y + dy));
}

public sealed record RectangleAnnotation : Annotation
{
    public Rgba32? Fill { get; init; }
    public double CornerRadius { get; init; }
    public override string Kind => "Rectangle";
}

public sealed record EllipseAnnotation : Annotation
{
    public Rgba32? Fill { get; init; }
    public override string Kind => "Ellipse";
}

/// <summary>Straight line segment from Start to End. Bounds is derived.</summary>
public record LineAnnotation : Annotation
{
    public PointD Start { get; init; }
    public PointD End { get; init; }
    public bool Dashed { get; init; }
    public override string Kind => "Line";

    public override RectD Extent() => RectD.FromPoints(Start, End).Inflate(StrokeWidth * 3 + 2);

    public override Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var s = map(Start); var e = map(End);
        return this with { Start = s, End = e, Bounds = RectD.FromPoints(s, e) };
    }
}

public sealed record ArrowAnnotation : LineAnnotation
{
    public bool DoubleHeaded { get; init; }
    public override string Kind => "Arrow";
}

[JsonConverter(typeof(JsonStringEnumConverter<TextAlign>))]
public enum TextAlign { Left, Center, Right }

public record TextAnnotation : Annotation
{
    public string Text { get; init; } = "Text";
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 24;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public TextAlign Alignment { get; init; } = TextAlign.Left;
    public Rgba32? Fill { get; init; }
    public Rgba32? Outline { get; init; }
    public override string Kind => "Text";
}

/// <summary>Text box with a tail pointing at <see cref="Tail"/>.</summary>
public sealed record CalloutAnnotation : TextAnnotation
{
    public PointD Tail { get; init; }
    /// <summary>Text color; <see cref="Annotation.Color"/> is the outline/tail color and Fill the body.</summary>
    public Rgba32 TextColor { get; init; } = Rgba32.Black;
    public override string Kind => "Callout";

    public override RectD Extent() => Bounds.Union(new RectD(Tail.X, Tail.Y, 0.001, 0.001)).Inflate(StrokeWidth + 2);

    public override Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var a = map(Bounds.TopLeft); var b = map(Bounds.BottomRight);
        return this with { Bounds = RectD.FromPoints(a, b), Tail = map(Tail) };
    }
}

/// <summary>Translucent marker rectangle, drawn with multiply-like translucency.</summary>
public sealed record HighlightAnnotation : Annotation
{
    public HighlightAnnotation() { Color = new Rgba32(255, 230, 0, 255); }
    public byte Opacity { get; init; } = 110;
    public override string Kind => "Highlight";
}

/// <summary>Numbered circle. Numbers are explicit; use Renumber to resequence.</summary>
public sealed record StepAnnotation : Annotation
{
    public int Number { get; init; } = 1;
    public Rgba32 TextColor { get; init; } = Rgba32.White;
    public override string Kind => "Step";
}

public sealed record FreehandAnnotation : Annotation
{
    public const int MaxPoints = 4000;
    public PointD[] Points { get; init; } = [];
    public override string Kind => "Freehand";

    public override RectD Extent() => Points.Length == 0 ? Bounds : BoundsOf(Points).Inflate(StrokeWidth / 2 + 1);

    public override Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var pts = Points.Select(map).ToArray();
        return this with { Points = pts, Bounds = pts.Length == 0 ? Bounds : BoundsOf(pts) };
    }

    public static RectD BoundsOf(IReadOnlyList<PointD> pts)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var p in pts) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
        return RectD.FromEdges(l, t, r, b);
    }
}

/// <summary>Opaque fill. Always rendered at 100% opacity with outward-rounded coverage, after other content.</summary>
public sealed record RedactionAnnotation : Annotation
{
    public RedactionAnnotation() { Color = Rgba32.Black; }
    public override string Kind => "Redaction";
    public override RectD Extent() => Bounds;
}

/// <summary>
/// Magnifier lens. <see cref="SourceRegion"/> is in document pixels; the lens is drawn at <see cref="Annotation.Bounds"/>.
/// Samples image content (with effects and redactions) but never other magnifiers.
/// </summary>
public sealed record MagnifierAnnotation : Annotation
{
    public MagnifierAnnotation() { Color = new Rgba32(40, 40, 40, 255); }
    public RectD SourceRegion { get; init; }
    public bool Circular { get; init; } = true;
    [JsonIgnore] public double Zoom => SourceRegion.Width > 0 ? Bounds.Width / SourceRegion.Width : 1;
    public override string Kind => "Magnifier";

    public override Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var a = map(Bounds.TopLeft); var b = map(Bounds.BottomRight);
        var sa = map(SourceRegion.TopLeft); var sb = map(SourceRegion.BottomRight);
        return this with { Bounds = RectD.FromPoints(a, b), SourceRegion = RectD.FromPoints(sa, sb) };
    }
}

/// <summary>Stamp: either a bundled symbol or an imported image asset.</summary>
public sealed record StampAnnotation : Annotation
{
    /// <summary>Bundled symbol id (see <see cref="StampSymbols"/>), used when AssetId is null.</summary>
    public string? Symbol { get; init; } = StampSymbols.Check;
    public string? AssetId { get; init; }
    public int QuarterTurns { get; init; }
    public override string Kind => "Stamp";
}

public static class StampSymbols
{
    public const string Check = "check";
    public const string Cross = "cross";
    public const string Star = "star";
    public const string Warning = "warning";
    public const string Info = "info";
    public const string Question = "question";
    public const string Heart = "heart";
    public const string ThumbUp = "thumbup";
    public static readonly string[] All = [Check, Cross, Star, Warning, Info, Question, Heart, ThumbUp];
}
