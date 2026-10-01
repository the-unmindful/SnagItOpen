using System.Text.Json.Serialization;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Documents.Annotations;

/// <summary>
/// Base annotation: a composition (canvas) object in document pixels. <see cref="ImageLayerId"/> exists only
/// for reading older documents, where geometry was in an image's source pixels; such annotations are
/// converted by <see cref="AnnotationCanvas"/> and never created any more.
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
    /// <summary>Drop shadow offset in document pixels.</summary>
    public const double ShadowOffset = 4;

    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Legacy link (schema 1). Converted on load; never set by the editor.</summary>
    public Guid? ImageLayerId { get; init; }
    public RectD Bounds { get; init; }
    public Rgba32 Color { get; init; } = Rgba32.Red;
    public double StrokeWidth { get; init; } = 3;
    /// <summary>Clockwise degrees about the bounds centre; used only when <see cref="CanRotate"/>.</summary>
    public double Rotation { get; init; }
    /// <summary>Overall opacity 0–1 (not applied to redactions).</summary>
    public double Alpha { get; init; } = 1;
    /// <summary>Soft drop shadow below-right (not applied to redactions).</summary>
    public bool Shadow { get; init; }
    /// <summary>Locked items can be selected and styled but not moved or reshaped.</summary>
    public bool Locked { get; init; }
    /// <summary>Hidden items are kept in the document but not drawn, exported or hit-tested.</summary>
    public bool Hidden { get; init; }
    /// <summary>Optional user label shown in the Objects list.</summary>
    public string? Name { get; init; }

    /// <summary>Tool name shown in UI; also the key for remembered tool styles.</summary>
    [JsonIgnore] public abstract string Kind { get; }

    /// <summary>Box-shaped kinds rotate about their centre. Lines and freehand rotate their points instead.</summary>
    [JsonIgnore] public virtual bool CanRotate => false;

    /// <summary>Bounds after rotation (axis-aligned box of the rotated rectangle).</summary>
    [JsonIgnore] public RectD RotatedBox => CanRotate && Rotation != 0 ? Rotation2D.RotateRect(Bounds, Rotation) : Bounds;

    /// <summary>Everything this annotation paints, including stroke, heads, tails and shadow.</summary>
    public RectD Extent()
    {
        var e = CoreExtent();
        if (Shadow && !e.IsEmpty) e = RectD.FromEdges(e.X, e.Y, e.Right + ShadowOffset + 2, e.Bottom + ShadowOffset + 2);
        return e;
    }

    protected virtual RectD CoreExtent() => RotatedBox.Inflate(StrokeWidth / 2 + 1);

    /// <summary>Returns a copy with geometry mapped through <paramref name="map"/> (translation / uniform scale).</summary>
    public virtual Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var a = map(Bounds.TopLeft);
        var b = map(Bounds.BottomRight);
        return this with { Bounds = RectD.FromPoints(a, b) };
    }

    public Annotation Offset(double dx, double dy) => MapGeometry(p => new PointD(p.X + dx, p.Y + dy));
    /// <summary>A user move (drag, nudge, duplicate, paste). Same as <see cref="Offset"/> except where the
    /// annotation has parts that must stay anchored to the document (a magnifier's source region).</summary>
    public virtual Annotation Translate(double dx, double dy) => Offset(dx, dy);
}

public sealed record RectangleAnnotation : Annotation
{
    public Rgba32? Fill { get; init; }
    public double CornerRadius { get; init; }
    public LineDash Dash { get; init; }
    public override string Kind => "Rectangle";
    public override bool CanRotate => true;
}

public sealed record EllipseAnnotation : Annotation
{
    public Rgba32? Fill { get; init; }
    public override string Kind => "Ellipse";
    public override bool CanRotate => true;
}

[JsonConverter(typeof(JsonStringEnumConverter<ArrowCap>))]
public enum ArrowCap { None, Filled, Open, Circle, Square, Bar }

[JsonConverter(typeof(JsonStringEnumConverter<LineDash>))]
public enum LineDash { Solid, Dashed, Dotted }

/// <summary>Line from Start to End; optionally a quadratic curve through <see cref="Control"/>. Bounds is derived.</summary>
public record LineAnnotation : Annotation
{
    public PointD Start { get; init; }
    public PointD End { get; init; }
    /// <summary>Legacy flag (schema 1); <see cref="Dash"/> supersedes it.</summary>
    public bool Dashed { get; init; }
    public LineDash Dash { get; init; }
    public ArrowCap StartCap { get; init; }
    public ArrowCap EndCap { get; init; }
    /// <summary>Arrowhead / cap size multiplier (1 = default).</summary>
    public double HeadSize { get; init; } = 1;
    /// <summary>Quadratic Bézier control point; null = straight.</summary>
    public PointD? Control { get; init; }
    /// <summary>Optional contrast outline drawn around the line and caps.</summary>
    public Rgba32? Outline { get; init; }
    public override string Kind => "Line";

