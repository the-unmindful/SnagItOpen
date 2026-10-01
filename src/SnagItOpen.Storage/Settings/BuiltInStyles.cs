using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Storage.Settings;

/// <summary>First-party, stable gallery entries. IDs survive future additions and user reordering.</summary>
public static class BuiltInStyles
{
    public static IReadOnlyList<string> Kinds { get; } = ["Arrow", "Line", "Rectangle", "Ellipse", "Text", "Callout", "Highlight", "Step", "Freehand", "Redaction", "Magnifier", "Stamp"];
    private static readonly Rgba32 Blue = new(0, 103, 192, 255), Yellow = new(255, 230, 0, 255), Green = new(40, 170, 70, 255), Pink = new(255, 80, 160, 255);
    public static IReadOnlyList<GalleryEntry> For(string kind)
    {
        var entries = new List<GalleryEntry>();
        void Add(string id, string name, Annotation style) => entries.Add(new($"builtin:{kind}:{id}", name, style with { Id = Guid.Empty, ImageLayerId = null, Locked = false, Hidden = false }, true, false));
        switch (kind)
        {
            case "Arrow":
                Add("red", "Red arrow · 4 px", new ArrowAnnotation { StrokeWidth = 4 });
                Add("thick", "Thick red arrow · 8 px", new ArrowAnnotation { StrokeWidth = 8 });
                Add("black", "Black arrow · 3 px", new ArrowAnnotation { Color = Rgba32.Black });
                Add("outline", "Red arrow with white outline", new ArrowAnnotation { StrokeWidth = 4, Outline = Rgba32.White });
                Add("yellow", "Thick yellow arrow", new ArrowAnnotation { Color = Yellow, StrokeWidth = 8 });
                Add("dashed", "Dashed blue arrow", new ArrowAnnotation { Color = Blue, Dash = LineDash.Dashed });
                Add("double", "Double-headed arrow", new ArrowAnnotation { StrokeWidth = 4, StartCap = ArrowCap.Filled });
                Add("curve", "Curved arrow", new ArrowAnnotation { StrokeWidth = 4, Control = new PointD(25, -15) });
                break;
            case "Rectangle":
                Add("red", "Red outline", new RectangleAnnotation());
                Add("yellow", "Yellow highlight fill", new RectangleAnnotation { Color = Yellow, StrokeWidth = 0, Fill = Yellow with { A = 100 } });
                Add("blue", "Rounded blue", new RectangleAnnotation { Color = Blue, CornerRadius = 8 });
                Add("black", "Thick black", new RectangleAnnotation { Color = Rgba32.Black, StrokeWidth = 6 });
                Add("dashed", "Dashed blue", new RectangleAnnotation { Color = Blue, Dash = LineDash.Dashed });
                Add("shadow", "Red with shadow", new RectangleAnnotation { Shadow = true, CornerRadius = 4 });
                break;
            case "Text":
                Add("red", "Bold red text", new TextAnnotation { Bold = true, StrokeWidth = 0 });
                Add("black", "Black on white", new TextAnnotation { Color = Rgba32.Black, Fill = Rgba32.White, StrokeWidth = 0 });
                Add("white", "White on black", new TextAnnotation { Color = Rgba32.White, Fill = Rgba32.Black, StrokeWidth = 0 });
                Add("yellow", "Yellow on dark", new TextAnnotation { Color = Yellow, Fill = new Rgba32(35, 35, 35, 255), Bold = true, StrokeWidth = 0 });
                Add("outline", "Red with white outline", new TextAnnotation { Outline = Rgba32.White, Bold = true, StrokeWidth = 0 });
                Add("blue", "Blue text", new TextAnnotation { Color = Blue, StrokeWidth = 0 });
                break;
            case "Step":
                Add("red", "Red circle", new StepAnnotation { StrokeWidth = 2 });
                Add("blue", "Blue circle", new StepAnnotation { Color = Blue, StrokeWidth = 2 });
                Add("black", "Black square", new StepAnnotation { Color = Rgba32.Black, Shape = StepShape.Square, StrokeWidth = 2 });
                Add("letter", "Blue letter", new StepAnnotation { Color = Blue, LabelStyle = StepLabelStyle.UpperLetters, StrokeWidth = 2 });
                Add("diamond", "Yellow diamond", new StepAnnotation { Color = Yellow, TextColor = Rgba32.Black, Shape = StepShape.Diamond, StrokeWidth = 2 });
                Add("rounded", "Green rounded square", new StepAnnotation { Color = Green, Shape = StepShape.RoundedSquare, StrokeWidth = 2 });
                break;
            case "Callout":
                Add("rounded", "Rounded white bubble", new CalloutAnnotation { Fill = Rgba32.White, StrokeWidth = 2 });
                Add("square", "Square blue bubble", new CalloutAnnotation { Color = Blue, Fill = Rgba32.White, Shape = CalloutShape.Rectangle, StrokeWidth = 2 });
                Add("ellipse", "Yellow speech bubble", new CalloutAnnotation { Color = Rgba32.Black, Fill = Yellow, Shape = CalloutShape.Ellipse, StrokeWidth = 2 });
                Add("dark", "Dark bubble", new CalloutAnnotation { Color = Rgba32.White, TextColor = Rgba32.White, Fill = Rgba32.Black, StrokeWidth = 2 });
                Add("pink", "Pink bubble", new CalloutAnnotation { Color = Pink, Fill = Pink with { A = 80 }, StrokeWidth = 3 });
                Add("shadow", "White bubble with shadow", new CalloutAnnotation { Fill = Rgba32.White, Shadow = true, StrokeWidth = 2 });
                break;
            case "Highlight":
                foreach (var (id, name, color) in new[] { ("yellow", "Yellow marker", Yellow), ("green", "Green marker", Green), ("pink", "Pink marker", Pink), ("blue", "Blue marker", Blue), ("orange", "Orange marker", new Rgba32(255, 145, 0, 255)), ("purple", "Purple marker", new Rgba32(150, 90, 210, 255)) }) Add(id, name, new HighlightAnnotation { Color = color });
                break;
            default:
                var prototype = AnnotationStyle.DefaultPrototype(kind);
                if (prototype is null) break;
                foreach (var (id, name, color) in new[] { ("red", "Red", Rgba32.Red), ("black", "Black", Rgba32.Black), ("white", "White", Rgba32.White), ("blue", "Blue", Blue), ("green", "Green", Green), ("yellow", "Yellow", Yellow) })
                    Add(id, name, prototype with { Color = color });
                break;
        }
        return entries;
    }
}
