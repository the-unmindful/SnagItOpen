using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Storage;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

public sealed class GalleryContractTests
{
    [Fact]
    public void Every_drawing_tool_has_six_to_eight_valid_named_built_in_styles()
    {
        foreach (var kind in BuiltInStyles.Kinds)
        {
            var entries = BuiltInStyles.For(kind); Assert.InRange(entries.Count, 6, 8);
            Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
            Assert.All(entries, entry => { Assert.True(entry.BuiltIn); Assert.Equal(kind, entry.Style.Kind); Assert.True(AnnotationStyle.IsValidPrototype(entry.Style), entry.Name); });
        }
    }

    [Fact]
    public void Gallery_round_trip_preserves_names_order_and_hidden_built_ins()
    {
        using var sandbox = new Sandbox(); var path = sandbox.PathOf("styles.json"); var store = new AnnotationStyleStore(path); store.Load();
        string builtin = store.GalleryFor("Arrow")[0].Id; store.HideGallery("Arrow", builtin);
        var custom = store.SaveGallery("Arrow", "My arrow", new ArrowAnnotation { StrokeWidth = 9 });
        store.RenameGallery("Arrow", custom.Id, "Renamed arrow"); store.MoveGallery("Arrow", custom.Id, 0);
        var again = new AnnotationStyleStore(path); again.Load(); Assert.Equal(custom.Id, again.GalleryFor("Arrow")[0].Id);
        Assert.Equal("Renamed arrow", again.GalleryFor("Arrow")[0].Name); Assert.DoesNotContain(again.GalleryFor("Arrow"), entry => entry.Id == builtin);
        again.RestoreBuiltIns("Arrow"); Assert.Contains(again.GalleryFor("Arrow"), entry => entry.Id == builtin); Assert.Contains(again.GalleryFor("Arrow"), entry => entry.Id == custom.Id);
    }

    [Fact]
    public void Quick_styles_migrate_once_while_the_old_field_remains_readable()
    {
        using var sandbox = new Sandbox(); var path = sandbox.PathOf("styles.json");
        new JsonFileStore<AnnotationStyleFile>(path, () => new(1, new(), [], [])).Save(new(1, new(), [new QuickStyle("Old rectangle", new RectangleAnnotation { StrokeWidth = 7 })], []));
        var store = new AnnotationStyleStore(path); store.Load(); Assert.Single(store.GalleryFor("Rectangle"), entry => !entry.BuiltIn);
        store.UseColor(Rgba32.Red); var again = new AnnotationStyleStore(path); again.Load();
        Assert.Single(again.GalleryFor("Rectangle"), entry => !entry.BuiltIn); Assert.Single(again.QuickStyles);
    }

    [Fact]
    public void Gallery_cap_refuses_an_extra_style_and_built_ins_cannot_be_renamed_or_deleted()
    {
        using var sandbox = new Sandbox(); var store = new AnnotationStyleStore(sandbox.PathOf("styles.json")); store.Load();
        var builtin = store.GalleryFor("Text")[0]; Assert.False(store.RenameGallery("Text", builtin.Id, "Changed")); store.DeleteGallery("Text", builtin.Id);
        Assert.Contains(store.GalleryFor("Text", true), entry => entry.Id == builtin.Id && entry.Hidden);
        while (store.GalleryFor("Text", true).Count < AnnotationStyleStore.MaxGalleryPerTool) store.SaveGallery("Text", "Saved " + store.GalleryFor("Text", true).Count, new TextAnnotation { StrokeWidth = 0 });
        Assert.Throws<InvalidOperationException>(() => store.SaveGallery("Text", "Too many", new TextAnnotation { StrokeWidth = 0 })); Assert.Equal(40, store.GalleryFor("Text", true).Count);
    }

    [Fact]
    public void Corrupt_gallery_falls_back_with_backup_and_complete_built_ins()
    {
        using var sandbox = new Sandbox(); var path = sandbox.PathOf("styles.json"); File.WriteAllText(path, "{broken");
        var store = new AnnotationStyleStore(path); store.Load(); Assert.NotNull(store.LastWarning); Assert.NotEmpty(Directory.GetFiles(sandbox.Root, "*.bak")); Assert.Equal(8, store.GalleryFor("Arrow").Count);
    }

