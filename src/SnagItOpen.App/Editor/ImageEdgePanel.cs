using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.App.Editor;

/// <summary>One persistent control tree for image edges; drags preview and commit one history step.</summary>
public sealed class ImageEdgePanel : StackPanel
{
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly List<Action> _updates = [];
    private bool _previewing, _syncing;
    private string _selection = "";
    public int RebuildCount { get; } = 1;
    public ImageEdgePanel(AppServices services, EditorViewModel vm)
    {
        _services = services; _vm = vm; AutomationProperties.SetName(this, "Image edges");
        var section = new InspectorSection(services, "Image.Edges", "Edges"); var body = new StackPanel(); section.Content = body; Children.Add(section);
        Toggle(body, "Border", e => e.BorderWidth > 0, on => e => e with { BorderWidth = on ? Math.Max(2, e.BorderWidth) : 0 });
        Colour(body, "Border colour", e => e.BorderColor, (e, c) => e with { BorderColor = c }, e => e.BorderWidth > 0);
        Slider(body, "Border width", e => e.BorderWidth, 1, 40, (e, v) => e with { BorderWidth = Round(v) }, e => e.BorderWidth > 0);
        Toggle(body, "Shadow", e => e.ShadowSize > 0, on => e => e with { ShadowSize = on ? Math.Max(8, e.ShadowSize) : 0 });
        Colour(body, "Shadow colour", e => e.ShadowColor, (e, c) => e with { ShadowColor = c }, e => e.ShadowSize > 0);
        Slider(body, "Shadow blur", e => e.ShadowSize, 1, 60, (e, v) => e with { ShadowSize = Round(v) }, e => e.ShadowSize > 0);
        Slider(body, "Shadow offset X", e => e.ShadowOffsetX, -40, 40, (e, v) => e with { ShadowOffsetX = Round(v) }, e => e.ShadowSize > 0);
        Slider(body, "Shadow offset Y", e => e.ShadowOffsetY, -40, 40, (e, v) => e with { ShadowOffsetY = Round(v) }, e => e.ShadowSize > 0);
        Slider(body, "Corner radius", e => e.CornerRadius, 0, 200, (e, v) => e with { CornerRadius = Round(v) });
        body.Children.Add(new TextBlock { Text = "Torn edges", Margin = new Thickness(0, 12, 0, 4) });
        var sides = new WrapPanel(); body.Children.Add(sides);
        foreach (var (side, name) in new[] { (TornSides.Top, "Top"), (TornSides.Right, "Right"), (TornSides.Bottom, "Bottom"), (TornSides.Left, "Left") })
        {
            var check = new CheckBox { Content = name, IsThreeState = true, Margin = new Thickness(0, 0, 8, 0) }; bool mixed = false;
            _updates.Add(() => { var values = Edges().Select(e => e.TornSides.HasFlag(side)).Distinct().ToArray(); mixed = values.Length > 1; check.IsChecked = mixed ? null : values.FirstOrDefault(); });
            check.Click += (_, _) => { if (_syncing) return; bool on = mixed || check.IsChecked == true; check.IsChecked = on; Commit(e => e with { TornSides = on ? e.TornSides | side : e.TornSides & ~side }, "Torn edge"); };
            sides.Children.Add(check);
        }
        Slider(body, "Torn depth", e => e.TornDepth, 2, 40, (e, v) => e with { TornDepth = Round(v) }, e => e.TornSides != TornSides.None);
        var pattern = new Button { Content = "New tear pattern", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) }; pattern.Click += (_, _) => Commit(e => e with { TornSeed = Random.Shared.Next(1, 1_000_000) }, "Torn pattern");
        _updates.Add(() => pattern.Visibility = Edges().Any(e => e.TornSides != TornSides.None) ? Visibility.Visible : Visibility.Collapsed); body.Children.Add(pattern);
        var remove = new Button { Content = "Remove all edges", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 4) }; remove.Click += (_, _) => { EndPreview(); _vm.SetEdge(null); Refresh(); }; body.Children.Add(remove);
        Refresh();
    }
    private static int Round(double value) => (int)Math.Round(value);
    private IReadOnlyList<EdgeStyle> Edges() => _vm.SelectedImages.Select(_vm.Displayed.FindImage).OfType<ImageLayer>().Select(i => i.Edge ?? new EdgeStyle()).ToArray();
    public void Refresh()
    {
        string selection = string.Join(",", _vm.SelectedImages.OrderBy(id => id)); if (selection != _selection) { _selection = selection; EndPreview(); }
        _syncing = true; try { foreach (var update in _updates) update(); } finally { _syncing = false; }
    }
    private void Commit(Func<EdgeStyle, EdgeStyle> edit, string label)
    {
        EndPreview(); var ids = _vm.SelectedImages.ToArray(); if (ids.Length > 0) _vm.Commit(label, d => DocumentOps.UpdateEdges(d, ids, edit)); Refresh();
    }
    private void Preview(Func<EdgeStyle, EdgeStyle> edit)
    {
        if (_vm.SelectedImages.Count == 0) return;
        try { _previewing = true; _vm.SetPreview(DocumentOps.UpdateEdges(_vm.Document, _vm.SelectedImages.ToArray(), edit)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { EndPreview(); }
    }
    private void EndPreview() { if (!_previewing) return; _previewing = false; _vm.SetPreview(null); }
    private static Grid Row(string label, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new Label { Content = label, Padding = new Thickness(0, 3, 6, 0), Target = control, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(control, 1); row.Children.Add(control); AutomationProperties.SetName(control, label); return row;
    }
    private void Toggle(Panel body, string label, Func<EdgeStyle, bool> get, Func<bool, Func<EdgeStyle, EdgeStyle>> edit)
    {
        var control = new SegmentedControl([new SegmentOption(false, "None"), new SegmentOption(true, "On")]);
        control.SelectionChanged += value => { if (!_syncing && value is bool on) Commit(edit(on), label); };
        _updates.Add(() => { var values = Edges().Select(get).Distinct().ToArray(); control.SelectedValue = values.FirstOrDefault(); control.IsMixed = values.Length > 1; }); body.Children.Add(Row(label, control));
    }
    private void Colour(Panel body, string label, Func<EdgeStyle, Rgba32> get, Func<EdgeStyle, Rgba32, EdgeStyle> edit, Func<EdgeStyle, bool> visible)
    {
        var control = new ColorSwatchButton { Label = label, AllowNone = false, Recent = _services.AnnotationStyles.RecentColors }; var row = Row(label, control);
        control.Preview += c => { if (!_syncing && c is { } value) Preview(e => edit(e, value)); };
        control.Committed += c => { if (c is not { } value) return; _services.AnnotationStyles.UseColor(value); Commit(e => edit(e, value), label); }; control.Canceled += EndPreview;
        _updates.Add(() => { var edges = Edges(); var values = edges.Select(get).Distinct().ToArray(); control.Value = values.Length > 0 ? values[0] : Rgba32.Black; control.IsMixed = values.Length > 1; control.Recent = _services.AnnotationStyles.RecentColors; row.Visibility = edges.Any(visible) ? Visibility.Visible : Visibility.Collapsed; }); body.Children.Add(row);
    }
    private void Slider(Panel body, string label, Func<EdgeStyle, double> get, double min, double max, Func<EdgeStyle, double, EdgeStyle> edit, Func<EdgeStyle, bool>? visible = null)
    {
        var control = new SliderRow { Label = label, Minimum = min, Maximum = max, Step = 1, Unit = "px", Margin = new Thickness(0, 2, 0, 2) };
        control.Preview += v => { if (!_syncing) Preview(e => edit(e, v)); }; control.Committed += v => { if (!_syncing) Commit(e => edit(e, v), label); };
        _updates.Add(() => { var edges = Edges(); var values = edges.Select(get).Distinct().ToArray(); control.Update(values.FirstOrDefault(), values.Length > 1); control.Visibility = visible is null || edges.Any(visible) ? Visibility.Visible : Visibility.Collapsed; }); body.Children.Add(control);
    }
}