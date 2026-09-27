using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Storage.Projects;

namespace SnagItOpen.Windows.Tests;

/// <summary>Rendering and persistence of the annotation styling added in schema 2.</summary>
public class AnnotationStyleRenderTests
{
    private static readonly Rgba32 Blue = new(0, 0, 255, 255);

    /// <summary>Document with an opaque white 200×200 canvas and the given annotations.</summary>
    private static DocumentState Canvas(params Annotation[] anns)
    {
        var d = DocumentState.CreateEmpty() with { Background = Rgba32.White, Layout = LayoutOptions.Default with { Mode = LayoutMode.Free } };
        d = DocumentOps.SetExportArea(d, new PixelRect(0, 0, 200, 200));
        foreach (var a in anns) d = DocumentOps.AddAnnotation(d, a);
        return d;
    }

    private static bool IsWhite(uint p) => Fixtures.Near(p, Rgba32.White, 8);

    [Fact]
    public async Task Rotated_rectangle_fill_moves_with_rotation()
    {
        using var s = new Sandbox();
        // Tall thin bar centred at (100,100); rotated 90° it becomes wide.
        var bar = new RectangleAnnotation { Bounds = new RectD(95, 40, 10, 120), Fill = Blue, StrokeWidth = 0 };
        var plain = await s.RenderAsync(Canvas(bar));
        var turned = await s.RenderAsync(Canvas(bar with { Rotation = 90 }));
        Assert.True(Fixtures.Near(plain[100, 50], Blue, 8));
        Assert.True(IsWhite(plain[50, 100]));
        Assert.True(Fixtures.Near(turned[50, 100], Blue, 8));
        Assert.True(IsWhite(turned[100, 50]));
    }

    [Fact]
    public async Task Alpha_blends_with_background()
    {
        using var s = new Sandbox();
        var box = new RectangleAnnotation { Bounds = new RectD(50, 50, 100, 100), Fill = Rgba32.Black, StrokeWidth = 0, Alpha = 0.5 };
        var px = await s.RenderAsync(Canvas(box));
        var c = PixelBuffer.Unpack(px[100, 100]);
        Assert.InRange(c.R, 110, 145);
    }

    [Fact]
    public async Task Shadow_paints_below_right_only()
    {
        using var s = new Sandbox();
        var box = new RectangleAnnotation { Bounds = new RectD(50, 50, 60, 60), Fill = Blue, StrokeWidth = 0, Shadow = true };
        var px = await s.RenderAsync(Canvas(box));
        Assert.False(IsWhite(px[112, 112]));   // shadow corner
        Assert.True(IsWhite(px[46, 46]));      // nothing above-left
    }

    [Fact]
    public async Task Arrow_end_caps_render_at_each_end()
    {
        using var s = new Sandbox();
        var both = new ArrowAnnotation { Start = new(30, 100), End = new(170, 100), Color = Blue, StrokeWidth = 4, StartCap = ArrowCap.Circle, EndCap = ArrowCap.Filled };
        var none = both with { StartCap = ArrowCap.None, EndCap = ArrowCap.None };
        var a = await s.RenderAsync(Canvas(both));
        var b = await s.RenderAsync(Canvas(none));
        // Caps are wider than the 4px stroke: 4–5px off-axis is painted only with caps.
        // Filled head: 14px long, tip at x=170, so near its base (x≈158) it is ~12px wide.
        Assert.False(IsWhite(a[158, 104]));
        Assert.False(IsWhite(a[30, 105]));
        Assert.True(IsWhite(b[158, 104]));
        Assert.True(IsWhite(b[30, 105]));
    }

    [Fact]
    public async Task Curved_line_passes_through_bend_point()
    {
        using var s = new Sandbox();
        var straight = new LineAnnotation { Start = new(20, 150), End = new(180, 150), Color = Blue, StrokeWidth = 4 };
        var bent = (LineAnnotation)AnnotationGeometry.Drag(straight, new AnnotationHandle(HandleKind.Bend, straight.CurveMid), straight.CurveMid, new PointD(100, 60), false);
        var px = await s.RenderAsync(Canvas(bent));
        Assert.False(IsWhite(px[100, 60]));
        Assert.True(IsWhite(px[100, 150]));
    }

