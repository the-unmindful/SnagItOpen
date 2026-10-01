using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Shell;
using SnagItOpen.App.Shell.DialogWindows;
using SnagItOpen.Core.Capture;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

public sealed class ControlInteractionTests
{
    private sealed class NumericSource { public double Value { get; set; } = 12; }

    [Fact]
    public void Numeric_commit_preserves_explicit_binding_and_rejects_invalid_edits() => ThemeTokenTests.RunSta(() =>
    {
        var source = new NumericSource(); var box = new NumberBox { Minimum = 0, Maximum = 100 };
        box.SetBinding(NumberBox.ValueProperty, new Binding(nameof(source.Value)) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        box.Input.Text = "35"; Assert.True(box.CommitEdit());
        var binding = box.GetBindingExpression(NumberBox.ValueProperty); Assert.NotNull(binding);
        Assert.Equal(12d, source.Value); binding!.UpdateSource(); Assert.Equal(35d, source.Value);
        box.Input.Text = "not numeric"; Assert.False(box.CommitEdit()); Assert.Equal(35d, box.Value);
        box.Revert(); Assert.True(box.IsValid); Assert.Equal("35", box.Input.Text); return true;
    });

    [Fact]
    public void Slider_drag_previews_many_values_but_commits_only_once() => ThemeTokenTests.RunSta(() =>
    {
        var row = new SliderRow { Minimum = 0, Maximum = 100, Value = 10 };
        var previews = new List<double>(); var commits = new List<double>(); row.Preview += previews.Add; row.Committed += commits.Add;
        row.Slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        row.Slider.Value = 20; row.Slider.Value = 30; row.Slider.Value = 40;
        Assert.Equal(new[] { 20d, 30d, 40d }, previews); Assert.Empty(commits);
        row.Slider.RaiseEvent(new DragCompletedEventArgs(30, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.Equal(new[] { 40d }, commits); return true;
    });

    [Fact]
    public void Slider_track_press_before_thumb_drag_still_commits_once() => ThemeTokenTests.RunSta(() =>
    {
        var row = new SliderRow { Minimum = 0, Maximum = 100, Value = 10 }; var commits = new List<double>(); row.Committed += commits.Add;
        row.Slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        row.Slider.Value = 25;
        row.Slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent }); row.Slider.Value = 40;
        Assert.Empty(commits); row.Slider.RaiseEvent(new DragCompletedEventArgs(15, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        row.Slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.Equal(new[] { 40d }, commits); return true;
    });

    [Fact]
    public void Slider_numeric_scrub_previews_without_an_early_commit_and_keeps_automation_name() => ThemeTokenTests.RunSta(() =>
    {
        var row = new SliderRow { Label = "Radius", Minimum = 0, Maximum = 100, Value = 10 }; var previews = new List<double>(); var commits = new List<double>();
        row.Preview += previews.Add; row.Committed += commits.Add;
        // NumberBox raises its Preview event for each scrub value, and Committed only on release.
        var preview = typeof(NumberBox).GetField("Preview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        row.Number.Value = 20; ((Action<double>?)preview.GetValue(row.Number))!.Invoke(20);
        Assert.Equal(new[] { 20d }, previews); Assert.Empty(commits); Assert.Equal(20d, row.Slider.Value);
        row.Number.Input.Text = "25"; Assert.True(row.Number.CommitEdit()); Assert.Equal(new[] { 25d }, commits);
        Assert.Equal("Radius", System.Windows.Automation.AutomationProperties.GetName(row.Number.Input)); return true;
    });

    [Fact]
    public void Segmented_keyboard_moves_selection_and_keeps_one_tab_stop() => ThemeTokenTests.RunSta(() =>
    {
        var control = new SegmentedControl([new(0, "Left"), new(1, "Center"), new(2, "Right")]) { SelectedValue = 0, IsMixed = true };
        var window = new Window { Content = control, Width = 280, Height = 120, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout(); control.Buttons[0].Focus();
            control.Buttons[0].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control)!, 0, Key.Right) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            Assert.Equal(1, control.SelectedValue); Assert.False(control.IsMixed); Assert.True(control.Buttons[1].IsKeyboardFocused);
            Assert.Single(control.Buttons, button => button.IsTabStop); Assert.True(control.Buttons[1].IsTabStop);
        }
        finally { window.Close(); }
        return true;
    });

    [Fact]
    public void Inspector_sections_save_latest_state_without_overwriting_other_sections() => ThemeTokenTests.RunSta(() =>
    {
        UiState state = new();
        var first = new InspectorSection(); first.ConfigurePersistence(() => state, saved => state = saved, "Appearance");
        var second = new InspectorSection(); second.ConfigurePersistence(() => state, saved => state = saved, "Canvas");
        first.IsExpanded = false; second.IsExpanded = false;
        Assert.False(state.SectionsOpen["Appearance"]); Assert.False(state.SectionsOpen["Canvas"]);
        var restored = new InspectorSection(); restored.ConfigurePersistence(() => state, saved => state = saved, "Canvas"); Assert.False(restored.IsExpanded); return true;
    });

    [Fact]
    public void Mixed_segment_has_no_selection_and_click_sets_one_value() => ThemeTokenTests.RunSta(() =>
    {
        var segments = new SegmentedControl([new SegmentOption("left", "Left"), new SegmentOption("right", "Right")]) { SelectedValue = "left", IsMixed = true };
        Assert.All(segments.Buttons, button => Assert.False(button.IsChecked));
        segments.Buttons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.False(segments.IsMixed); Assert.Equal("right", segments.SelectedValue); Assert.True(segments.Buttons[1].IsChecked); Assert.False(segments.Buttons[0].IsChecked); return true;
    });

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 8)]
    public void Notification_timeout_pauses_without_spending_remaining_time(bool actions, int seconds)
    {
        var lifetime = new NotificationLifetime(actions);
        Assert.False(lifetime.Advance(TimeSpan.FromSeconds(100), true));
        Assert.Equal(TimeSpan.FromSeconds(seconds), lifetime.Remaining);
        Assert.False(lifetime.Advance(TimeSpan.FromSeconds(seconds - 1), false));
        Assert.True(lifetime.Advance(TimeSpan.FromSeconds(1), false));
    }

