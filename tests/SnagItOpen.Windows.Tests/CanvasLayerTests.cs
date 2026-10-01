using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Storage;

namespace SnagItOpen.Windows.Tests;

public sealed class CanvasLayerTests
{
    [Fact]
    public void Ten_1080p_layers_and_fifty_hover_updates_never_redraw_document()
    {
        WithCanvas((canvas, vm, services) =>
        {
            var asset = services.Importer.ImportPixelsAsync(PixelBuffer.Solid(1920, 1080, Rgba32.White)).GetAwaiter().GetResult();
            var layers = Enumerable.Range(0, 10).Select(i => ImageLayer.ForAsset(asset, i * 12, i * 12)).ToArray();
            vm.Commit("Fixture", d => DocumentOps.AddImages(d, layers.Select(l => (asset, l)).ToArray()));
            vm.AddAnnotation(new RectangleAnnotation { Bounds = new RectD(40, 40, 120, 80) });
            vm.ClearSelection();
            Render(canvas);
            Assert.Equal(2, VisualTreeHelper.GetChildrenCount(canvas));
            int count = canvas.DocumentRenderCount;
            for (int i = 0; i < 50; i++)
            {
                var point = canvas.ToViewPoint(i % 2 == 0 ? new PointD(40, 40) : new PointD(300, 300));
                typeof(CanvasView).GetMethod("UpdateHover", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, [point]);
                Render(canvas);
                Assert.Equal(i % 2 == 0 ? 2 : 1, Assert.IsType<DrawingVisual>(VisualTreeHelper.GetChild(canvas, 1)).Drawing.Children.Count);
            }
            Assert.Equal(count, canvas.DocumentRenderCount);
            vm.ClearSelection();
            Render(canvas);
            Assert.Equal(count, canvas.DocumentRenderCount);
            canvas.ZoomBy(1.1);
            Render(canvas);
            Assert.Equal(count + 1, canvas.DocumentRenderCount);
            vm.SetPreview(vm.Document with { Background = Rgba32.White });
            Render(canvas);
            Assert.Equal(count + 2, canvas.DocumentRenderCount);
        });
    }

    [Fact]
    public void Object_eye_and_lock_are_named_focusable_toggle_peers_and_apply_one_edit()
    {
        WithCanvas((_, vm, _) =>
        {
            vm.AddAnnotation(new RectangleAnnotation { Bounds = new RectD(0, 0, 100, 80) });
            var type = typeof(CanvasView).Assembly.GetType("SnagItOpen.App.Editor.ObjectsList")!;
            var objects = (FrameworkElement)Activator.CreateInstance(type, vm)!;
            var window = new Window { Content = objects, Width = 240, Height = 200, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                var toggles = Descendants(objects).OfType<ToggleButton>().ToArray();
                Assert.Equal(2, toggles.Length);
                foreach (var toggle in toggles)
                {
                    Assert.True(toggle.Focusable);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle)));
                    var peer = UIElementAutomationPeer.CreatePeerForElement(toggle)!;
                    var provider = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
                    provider.Toggle();
                }
                Assert.True(vm.Document.Annotations[0].Hidden);
                Assert.True(vm.Document.Annotations[0].Locked);
                vm.Undo();
                Assert.False(vm.Document.Annotations[0].Locked);
                Assert.True(vm.Document.Annotations[0].Hidden);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void All_hidden_indicator_keeps_redactions_visible_and_cursor_resource_loads()
    {
        WithCanvas((canvas, vm, _) =>
        {
            vm.AddAnnotation(new RectangleAnnotation { Bounds = new RectD(0, 0, 100, 80), Hidden = true });
            Assert.True(canvas.IsAllContentHidden);
            vm.AddAnnotation(new RedactionAnnotation { Bounds = new RectD(0, 0, 20, 20), Hidden = true });
            Assert.False(canvas.IsAllContentHidden);
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/SnagItOpen;component/Assets/Cursors/rotate.cur"));
            Assert.NotNull(resource);
            using var stream = resource!.Stream;
            using var cursor = new System.Windows.Input.Cursor(stream, true);
            Assert.NotNull(cursor);
        });
    }

    [Fact]
    public void Drawing_a_curved_style_places_its_bend_relative_to_the_new_endpoints()
    {
        WithCanvas((canvas, vm, _) =>
        {
            var prototype = new ArrowAnnotation { Start = new PointD(0, 0), End = new PointD(40, 0), Control = new PointD(20, 12) };
            canvas.PrototypeProvider = _ => prototype;
            canvas.Tool = ToolKind.Arrow;
            typeof(CanvasView).GetField("_downDoc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, new PointD(400, 300));
            typeof(CanvasView).GetField("_curDoc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, new PointD(600, 300));
            typeof(CanvasView).GetMethod("FinishDraw", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);
            var arrow = Assert.IsType<ArrowAnnotation>(Assert.Single(vm.Document.Annotations));
            Assert.Equal(new PointD(400, 300), arrow.Start);
            Assert.Equal(new PointD(600, 300), arrow.End);
            Assert.Equal(new PointD(500, 360), arrow.Control);
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Fact]
    public void Zoom_to_selection_centres_its_bounds_and_fit_width_respects_viewport()
    {
        WithCanvas((canvas, vm, _) =>
        {
            vm.AddAnnotation(new RectangleAnnotation { Bounds = new RectD(400, 300, 100, 80) });
            canvas.ZoomToSelection();
            var centre = canvas.ToViewPoint(new PointD(450, 340));
            Assert.Equal(400, centre.X, 5);
            Assert.Equal(300, centre.Y, 5);
            canvas.FitWidth();
            Assert.InRange(vm.Document.ExportArea.Width * canvas.Zoom, 0, 752);
        });
    }

    private static void Render(CanvasView canvas)
    {
        canvas.UpdateLayout();
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        typeof(CanvasView).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, [dc]);
    }

    private static void WithCanvas(Action<CanvasView, EditorViewModel, AppServices> body) => ThemeTokenTests.RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "SnagItOpenCanvasTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var services = new AppServices(new AppPaths(root));
            var vm = new EditorViewModel(services, new CaptureCoordinator(services));
            var canvas = new CanvasView { ViewModel = vm };
            canvas.Measure(new Size(800, 600));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            body(canvas, vm, services);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    });
}
