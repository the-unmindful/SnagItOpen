using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.App.Editor;

/// <summary>
/// Live edge controls for the selected images: border, shadow, rounded corners and torn edges, with the
/// same sliders and colour picker as annotations. Dragging previews; each change is one undo step.
/// </summary>
internal sealed class ImageEdgePanel : StackPanel
{
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private bool _previewing;

    public ImageEdgePanel(AppServices services, EditorViewModel vm)
    {
        _services = services;
        _vm = vm;
        AutomationProperties.SetName(this, "Image edges");
    }

    private IReadOnlyList<ImageLayer> Targets() =>
        _vm.SelectedImages.Select(_vm.Document.FindImage).OfType<ImageLayer>().ToList();

    public void Refresh()
    {
        Children.Clear();
        var t = Targets();
        if (t.Count == 0) return;
        var e = t[0].Edge ?? new EdgeStyle();
        bool mixed = t.Select(x => x.Edge ?? new EdgeStyle()).Distinct().Count() > 1;
        Children.Add(new TextBlock { Text = "Edges", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        if (mixed) Hint("The selected images have different edges; changes apply to each.");

        // Border
        Toggle("Border", e.BorderWidth > 0, on => x => x with { BorderWidth = on ? Math.Max(2, x.BorderWidth) : 0 });
        if (e.BorderWidth > 0)
        {
            ColorRow("  colour", e.BorderColor, (x, c) => x with { BorderColor = c }, "Border colour");
            SliderRow("  width", e.BorderWidth, 1, 40, "px", (x, v) => x with { BorderWidth = (int)Math.Round(v) }, "Border width");
        }

        // Shadow
        Toggle("Shadow", e.ShadowSize > 0, on => x => x with { ShadowSize = on ? Math.Max(8, x.ShadowSize) : 0 });
        if (e.ShadowSize > 0)
        {
            ColorRow("  colour", e.ShadowColor, (x, c) => x with { ShadowColor = c }, "Shadow colour");
            SliderRow("  blur", e.ShadowSize, 1, 60, "px", (x, v) => x with { ShadowSize = (int)Math.Round(v) }, "Shadow size");
            SliderRow("  offset X", e.ShadowOffsetX, -40, 40, "px", (x, v) => x with { ShadowOffsetX = (int)Math.Round(v) }, "Shadow offset");
            SliderRow("  offset Y", e.ShadowOffsetY, -40, 40, "px", (x, v) => x with { ShadowOffsetY = (int)Math.Round(v) }, "Shadow offset");
        }

        SliderRow("Corner radius", e.CornerRadius, 0, 200, "px", (x, v) => x with { CornerRadius = (int)Math.Round(v) }, "Corner radius");

        // Torn edges
        var sides = new WrapPanel { Margin = new Thickness(88, 2, 0, 2) };
        foreach (var (side, name) in new[] { (TornSides.Top, "Top"), (TornSides.Right, "Right"), (TornSides.Bottom, "Bottom"), (TornSides.Left, "Left") })
        {
            var cb = new CheckBox { Content = name, IsChecked = e.TornSides.HasFlag(side), Margin = new Thickness(0, 0, 8, 0) };
            cb.Click += (_, _) => Commit(x => x with { TornSides = cb.IsChecked == true ? x.TornSides | side : x.TornSides & ~side }, "Torn edge");
            sides.Children.Add(cb);
        }
        Children.Add(Row("Torn edges", new TextBlock()));
        Children.Add(sides);
        if (e.TornSides != TornSides.None)
        {
            SliderRow("  depth", e.TornDepth, 2, 40, "px", (x, v) => x with { TornDepth = (int)Math.Round(v) }, "Torn depth");
            var again = new Button { Content = "New tear pattern", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(88, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
            again.Click += (_, _) => Commit(x => x with { TornSeed = Random.Shared.Next(1, 1_000_000) }, "Torn pattern");
            Children.Add(again);
        }

        var remove = new Button { Content = "Remove all edges", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 6, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
        remove.Click += (_, _) => { EndPreview(); _vm.SetEdge(null); Refresh(); };
        Children.Add(remove);
    }

    // ------------------------------------------------------------------ apply / preview

    private void Commit(Func<EdgeStyle, EdgeStyle> f, string label)
    {
        EndPreview();
        var ids = _vm.SelectedImages.ToArray();
        if (ids.Length > 0) _vm.Commit(label, d => DocumentOps.UpdateEdges(d, ids, f));
        Refresh();
    }

    private void Preview(Func<EdgeStyle, EdgeStyle> f)
    {
        try { _vm.SetPreview(DocumentOps.UpdateEdges(_vm.Document, _vm.SelectedImages.ToArray(), f)); _previewing = true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }

    private void EndPreview()
    {
        if (!_previewing) return;
        _previewing = false;
        _vm.SetPreview(null);
    }

    // ------------------------------------------------------------------ controls

    private void Hint(string text) =>
        Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });

    private static Grid Row(string label, FrameworkElement c)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Label { Content = label, Padding = new Thickness(0, 3, 6, 0), Target = c, VerticalAlignment = VerticalAlignment.Center });
        if (label.Trim().Length > 0) AutomationProperties.SetName(c, label.Trim());
        Grid.SetColumn(c, 1);
        g.Children.Add(c);
        return g;
    }

    private void Toggle(string label, bool on, Func<bool, Func<EdgeStyle, EdgeStyle>> set)
    {
        var none = new ToggleButton { Content = "None", IsChecked = !on, Padding = new Thickness(8, 1, 8, 1), ToolTip = $"No {label.ToLowerInvariant()}" };
        var solid = new ToggleButton { Content = "On", IsChecked = on, Padding = new Thickness(8, 1, 8, 1), ToolTip = $"Add a {label.ToLowerInvariant()}" };
        none.Click += (_, _) => Commit(set(false), on ? $"Remove {label.ToLowerInvariant()}" : label);
        solid.Click += (_, _) => Commit(set(true), label);
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(none);
        p.Children.Add(solid);
        Children.Add(Row(label, p));
    }

    private void ColorRow(string label, Rgba32 value, Func<EdgeStyle, Rgba32, EdgeStyle> set, string undo)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(ColorPicker.Chip(value, 20));
        content.Children.Add(new TextBlock { Text = value.ToHex(), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Consolas") });
        var btn = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(3, 2, 6, 2), ToolTip = "Click to choose a colour" };
        AutomationProperties.SetName(btn, $"{label.Trim()}, {value.ToHex()}");
        btn.Click += (_, _) => ColorPicker.Open(btn, value, false, _services.AnnotationStyles.RecentColors,
            preview: c => { if (c is { } cc) Preview(x => set(x, cc)); },
            commit: c => { if (c is { } cc) { _services.AnnotationStyles.UseColor(cc); Commit(x => set(x, cc), undo); } },
            cancel: EndPreview);
        Children.Add(Row(label, btn));
    }

    private void SliderRow(string label, double value, double min, double max, string unit, Func<EdgeStyle, double, EdgeStyle> map, string undo)
    {
        value = Math.Clamp(value, min, max);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = 1, LargeChange = 5, IsMoveToPointEnabled = true, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var box = new TextBox { Width = 42, Text = value.ToString("0", CultureInfo.CurrentCulture), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, label.Trim());
        AutomationProperties.SetName(box, label.Trim() + " value");
        bool dragging = false, syncing = false;
        double committed = value;
        void Done(double v)
        {
            v = Math.Clamp(Math.Round(v), min, max);
            if (Math.Abs(v - committed) < 1e-9) { EndPreview(); return; }
            committed = v;
            Commit(x => map(x, v), undo);
        }
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            syncing = true; box.Text = e.NewValue.ToString("0", CultureInfo.CurrentCulture); syncing = false;
            if (dragging) Preview(x => map(x, e.NewValue)); else Done(e.NewValue);
        };
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; Done(slider.Value); }));
        void Typed()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) Done(v);
            else { _vm.Status = $"{label.Trim()} must be a number between {min} and {max}."; box.Text = committed.ToString("0", CultureInfo.CurrentCulture); }
        }
        box.LostKeyboardFocus += (_, _) => Typed();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Typed(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down) { Done(committed + (e.Key == Key.Up ? 1 : -1) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1)); e.Handled = true; }
        };
        var dock = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(box);
        right.Children.Add(new TextBlock { Text = unit, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) });
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(slider);
        Children.Add(Row(label, dock));
    }
}