    [Fact]
    public void Fourth_toast_drops_oldest_and_each_expiry_removes_only_its_card() => ThemeTokenTests.RunSta(() =>
    {
        var host = new ToastHost(); for (int i = 1; i <= 4; i++) host.Show(new Notification(NotificationKind.Success, "Notice " + i));
        Assert.Equal(3, host.VisibleCount); Assert.Equal(new[] { "Notice 2", "Notice 3", "Notice 4" }, host.Notifications.Select(n => n.Title));
        host.Advance(TimeSpan.FromSeconds(4)); Assert.Equal(0, host.VisibleCount); return true;
    });

    [Fact]
    public void Toast_containing_keyboard_focus_pauses_host_dismissal() => ThemeTokenTests.RunSta(() =>
    {
        var host = new ToastHost(); host.Show(new Notification(NotificationKind.Success, "Copied"));
        var window = new Window { Content = host, Width = 400, Height = 240, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var card = Assert.IsAssignableFrom<System.Windows.Controls.Border>(host.Children[0]);
            var close = ((System.Windows.Controls.DockPanel)card.Child).Children.OfType<IconButton>().Single(); close.Focus();
            Assert.True(card.IsKeyboardFocusWithin); host.Advance(TimeSpan.FromSeconds(100)); Assert.Equal(1, host.VisibleCount);
            window.Content = null; host.Clear();
        }
        finally { window.Close(); }
        return true;
    });

    [Fact]
    public void Shortcuts_inventory_is_complete_and_search_matches_titles_keys_and_groups() => ThemeTokenTests.RunSta(() =>
    {
        ShortcutEntry[] inventory = [new("File", "Save project", "Ctrl+S"), new("Capture global", "Capture region", "PrintScreen"), new("Tools", "Arrow", "A")];
        var window = new ShortcutsWindow(null, inventory); Assert.Equal(inventory.Length, window.EntryCount); Assert.Equal(inventory.Length, window.VisibleEntries.Count);
        window.SearchText = "global print"; Assert.Single(window.VisibleEntries); Assert.Equal("Capture region", window.VisibleEntries[0].Title);
        window.SearchText = "arrow"; Assert.Equal("A", Assert.Single(window.VisibleEntries).Gesture); window.Close(); return true;
    });

    [Theory]
    [InlineData(1920d, 1080d, true)]
    [InlineData(0d, 100d, false)]
    [InlineData(100.5d, 100d, false)]
    [InlineData(10000d, 10000d, false)]
    [InlineData(32768d, 1d, false)]
    public void Scale_validation_observes_existing_export_guardrails(double width, double height, bool valid) =>
        Assert.Equal(valid, DialogValidation.ScaleError(width, height) is null);

    [Theory]
    [InlineData(CaptureDestination.AppendBelow, "Add below current image")]
    [InlineData(CaptureDestination.CopyOnly, "Copy to clipboard only")]
    public void Capture_destinations_use_shared_friendly_labels(CaptureDestination destination, string expected) =>
        Assert.Equal(expected, SnagItOpen.App.Infrastructure.DisplayNames.Destination(destination));
}