    [Fact]
    public async Task Step_label_and_shapes_render()
    {
        using var s = new Sandbox();
        foreach (var shape in Enum.GetValues<StepShape>())
        {
            var step = new StepAnnotation { Bounds = new RectD(70, 70, 60, 60), Color = Blue, Shape = shape, LabelStyle = StepLabelStyle.UpperLetters, Number = 2 };
            var px = await s.RenderAsync(Canvas(step));
            Assert.False(IsWhite(px[100, 75]), shape.ToString());   // body painted near the top edge
            Assert.Equal("B", step.Label);
        }
    }

    [Fact]
    public async Task Step_tail_reaches_its_tip()
    {
        using var s = new Sandbox();
        var step = new StepAnnotation { Bounds = new RectD(20, 20, 40, 40), Color = Blue, Tail = new PointD(160, 160) };
        var px = await s.RenderAsync(Canvas(step));
        Assert.False(IsWhite(px[150, 150]));
    }

    [Fact]
    public async Task Redaction_stays_fully_opaque_with_alpha_and_shadow_ignored()
    {
        using var s = new Sandbox();
        var r = new RedactionAnnotation { Bounds = new RectD(40, 40, 50, 30), Alpha = 0.2, Shadow = true, Rotation = 45 };
        var px = await s.RenderAsync(Canvas(r));
        for (int y = 40; y < 70; y++) for (int x = 40; x < 90; x++)
            Assert.True(Fixtures.Near(px[x, y], Rgba32.Black), $"{x},{y}");
        Assert.True(IsWhite(px[95, 75]));
    }

    [Fact]
    public async Task Rotated_text_and_callout_render_without_error()
    {
        using var s = new Sandbox();
        var t = new TextAnnotation { Bounds = new RectD(40, 80, 120, 40), Text = "Hello", Color = Blue, Fill = new Rgba32(255, 255, 0, 255), Rotation = 30, Underline = true, Italic = true, Border = Blue, StrokeWidth = 2 };
        var c = new CalloutAnnotation { Bounds = new RectD(20, 20, 100, 40), Text = "Note", Tail = new PointD(160, 170), Shape = CalloutShape.Ellipse, Rotation = -20, Fill = Rgba32.White, Color = Blue };
        var px = await s.RenderAsync(Canvas(t, c));
        Assert.False(IsWhite(px[100, 100]));
    }

    [Fact]
    public async Task Schema2_properties_roundtrip_through_project()
    {
        using var s = new Sandbox();
        var doc = Canvas(
            new ArrowAnnotation { Start = new(1, 2), End = new(50, 60), StartCap = ArrowCap.Bar, EndCap = ArrowCap.Open, HeadSize = 1.5, Dash = LineDash.Dotted, Control = new PointD(30, 5), Outline = Rgba32.White, Alpha = 0.7, Shadow = true, Locked = true },
            new TextAnnotation { Bounds = new RectD(5, 5, 90, 30), Text = "x", Rotation = 12.5, Underline = true, Border = Blue, Padding = 9, CornerRadius = 2 },
            new CalloutAnnotation { Bounds = new RectD(5, 50, 90, 30), Tail = new(150, 150), TailWidth = 30, Shape = CalloutShape.Rectangle },
            new StepAnnotation { Bounds = new RectD(100, 5, 30, 30), Shape = StepShape.Diamond, LabelStyle = StepLabelStyle.Roman, Number = 4, Prefix = "(", Suffix = ")", Tail = new(190, 190), Border = Blue });
        var path = s.PathOf("v2.sio");
        Assert.True((await new ProjectStore(s.Store).SaveAsync(doc, path, null)).IsSuccess);
        var r = await new ProjectStore(s.Store).LoadAsync(path);
        Assert.True(r.IsSuccess, r.Message);
        var d = r.Value!;
        Assert.Equal(DocumentState.CurrentSchemaVersion, d.SchemaVersion);
        for (int i = 0; i < doc.Annotations.Length; i++) Assert.Equal(doc.Annotations[i], d.Annotations[i], new AnnEq());
        Assert.Equal("(IV)", ((StepAnnotation)d.Annotations[3]).Label);
    }

    /// <summary>Records compare arrays by reference; compare through JSON instead.</summary>
    private sealed class AnnEq : IEqualityComparer<Annotation>
    {
        public bool Equals(Annotation? a, Annotation? b) =>
            System.Text.Json.JsonSerializer.Serialize(a, Storage.Json.Options) == System.Text.Json.JsonSerializer.Serialize(b, Storage.Json.Options);
        public int GetHashCode(Annotation o) => o.Id.GetHashCode();
    }
}