    [Fact]
    public void Preview_uses_real_annotation_pixels_and_cache_is_separate_per_theme() => ThemeTokenTests.RunSta(() =>
    {
        var renderer = new StyleThumbnailRenderer(); var style = BuiltInStyles.For("Arrow")[0].Style;
        var bitmap = renderer.Render(style, EffectiveTheme.Light); Assert.Same(bitmap, renderer.Render(style, EffectiveTheme.Light)); Assert.Equal(1, renderer.RenderCount);
        var pixels = new byte[StyleThumbnailRenderer.TileWidth * StyleThumbnailRenderer.TileHeight * 4]; bitmap.CopyPixels(pixels, StyleThumbnailRenderer.TileWidth * 4, 0);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4 + 2] > 160 && pixels[i * 4 + 1] < 100);
        Assert.NotSame(bitmap, renderer.Render(style, EffectiveTheme.Dark)); Assert.Equal(2, renderer.RenderCount); return true;
    });

    [Fact]
    public void Curved_style_uses_current_endpoints_and_plain_style_preserves_existing_curve()
    {
        var line = new ArrowAnnotation { Start = new(100, 200), End = new(300, 200), Bounds = new RectD(100, 200, 200, 1) };
        var curvedStyle = BuiltInStyles.For("Arrow").Single(entry => entry.Id.EndsWith(":curve", StringComparison.Ordinal)).Style;
        var curved = Assert.IsType<ArrowAnnotation>(StyleGallery.ApplyStyle(curvedStyle, line)); Assert.Equal(line.Start, curved.Start); Assert.Equal(line.End, curved.End); Assert.NotNull(curved.Control);
        Assert.InRange(curved.Control!.Value.X, 190, 210); Assert.True(curved.Control.Value.Y < 200);
        var plain = Assert.IsType<ArrowAnnotation>(StyleGallery.ApplyStyle(new ArrowAnnotation { Color = Rgba32.Black }, curved)); Assert.Equal(curved.Control, plain.Control);
    }

    [Fact]
    public void Gallery_applies_to_a_selection_in_one_undo_step_and_keeps_identity_and_geometry() => WithEditor((vm, services) =>
    {
        var first = new RectangleAnnotation { Bounds = new RectD(10, 10, 40, 30) }; var second = new RectangleAnnotation { Bounds = new RectD(70, 10, 40, 30) };
        vm.AddAnnotation(first); vm.AddAnnotation(second); vm.Select([], [first.Id, second.Id]); int count = vm.History.UndoCount;
        var gallery = new StyleGallery(services, vm, () => vm.PrimaryAnnotation ?? vm.Document.Annotations[0], () => { }); gallery.Refresh("Rectangle");
        gallery.Apply(services.AnnotationStyles.GalleryFor("Rectangle").Single(entry => entry.Id.EndsWith(":black", StringComparison.Ordinal)));
        Assert.Equal(count + 1, vm.History.UndoCount); Assert.All(vm.Document.Annotations, annotation => Assert.Equal(Rgba32.Black, annotation.Color));
        Assert.Equal(first.Bounds, vm.Document.FindAnnotation(first.Id)!.Bounds); vm.Undo(); Assert.All(vm.Document.Annotations, annotation => Assert.Equal(Rgba32.Red, annotation.Color));
    });

    [Fact]
    public void Annotation_values_and_mixed_controls_update_without_rebuilding_or_losing_focus() => WithEditor((vm, services) =>
    {
        var first = new RectangleAnnotation { Bounds = new RectD(10, 10, 40, 30), StrokeWidth = 3 }; var second = new RectangleAnnotation { Bounds = new RectD(70, 10, 40, 30), StrokeWidth = 7, Fill = Rgba32.White };
        vm.AddAnnotation(first); vm.AddAnnotation(second); vm.Select([], [first.Id]);
        var panel = new AnnotationPropertiesPanel(services, vm, () => ToolKind.Select, kind => services.ToolStyles.Get(kind.ToString())); panel.Refresh();
        var window = new Window { Content = panel, Width = 360, Height = 600, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var width = Descendants(panel).OfType<SliderRow>().First(row => row.Label == "width"); width.Number.Input.Focus(); Assert.True(width.Number.Input.IsKeyboardFocused); int builds = panel.RebuildCount;
            vm.UpdateSelectedAnnotations(annotation => annotation with { StrokeWidth = 9 }, "Width"); panel.Refresh(); Assert.Equal(9d, width.Value); Assert.Equal(builds, panel.RebuildCount); Assert.True(width.Number.Input.IsKeyboardFocused);
            vm.Undo(); panel.Refresh(); Assert.Equal(3d, width.Value); Assert.True(width.Number.Input.IsKeyboardFocused);
            vm.Select([], [first.Id, second.Id]); panel.Refresh(); Assert.Equal(builds, panel.RebuildCount); Assert.True(width.IsMixed); Assert.Contains(Descendants(panel).OfType<SegmentedControl>(), segmented => segmented.IsMixed);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Edge_panel_keeps_its_tree_when_the_selected_image_changes() => WithEditor((vm, services) =>
    {
        var asset = services.Importer.ImportPixelsAsync(PixelBuffer.Solid(40, 30, Rgba32.White)).GetAwaiter().GetResult(); var first = ImageLayer.ForAsset(asset); var second = ImageLayer.ForAsset(asset, 60, 0) with { Edge = new EdgeStyle { CornerRadius = 12 } };
        vm.Commit("Images", doc => DocumentOps.AddImages(doc, [(asset, first), (asset, second)])); vm.Select([first.Id]);
        var panel = new ImageEdgePanel(services, vm); var radius = LogicalDescendants(panel).OfType<SliderRow>().Single(row => row.Label == "Corner radius");
        vm.Select([second.Id]); panel.Refresh(); Assert.Equal(1, panel.RebuildCount); Assert.Equal(12d, radius.Value); Assert.Same(radius, LogicalDescendants(panel).OfType<SliderRow>().Single(row => row.Label == "Corner radius"));
    });

    [Fact]
    public async Task Dashed_rectangle_renders_gaps_and_old_rectangle_json_defaults_to_solid()
    {
        using var sandbox = new Sandbox(); var rectangle = new RectangleAnnotation { Bounds = new RectD(10, 10, 120, 60), Color = Rgba32.Black, StrokeWidth = 2, Dash = LineDash.Dashed };
        var document = DocumentOps.SetExportArea(DocumentState.CreateEmpty() with { Background = Rgba32.White, Layout = LayoutOptions.Default with { Mode = LayoutMode.Free } }, new PixelRect(0, 0, 160, 100)); document = DocumentOps.AddAnnotation(document, rectangle);
        var pixels = await sandbox.RenderAsync(document); var colors = Enumerable.Range(20, 100).Select(x => PixelBuffer.Unpack(pixels[x, 10]).R).ToArray(); Assert.Contains(colors, value => value < 30); Assert.Contains(colors, value => value > 220);
        var old = JsonSerializer.Deserialize<RectangleAnnotation>("{\"fill\":null,\"cornerRadius\":0}", Json.Options); Assert.Equal(LineDash.Solid, old!.Dash);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (object item in LogicalTreeHelper.GetChildren(root)) if (item is DependencyObject child) { yield return child; foreach (var nested in LogicalDescendants(child)) yield return nested; }
    }
    private static void WithEditor(Action<EditorViewModel, AppServices> action) => ThemeTokenTests.RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "SnagItOpenGalleryTests", Guid.NewGuid().ToString("N"));
        try { using var services = new AppServices(new AppPaths(root)); var vm = new EditorViewModel(services, new CaptureCoordinator(services)); action(vm, services); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    });
}
