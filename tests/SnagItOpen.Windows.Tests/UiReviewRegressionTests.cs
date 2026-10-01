using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage;

namespace SnagItOpen.Windows.Tests;

public sealed class UiReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enter_on_a_focused_cancel_or_secondary_button_does_not_accept_the_primary_action(bool secondary) => ThemeTokenTests.RunSta(() =>
    {
        var dialog = new DialogWindow(null, "Confirm deletion", new TextBlock { Text = "Delete capture?" }, "Delete");
        bool accepted = false, declined = false;
        dialog.Accepted = () => accepted = true;
        Button button = secondary ? dialog.AddSecondary("No", () => { declined = true; dialog.Finish(false); }) : dialog.CancelButton!;
        try
        {
            dialog.Show(); dialog.UpdateLayout(); Assert.True(button.Focus());
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, 0, Key.Enter) { RoutedEvent = UIElement.PreviewKeyDownEvent };
            button.RaiseEvent(key);
            Assert.False(accepted); Assert.True(dialog.IsVisible);
            Assert.False(key.Handled); // Let the focused button handle Enter through normal WPF routing.
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(accepted); Assert.False(dialog.IsVisible); Assert.Equal(secondary, declined);
        }
        finally { dialog.Close(); }
        return true;
    });

    [Theory]
    [InlineData("Padding H", 17d)]
    [InlineData("Padding V", 23d)]
    public void Text_padding_rows_read_their_own_values_and_stay_in_sync_through_commit_and_undo(string label, double expected) => WithEditor((vm, services) =>
    {
        var text = new TextAnnotation { Bounds = new RectD(10, 10, 200, 100), StrokeWidth = 2, PaddingX = 17, PaddingY = 23 };
        vm.AddAnnotation(text); vm.Select([], [text.Id]);
        var panel = Panel(vm, services); var row = LogicalDescendants(panel).OfType<SliderRow>().Single(row => row.Label == label);
        Assert.Equal(expected, row.Value); int builds = panel.RebuildCount, undo = vm.History.UndoCount;
        row.Number.Input.Text = "30"; Assert.True(row.Number.CommitEdit());
        Assert.Equal(30d, row.Value); Assert.Equal(undo + 1, vm.History.UndoCount);
        var changed = Assert.IsType<TextAnnotation>(vm.Document.FindAnnotation(text.Id));
        Assert.Equal(30d, label == "Padding H" ? changed.PadX : changed.PadY);
        vm.Undo(); panel.Refresh(); Assert.Equal(expected, row.Value); Assert.Equal(builds, panel.RebuildCount);
        var other = text with { Id = Guid.NewGuid(), PaddingX = 37, PaddingY = 43 };
        vm.AddAnnotation(other); vm.Select([], [text.Id, other.Id]); panel.Refresh(); Assert.True(row.IsMixed);
    });

    [Fact]
    public void Magnifier_zoom_row_tracks_source_geometry_through_commit_undo_and_mixed_selection() => WithEditor((vm, services) =>
    {
        var lens = new MagnifierAnnotation { Bounds = new RectD(10, 10, 120, 120), SourceRegion = new RectD(0, 0, 30, 30), StrokeWidth = 2 };
        vm.AddAnnotation(lens); vm.Select([], [lens.Id]); var panel = Panel(vm, services);
        var row = LogicalDescendants(panel).OfType<SliderRow>().Single(row => row.Label == "Zoom");
        Assert.Equal(4d, row.Value); int undo = vm.History.UndoCount;
        row.Number.Input.Text = "6"; Assert.True(row.Number.CommitEdit());
        Assert.Equal(6d, row.Value); Assert.Equal(6d, Assert.IsType<MagnifierAnnotation>(vm.Document.FindAnnotation(lens.Id)).Zoom, 6); Assert.Equal(undo + 1, vm.History.UndoCount);
        vm.Undo(); panel.Refresh(); Assert.Equal(4d, row.Value);
        var other = lens with { Id = Guid.NewGuid(), SourceRegion = new RectD(0, 0, 60, 60) };
        vm.AddAnnotation(other); vm.Select([], [lens.Id, other.Id]); panel.Refresh(); Assert.True(row.IsMixed);
    });

    [Theory]
    [InlineData(TextVAlign.Middle, TextSizing.Fixed, 1, 2)]
    [InlineData(TextVAlign.Bottom, TextSizing.AutoHeight, 2, 1)]
    public void Text_vertical_and_sizing_segments_read_actual_values_and_mixed_state(TextVAlign vertical, TextSizing sizing, int verticalIndex, int sizingIndex) => WithEditor((vm, services) =>
    {
        var text = new TextAnnotation { Bounds = new RectD(10, 10, 200, 100), VerticalAlign = vertical, Sizing = sizing };
        vm.AddAnnotation(text); vm.Select([], [text.Id]); var panel = Panel(vm, services);
        var segments = LogicalDescendants(panel).OfType<SegmentedControl>().ToArray();
        var alignment = segments.Single(control => AutomationProperties.GetName(control) == "Vertical");
        var boxSize = segments.Single(control => AutomationProperties.GetName(control) == "Box size");
        Assert.Equal(verticalIndex, alignment.SelectedValue); Assert.Equal(sizingIndex, boxSize.SelectedValue);
        var other = text with { Id = Guid.NewGuid(), VerticalAlign = TextVAlign.Top, Sizing = TextSizing.AutoWidth };
        vm.AddAnnotation(other); vm.Select([], [text.Id, other.Id]); panel.Refresh();
        Assert.True(alignment.IsMixed); Assert.True(boxSize.IsMixed);
        alignment.Buttons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.All(vm.Document.Annotations.OfType<TextAnnotation>(), annotation => Assert.Equal(TextVAlign.Middle, annotation.VerticalAlign));
        Assert.Equal(1, alignment.SelectedValue); Assert.False(alignment.IsMixed);
        vm.Undo(); panel.Refresh(); Assert.True(alignment.IsMixed);
        boxSize.Buttons[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.All(vm.Document.Annotations.OfType<TextAnnotation>(), annotation => Assert.Equal(TextSizing.Fixed, annotation.Sizing));
        Assert.Equal(2, boxSize.SelectedValue); Assert.False(boxSize.IsMixed);
        vm.Undo(); panel.Refresh(); Assert.True(boxSize.IsMixed);
    });

    private static AnnotationPropertiesPanel Panel(EditorViewModel vm, AppServices services)
    {
        var panel = new AnnotationPropertiesPanel(services, vm, () => ToolKind.Select, tool => services.ToolStyles.Get(tool.ToString())); panel.Refresh(); return panel;
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (object item in LogicalTreeHelper.GetChildren(root))
            if (item is DependencyObject child) { yield return child; foreach (var nested in LogicalDescendants(child)) yield return nested; }
    }
    private static void WithEditor(Action<EditorViewModel, AppServices> action) => ThemeTokenTests.RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "SnagItOpenUiReviewTests", Guid.NewGuid().ToString("N"));
        try { using var services = new AppServices(new AppPaths(root)); var vm = new EditorViewModel(services, new CaptureCoordinator(services)); action(vm, services); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    });
}