    [JsonIgnore] public LineDash EffectiveDash => Dash != LineDash.Solid ? Dash : Dashed ? LineDash.Dashed : LineDash.Solid;
    [JsonIgnore] public virtual ArrowCap EffectiveStartCap => StartCap;
    [JsonIgnore] public virtual ArrowCap EffectiveEndCap => EndCap;

    /// <summary>Point on the (curved) line at parameter t ∈ [0,1].</summary>
    public PointD PointAt(double t)
    {
        if (Control is not { } c) return new PointD(Start.X + (End.X - Start.X) * t, Start.Y + (End.Y - Start.Y) * t);
        double u = 1 - t;
        return new PointD(u * u * Start.X + 2 * u * t * c.X + t * t * End.X, u * u * Start.Y + 2 * u * t * c.Y + t * t * End.Y);
    }

    /// <summary>Middle of the drawn line (where the bend handle sits).</summary>
    [JsonIgnore] public PointD CurveMid => PointAt(0.5);

    [JsonIgnore] public double CapSize => Math.Max(8, StrokeWidth * 3.5) * Math.Clamp(HeadSize, 0.25, 8);

    protected override RectD CoreExtent()
    {
        var r = RectD.FromPoints(Start, End);
        if (Control is not null) for (int i = 1; i < 16; i++) r = r.Union(new RectD(PointAt(i / 16.0).X, PointAt(i / 16.0).Y, 0.001, 0.001));
        bool caps = EffectiveStartCap != ArrowCap.None || EffectiveEndCap != ArrowCap.None;
        double pad = (caps ? CapSize : StrokeWidth) + (Outline is null ? 0 : Math.Max(2, StrokeWidth * 0.6)) + 2;
        return r.Inflate(pad);
    }

    public override Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var s = map(Start); var e = map(End);
        return this with { Start = s, End = e, Control = Control is { } c ? map(c) : null, Bounds = RectD.FromPoints(s, e) };
    }
}

public sealed record ArrowAnnotation : LineAnnotation
{
    public ArrowAnnotation() { EndCap = ArrowCap.Filled; }
    /// <summary>Legacy flag (schema 1): a filled head at the start as well.</summary>
    public bool DoubleHeaded { get; init; }
    public override string Kind => "Arrow";
    public override ArrowCap EffectiveStartCap => StartCap != ArrowCap.None ? StartCap : DoubleHeaded ? ArrowCap.Filled : ArrowCap.None;
}

[JsonConverter(typeof(JsonStringEnumConverter<TextAlign>))]
public enum TextAlign { Left, Center, Right }

/// <summary>AutoWidth: no wrapping, the box fits the longest line. AutoHeight: fixed width, wraps, height fits. Fixed: user-sized.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TextSizing>))]
public enum TextSizing { Fixed, AutoWidth, AutoHeight }

[JsonConverter(typeof(JsonStringEnumConverter<TextVAlign>))]
public enum TextVAlign { Top, Middle, Bottom }

public record TextAnnotation : Annotation
{
    public string Text { get; init; } = "Text";
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 24;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public TextAlign Alignment { get; init; } = TextAlign.Left;
    public Rgba32? Fill { get; init; }
    /// <summary>Outline around the glyphs (readability on busy backgrounds).</summary>
    public Rgba32? Outline { get; init; }
    /// <summary>Box border colour (width is <see cref="Annotation.StrokeWidth"/>).</summary>
    public Rgba32? Border { get; init; }
    public double Padding { get; init; } = 6;
    /// <summary>Horizontal/vertical padding; null falls back to <see cref="Padding"/> (files saved before these existed).</summary>
    public double? PaddingX { get; init; }
    public double? PaddingY { get; init; }
    public double CornerRadius { get; init; } = 4;
    /// <summary>How the box follows its text. Old files have no value and load as Fixed, so they never move.</summary>
    public TextSizing Sizing { get; init; } = TextSizing.Fixed;
    /// <summary>Vertical placement of the text block (cap height to last baseline) inside the box.</summary>
    public TextVAlign VerticalAlign { get; init; } = TextVAlign.Top;
    [JsonIgnore] public double PadX => Math.Clamp(PaddingX ?? Padding, 0, 200);
    [JsonIgnore] public double PadY => Math.Clamp(PaddingY ?? Padding, 0, 200);
    public override string Kind => "Text";
    public override bool CanRotate => true;
}

[JsonConverter(typeof(JsonStringEnumConverter<CalloutShape>))]
public enum CalloutShape { Rounded, Rectangle, Ellipse }

/// <summary>Text box with a tail pointing at <see cref="Tail"/> (document pixels, not rotated).</summary>
public sealed record CalloutAnnotation : TextAnnotation
{
    public PointD Tail { get; init; }
    /// <summary>Text colour; <see cref="Annotation.Color"/> is the border/tail colour and Fill the body.</summary>
    public Rgba32 TextColor { get; init; } = Rgba32.Black;
    public double TailWidth { get; init; } = 24;
    public CalloutShape Shape { get; init; }
    public override string Kind => "Callout";

