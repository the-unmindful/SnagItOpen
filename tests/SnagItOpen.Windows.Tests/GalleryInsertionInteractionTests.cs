using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage;

namespace SnagItOpen.Windows.Tests;

public sealed class GalleryInsertionInteractionTests
{
    [Fact]
    public void Double_click_inserts_once_without_restyling_the_existing_selection() => WithGallery((gallery, button, vm, original) =>
    {
        int undo = vm.History.UndoCount;
        MouseClick(button);
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpClickDelay();
        Assert.Equal(original, vm.Document.FindAnnotation(original.Id));
        Assert.Equal(2, vm.Document.Annotations.Length);
        Assert.Equal(undo + 1, vm.History.UndoCount);
        vm.Undo();
        Assert.Equal(original, Assert.Single(vm.Document.Annotations));
    });

    [Fact]
    public void A_single_mouse_click_still_applies_the_style_in_one_undo_step() => WithGallery((gallery, button, vm, original) =>
    {
        int undo = vm.History.UndoCount;
        MouseClick(button);
        PumpClickDelay();
        Assert.Equal(Rgba32.Black, vm.Document.FindAnnotation(original.Id)!.Color);
        Assert.Single(vm.Document.Annotations);
        Assert.Equal(undo + 1, vm.History.UndoCount);
    });

    [Fact]
    public void Keyboard_or_automation_click_applies_immediately() => WithGallery((gallery, button, vm, original) =>
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Rgba32.Black, vm.Document.FindAnnotation(original.Id)!.Color);
        Assert.Single(vm.Document.Annotations);
    });

    private static void MouseClick(Button button)
    {
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void PumpClickDelay()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void WithGallery(Action<StyleGallery, Button, EditorViewModel, RectangleAnnotation> body) => ThemeTokenTests.RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "SnagItOpenGalleryInteractions", Guid.NewGuid().ToString("N"));
        try
        {
            using var services = new AppServices(new AppPaths(root));
            var vm = new EditorViewModel(services, new CaptureCoordinator(services));
            var original = new RectangleAnnotation { Bounds = new RectD(10, 10, 40, 30), Color = Rgba32.Red };
            vm.AddAnnotation(original);
            vm.InsertStyleRequested += style => vm.AddAnnotation(StyleInsertion.Create(style, new PointD(200, 200), vm.Document));
            var gallery = new StyleGallery(services, vm, () => vm.PrimaryAnnotation, () => { });
            gallery.Refresh("Rectangle");
            var panel = Assert.IsType<WrapPanel>(gallery.Content);
            var button = panel.Children.OfType<Button>().Single(tile => tile.Tag is string id && id.EndsWith(":black", StringComparison.Ordinal));
            body(gallery, button, vm, original);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    });
}
