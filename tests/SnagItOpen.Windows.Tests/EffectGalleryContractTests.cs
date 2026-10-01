using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Storage;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

public sealed class EffectGalleryContractTests
{
    [Fact]
    public void Old_tool_defaults_load_without_losing_non_effect_properties()
    {
        using var sandbox = new Sandbox(); string path = sandbox.PathOf("tools.json");
        var old = new ToolStyle { EffectStrength = 13, FontFamily = "Consolas", FontSize = 37, Bold = true, Color = Rgba32.Black };
        new JsonFileStore<ToolStyleFile>(path, () => new(1, new())).Save(new(1, new() { ["Blur"] = old }));
        var store = new ToolStyleStore(path); store.Load(); Assert.Equal(old, store.Get("Blur"));
        Assert.Equal(6, store.EffectGalleryFor("Blur").Count); Assert.Equal(6, store.EffectGalleryFor("Pixelate").Count);
        var saved = store.SaveEffectGallery("Blur", "Fine blur", old); store.Set("Blur", old with { EffectStrength = 20 });
        var again = new ToolStyleStore(path); again.Load(); Assert.Equal(old with { EffectStrength = 20 }, again.Get("Blur"));
        Assert.Equal(13, Assert.Single(again.EffectGalleryFor("Blur"), entry => entry.Id == saved.Id).Style.EffectStrength);
    }

    [Fact]
    public void Effect_gallery_round_trip_preserves_reorder_hide_restore_and_cap()
    {
        using var sandbox = new Sandbox(); string path = sandbox.PathOf("tools.json"); var store = new ToolStyleStore(path); store.Load();
        var builtIn = store.EffectGalleryFor("Pixelate")[0]; store.DeleteEffectGallery("Pixelate", builtIn.Id);
        Assert.False(store.RenameEffectGallery("Pixelate", builtIn.Id, "Different"));
        var saved = store.SaveEffectGallery("Pixelate", "Custom blocks", new() { EffectStrength = 14 }); store.MoveEffectGallery("Pixelate", saved.Id, 0);
        var again = new ToolStyleStore(path); again.Load(); Assert.Equal(saved.Id, again.EffectGalleryFor("Pixelate")[0].Id);
        Assert.DoesNotContain(again.EffectGalleryFor("Pixelate"), entry => entry.Id == builtIn.Id);
        again.RestoreBuiltInEffects("Pixelate"); Assert.Contains(again.EffectGalleryFor("Pixelate"), entry => entry.Id == builtIn.Id);
        while (again.EffectGalleryFor("Pixelate", true).Count < ToolStyleStore.MaxEffectGalleryPerTool)
            again.SaveEffectGallery("Pixelate", "Saved " + again.EffectGalleryFor("Pixelate", true).Count, new());
        Assert.Throws<InvalidOperationException>(() => again.SaveEffectGallery("Pixelate", "Extra", new()));
        Assert.Equal(40, again.EffectGalleryFor("Pixelate", true).Count);
    }

    [Theory]
    [InlineData(ToolKind.Blur, "Blur", 32)]
    [InlineData(ToolKind.Pixelate, "Pixelate", 64)]
    public void Effect_tile_sets_default_and_drawing_uses_it_in_one_undo(ToolKind tool, string kind, int strength) => WithEditor((vm, services) =>
    {
        var asset = services.Importer.ImportPixelsAsync(PixelBuffer.Solid(80, 60, Rgba32.White)).GetAwaiter().GetResult(); var image = ImageLayer.ForAsset(asset);
        vm.Commit("Image", doc => DocumentOps.AddImages(doc, [(asset, image)])); int history = vm.History.UndoCount;
        var original = new ToolStyle { EffectStrength = 9, Color = Rgba32.Black, Bold = true }; services.ToolStyles.Set(kind, original);
        var gallery = new EffectStyleGallery(services, vm, () => { }); gallery.Refresh(tool);
        gallery.Apply(services.ToolStyles.EffectGalleryFor(kind).Single(entry => entry.Style.EffectStrength == strength));
        Assert.Equal(original with { EffectStrength = strength }, services.ToolStyles.Get(kind)); Assert.Equal(history, vm.History.UndoCount);
        var canvas = new CanvasView { Tool = tool, StyleProvider = selected => services.ToolStyles.Get(selected.ToString()), ViewModel = vm };
        var placed = vm.Document.FindImage(image.Id)!;
        Set(canvas, "_downDoc", new PointD(placed.Bounds.X + 5, placed.Bounds.Y + 5)); Set(canvas, "_curDoc", new PointD(placed.Bounds.X + 35, placed.Bounds.Y + 30)); Set(canvas, "_drawLayer", image.Id);
        typeof(CanvasView).GetMethod("FinishDraw", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);
        var effect = Assert.Single(vm.Document.FindImage(image.Id)!.Effects); Assert.Equal(strength, effect.Strength); Assert.Equal(history + 1, vm.History.UndoCount);
        vm.Undo(); Assert.Empty(vm.Document.FindImage(image.Id)!.Effects); Assert.Equal(strength, services.ToolStyles.Get(kind).EffectStrength);
    });

    [Fact]
    public void Effect_inspector_keeps_slider_and_tile_tree_when_default_changes() => WithEditor((vm, services) =>
    {
        var panel = new AnnotationPropertiesPanel(services, vm, () => ToolKind.Blur, tool => services.ToolStyles.Get(tool.ToString())); panel.Refresh();
        var gallery = Assert.Single(panel.Children.OfType<EffectStyleGallery>()); var slider = Assert.Single(panel.Children.OfType<SnagItOpen.App.Controls.SliderRow>());
        int rebuilds = panel.RebuildCount; services.ToolStyles.Set("Blur", new() { EffectStrength = 20 }); panel.Refresh();
        Assert.Equal(rebuilds, panel.RebuildCount); Assert.Same(gallery, Assert.Single(panel.Children.OfType<EffectStyleGallery>())); Assert.Equal(20d, slider.Value);
        Assert.Equal(6, gallery.TileCount); Assert.All(gallery.Tiles, tile => { Assert.Equal(56d, tile.Width); Assert.Equal(40d, tile.Height); Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(tile))); });
    });

    private static void Set(CanvasView canvas, string field, object value) => typeof(CanvasView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, value);
    private static void WithEditor(Action<EditorViewModel, AppServices> action) => ThemeTokenTests.RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "SnagItOpenEffectGalleryTests", Guid.NewGuid().ToString("N"));
        try { using var services = new AppServices(new AppPaths(root)); var vm = new EditorViewModel(services, new CaptureCoordinator(services)); action(vm, services); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    });
}