    protected override RectD CoreExtent() => RotatedBox.Union(new RectD(Tail.X, Tail.Y, 0.001, 0.001)).Inflate(StrokeWidth + 2);

    public override Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var a = map(Bounds.TopLeft); var b = map(Bounds.BottomRight);
        return this with { Bounds = RectD.FromPoints(a, b), Tail = map(Tail) };
    }
}

/// <summary>Translucent marker rectangle.</summary>
public sealed record HighlightAnnotation : Annotation
{
    public HighlightAnnotation() { Color = new Rgba32(255, 230, 0, 255); }
    /// <summary>Marker strength 0–255.</summary>
    public byte Opacity { get; init; } = 110;
    public override string Kind => "Highlight";
    public override bool CanRotate => true;
}

[JsonConverter(typeof(JsonStringEnumConverter<StepShape>))]
public enum StepShape { Circle, RoundedSquare, Square, Diamond }

[JsonConverter(typeof(JsonStringEnumConverter<StepLabelStyle>))]
public enum StepLabelStyle { Numbers, UpperLetters, LowerLetters, Roman, Custom }

/// <summary>Numbered marker. Numbers are explicit; use renumber commands to resequence.</summary>
public sealed record StepAnnotation : Annotation
{
    public int Number { get; init; } = 1;
    public Rgba32 TextColor { get; init; } = Rgba32.White;
    public StepShape Shape { get; init; }
    public StepLabelStyle LabelStyle { get; init; }
    public string? CustomText { get; init; }
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
    /// <summary>Border colour (width is StrokeWidth); null = white.</summary>
    public Rgba32? Border { get; init; }
    public string FontFamily { get; init; } = "Segoe UI";
    /// <summary>Optional pointer tip in document pixels.</summary>
    public PointD? Tail { get; init; }
    public override string Kind => "Step";
    public override bool CanRotate => true;

    [JsonIgnore] public string Label => StepLabels.Format(LabelStyle, Number, CustomText, Prefix, Suffix);

    protected override RectD CoreExtent()
    {
        var r = RotatedBox;
        if (Tail is { } t) r = r.Union(new RectD(t.X, t.Y, 0.001, 0.001));
        return r.Inflate(StrokeWidth / 2 + 1);
    }

    public override Annotation MapGeometry(Func<PointD, PointD> map)
    {
        var a = map(Bounds.TopLeft); var b = map(Bounds.BottomRight);
        return this with { Bounds = RectD.FromPoints(a, b), Tail = Tail is { } t ? map(t) : null };
    }
}

public static class StepLabels
{
    public const int MaxAffix = 20, MaxCustom = 40;

    public static string Format(StepLabelStyle style, int n, string? custom, string? prefix, string? suffix)
    {
        string core = style switch
        {
            StepLabelStyle.UpperLetters => Letters(n),
            StepLabelStyle.LowerLetters => Letters(n).ToLowerInvariant(),
            StepLabelStyle.Roman => Roman(n),
            StepLabelStyle.Custom => string.IsNullOrEmpty(custom) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : custom,
            _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return (prefix ?? "") + core + (suffix ?? "");
    }

    /// <summary>1 → A, 26 → Z, 27 → AA (spreadsheet style); values below 1 fall back to digits.</summary>
    public static string Letters(int n)
    {
        if (n < 1) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var s = "";
        while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; }
        return s;
    }

    public static string Roman(int n)
    {
        if (n is < 1 or > 3999) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (int V, string S)[] map = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var sb = new System.Text.StringBuilder();
        foreach (var (v, s) in map) while (n >= v) { sb.Append(s); n -= v; }
        return sb.ToString();
    }
}

public sealed record FreehandAnnotation : Annotation
{
    public const int MaxPoints = 4000;
    public PointD[] Points { get; init; } = [];
    public override string Kind => "Freehand";

    protected override RectD CoreExtent() => Points.Length == 0 ? Bounds : BoundsOf(Points).Inflate(StrokeWidth / 2 + 1);

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

/// <summary>Opaque fill. Always axis-aligned, 100% opaque, outward-rounded, drawn after other content.</summary>
public sealed record RedactionAnnotation : Annotation
{
    public RedactionAnnotation() { Color = Rgba32.Black; }
    public override string Kind => "Redaction";
    protected override RectD CoreExtent() => Bounds;
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
    /// <summary>Moving the lens keeps showing the same content: only the lens moves (F-MAG1).</summary>
    public override Annotation Translate(double dx, double dy) => this with { Bounds = new RectD(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height) };
    /// <summary>Moves or resizes what the lens shows; the lens stays put.</summary>
    public MagnifierAnnotation WithSource(RectD source) => this with { SourceRegion = source };

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
    public override bool CanRotate => true;
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
