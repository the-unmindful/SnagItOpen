using System.IO;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

public class AnnotationStyleStoreTests
{
    [Fact]
    public void Prototypes_quick_styles_and_recent_colours_survive_reload()
    {
        using var s = new Sandbox();
        var path = s.PathOf("styles.json");
        var store = new AnnotationStyleStore(path);
        store.Load();
        store.SetPrototype("Arrow", new ArrowAnnotation { Color = Fixtures.Blue, StrokeWidth = 7, EndCap = ArrowCap.Open, Dash = LineDash.Dotted });
        store.SaveQuick("Big blue", new RectangleAnnotation { Color = Fixtures.Blue, StrokeWidth = 9, Shadow = true });
        store.UseColor(Fixtures.Green);
        store.UseColor(Fixtures.Blue);

        var again = new AnnotationStyleStore(path);
        again.Load();
        var arrow = Assert.IsType<ArrowAnnotation>(again.Prototype("Arrow"));
        Assert.Equal(7, arrow.StrokeWidth);
        Assert.Equal(ArrowCap.Open, arrow.EndCap);
        Assert.Equal(LineDash.Dotted, arrow.Dash);
        Assert.Single(again.QuickStyles);
        Assert.True(again.QuickStyles[0].Style.Shadow);
        Assert.Equal([Fixtures.Blue, Fixtures.Green], again.RecentColors);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        using var s = new Sandbox();
        var path = s.PathOf("styles.json");
        File.WriteAllText(path, "{ nope");
        var store = new AnnotationStyleStore(path);
        store.Load();
        Assert.NotNull(store.LastWarning);
        Assert.IsType<ArrowAnnotation>(store.Prototype("Arrow"));
    }

    [Fact]
    public void Transfer_keeps_target_geometry_and_text()
    {
        var style = new TextAnnotation { Color = Fixtures.Blue, FontSize = 40, Bold = true, Text = "style", Bounds = new RectD(0, 0, 5, 5) };
        var target = new TextAnnotation { Text = "keep me", Bounds = new RectD(10, 20, 100, 40), Rotation = 30 };
        var r = Assert.IsType<TextAnnotation>(AnnotationStyle.Transfer(style, target));
        Assert.Equal("keep me", r.Text);
        Assert.Equal(target.Bounds, r.Bounds);
        Assert.Equal(30, r.Rotation);
        Assert.Equal(target.Id, r.Id);
        Assert.True(r.Bold);
        Assert.Equal(40, r.FontSize);
    }

    [Fact]
    public void Transfer_to_redaction_only_changes_colour_and_stays_opaque()
    {
        var style = new RectangleAnnotation { Color = Fixtures.Blue with { A = 80 }, Alpha = 0.3 };
        var target = new RedactionAnnotation { Bounds = new RectD(0, 0, 10, 10) };
        var r = Assert.IsType<RedactionAnnotation>(AnnotationStyle.Transfer(style, target));
        Assert.Equal(255, r.Color.A);
        Assert.Equal(1, r.Alpha);
    }
}
