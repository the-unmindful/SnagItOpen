using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Shell;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Editor;

/// <summary>Persistent control trees: context switches show sections, value changes update bindings.</summary>
public sealed class InspectorPanel : UserControl
{
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly Func<ToolKind> _tool;
    private readonly StackPanel _document = new(), _image = new(), _annotation = new();
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _width, _height;
    private readonly System.Windows.Shapes.Path _kindIcon = new() { Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _outside;
    private readonly CheckBox _snap;
    private readonly RadioButton _auto = new() { Content = "Auto", GroupName = "inspector-canvas" }, _locked = new() { Content = "Locked", GroupName = "inspector-canvas" };
    private readonly InfoBar _privacy = new();
    private readonly InspectorSection _canvas;
    private readonly List<Button> _align = [], _distribute = [];
    private bool _sync;
    internal AnnotationPropertiesPanel AnnotationPanel { get; }
    internal ImageEdgePanel EdgePanel { get; }
    public int RebuildCount { get; } = 1;
    public event Action<OutsideCanvasMode>? OutsideChanged;
    public event Action<bool>? SnapChanged;
    public event Action? CropToolRequested;

    public InspectorPanel(AppServices services, EditorViewModel vm, Func<ToolKind> tool, Func<ToolKind, ToolStyle> styles)
    {
        _services = services; _vm = vm; _tool = tool; DataContext = vm;
        AutomationProperties.SetName(this, "Properties inspector");
        SetResourceReference(BackgroundProperty, "Bg.Surface");
        AnnotationPanel = new(services, vm, tool, styles); EdgePanel = new(services, vm);
        var stack = new StackPanel { Margin = new Thickness(12, 4, 12, 12) };
        var header = new DockPanel { MinHeight = 40 };
        var more = new IconButton { Label = "More properties", Icon = TryFindResource("Icon.More") as Geometry, ShowLabel = false };
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            AddMenu(menu, "Copy style", vm.CopyStyle); AddMenu(menu, "Paste style", vm.PasteStyle);
            AddMenu(menu, "Set as tool default", vm.SetAsDefaultStyle);
            AddMenu(menu, "Reset tool default", () => vm.ResetDefaultStyle(_tool().ToString()));
            more.ContextMenu = menu; menu.PlacementTarget = more; menu.IsOpen = true;
        };
        DockPanel.SetDock(more, Dock.Right); header.Children.Add(more);
        _kindIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text.Primary"); header.Children.Add(_kindIcon); header.Children.Add(_title); stack.Children.Add(header);
        stack.Children.Add(_privacy); stack.Children.Add(_document); stack.Children.Add(_image); stack.Children.Add(_annotation);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var layout = Section(_document, "Layout");
        var layoutContent = new StackPanel(); layout.Content = layoutContent;
        AddNumber(layoutContent, "Gap (px)", nameof(vm.Gap), 0, Limits.MaxGap);
        AddNumber(layoutContent, "Padding (px)", nameof(vm.Padding), 0, Limits.MaxPadding);
        var alignment = new ComboBox { ItemsSource = Enum.GetValues<CrossAlignment>() };
        alignment.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(vm.Alignment)) { Mode = BindingMode.TwoWay });
        AddRow(layoutContent, "Alignment", alignment);
        AddCheck(layoutContent, "Match width / height", nameof(vm.MatchSize));
        AddNumber(layoutContent, "Target size", nameof(vm.TargetSize), 0, Limits.MaxDimension, "Width (vertical) or height (horizontal) to scale every image to. 0 = the largest image.");
        AddCheck(layoutContent, "Allow enlarging smaller images", nameof(vm.AllowUpscale));
        layout.SetBinding(IsEnabledProperty, new Binding(nameof(vm.IsAutoLayout)));

        _canvas = Section(_document, "Canvas"); var canvas = new StackPanel(); _canvas.Content = canvas;
        var mode = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) }; mode.Children.Add(_auto); mode.Children.Add(_locked); canvas.Children.Add(mode);
        _auto.Click += (_, _) => { if (!_sync) vm.FitCanvas(); };
        _locked.Click += (_, _) => { if (!_sync) vm.SetCanvas(vm.Document.ExportArea); };
        _width = new NumberBox { Label = "Width (px)", Minimum = 1, Maximum = Limits.MaxDimension };
        _height = new NumberBox { Label = "Height (px)", Minimum = 1, Maximum = Limits.MaxDimension };
        canvas.Children.Add(_width); canvas.Children.Add(_height);
        _width.Committed += _ => SetCanvasSize(); _height.Committed += _ => SetCanvasSize();
        var sizes = new ComboBox { ItemsSource = new[] { "Size preset", "1920 × 1080", "1280 × 720", "1080 × 1080", "800 × 600" }, SelectedIndex = 0 };
        sizes.SelectionChanged += (_, _) =>
        {
            if (_sync || sizes.SelectedIndex <= 0) return;
            (int W, int H)[] values = [(0, 0), (1920, 1080), (1280, 720), (1080, 1080), (800, 600)];
            _width.Value = values[sizes.SelectedIndex].W; _height.Value = values[sizes.SelectedIndex].H; SetCanvasSize(); sizes.SelectedIndex = 0;
        };
        AddRow(canvas, "Preset", sizes);
        AddButton(canvas, "Fit to content", vm.FitCanvas);
        _outside = new ComboBox { ItemsSource = Enum.GetValues<OutsideCanvasMode>(), SelectedItem = services.Settings.OutsideCanvas };
        _outside.SelectionChanged += (_, _) => { if (!_sync && _outside.SelectedItem is OutsideCanvasMode value) { services.SaveSettings(services.Settings with { OutsideCanvas = value }); OutsideChanged?.Invoke(value); } };
        AddRow(canvas, "Outside canvas", _outside);
        AddCheck(canvas, "Transparent background", nameof(vm.TransparentBackground));
        var color = new ColorSwatchButton();
        color.SetBinding(ColorSwatchButton.ValueProperty, new Binding("BackgroundColor") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        color.Committed += _ => color.GetBindingExpression(ColorSwatchButton.ValueProperty)?.UpdateSource();
        AddRow(canvas, "Background", color);
        var edit = Section(_document, "Editing"); var editing = new StackPanel(); edit.Content = editing;
        _snap = new CheckBox { Content = "Snap while moving (Alt disables)", IsChecked = services.Settings.SnapEnabled };
        _snap.Click += (_, _) => { bool enabled = _snap.IsChecked == true; services.SaveSettings(services.Settings with { SnapEnabled = enabled }); SnapChanged?.Invoke(enabled); }; editing.Children.Add(_snap);

        var position = Section(_image, "Position & size"); var geometry = new StackPanel(); position.Content = geometry;
        AddNumber(geometry, "X", nameof(vm.SelX), -Limits.MaxDimension, Limits.MaxDimension);
        AddNumber(geometry, "Y", nameof(vm.SelY), -Limits.MaxDimension, Limits.MaxDimension);
        AddNumber(geometry, "Width", nameof(vm.SelWidth), 1, Limits.MaxDimension);
        AddNumber(geometry, "Height", nameof(vm.SelHeight), 1, Limits.MaxDimension);
        AddCheck(geometry, "Keep aspect ratio", nameof(vm.KeepAspect));
        var crop = Section(_image, "Crop"); var cropRows = new StackPanel(); crop.Content = cropRows;
        AddNumber(cropRows, "Left", nameof(vm.CropLeft), 0, Limits.MaxDimension);
        AddNumber(cropRows, "Top", nameof(vm.CropTop), 0, Limits.MaxDimension);
        AddNumber(cropRows, "Right", nameof(vm.CropRight), 1, Limits.MaxDimension);
        AddNumber(cropRows, "Bottom", nameof(vm.CropBottom), 1, Limits.MaxDimension);
        AddButton(cropRows, "Reset crop", vm.ResetCrop); AddButton(cropRows, "Crop tool", () => CropToolRequested?.Invoke());
        var orientation = new WrapPanel(); _image.Children.Add(orientation);
        AddIcon(orientation, "Rotate left", "Icon.Reset", () => vm.Rotate(-1)); AddIcon(orientation, "Rotate right", "Icon.Redo", () => vm.Rotate(1));
        AddIcon(orientation, "Flip horizontally", "Icon.Horizontal", () => vm.Flip(true)); AddIcon(orientation, "Flip vertically", "Icon.Vertical", () => vm.Flip(false));
        _image.Children.Add(EdgePanel); _annotation.Children.Add(AnnotationPanel);
        var arrange = Section(stack, "Arrange", false); var actions = new WrapPanel(); arrange.Content = actions;
        AddIcon(actions, "Bring to front", "Icon.Front", () => vm.ZOrder(DocumentOps.ZMove.ToFront));
        AddIcon(actions, "Bring forward", "Icon.Forward", () => vm.ZOrder(DocumentOps.ZMove.Forward));
        AddIcon(actions, "Send backward", "Icon.Backward", () => vm.ZOrder(DocumentOps.ZMove.Backward));
        AddIcon(actions, "Send to back", "Icon.Back", () => vm.ZOrder(DocumentOps.ZMove.ToBack));
        string[] icons = ["Icon.AlignLeft", "Icon.AlignCenterX", "Icon.AlignRight", "Icon.AlignTop", "Icon.AlignMiddle", "Icon.AlignBottom"];
        int ai = 0;
        foreach (var align in Enum.GetValues<AlignMode>()) _align.Add(AddIcon(actions, "Align " + align, icons[ai++ % icons.Length], () => vm.Align(align)));
        _distribute.Add(AddIcon(actions, "Distribute horizontally", "Icon.DistributeH", () => vm.Distribute(true)));
        _distribute.Add(AddIcon(actions, "Distribute vertically", "Icon.DistributeV", () => vm.Distribute(false)));
        AddIcon(actions, "Toggle lock", "Icon.Lock", () => vm.UpdateSelectedAnnotations(a => a with { Locked = !a.Locked }, "Toggle lock"));
        vm.PropertyChanged += OnChanged; Unloaded += (_, _) => vm.PropertyChanged -= OnChanged; Loaded += (_, _) => { vm.PropertyChanged -= OnChanged; vm.PropertyChanged += OnChanged; Refresh(); };
        Refresh();
    }

    public void ShowCanvasSection() { _vm.ClearSelection(); _canvas.IsExpanded = true; Refresh(); _width.Focus(); _width.BringIntoView(); }
    private void OnChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is "" or null or nameof(EditorViewModel.SelectedImages) or nameof(EditorViewModel.SelectedAnnotations)) Refresh(); }
    public void Refresh()
    {
        _sync = true;
        try
        {
            bool annotation = _vm.SelectedAnnotations.Count > 0 || (_vm.SelectedImages.Count == 0 && _tool() is not (ToolKind.Select or ToolKind.Crop or ToolKind.CutOut));
            bool image = !annotation && _vm.SelectedImages.Count > 0;
            _document.Visibility = !annotation && !image ? Visibility.Visible : Visibility.Collapsed;
            _outside.SelectedItem = _services.Settings.OutsideCanvas; _snap.IsChecked = _services.Settings.SnapEnabled;
            _kindIcon.Data = TryFindResource(annotation ? ToolCatalog.All.FirstOrDefault(t => t.Kind.ToString().Equals(_vm.PrimaryAnnotation?.Kind ?? _tool().ToString(), StringComparison.OrdinalIgnoreCase))?.Icon ?? "Icon.Select" : image ? "Icon.Import" : "Icon.Free") as Geometry;
            _image.Visibility = image ? Visibility.Visible : Visibility.Collapsed; _annotation.Visibility = annotation ? Visibility.Visible : Visibility.Collapsed;
            _title.Text = annotation ? (_vm.SelectedAnnotations.Count > 1 ? $"{_vm.SelectedAnnotations.Count} annotations" : _vm.PrimaryAnnotation?.Kind ?? ToolCatalog.Get(_tool()).Name + " defaults")
                : image ? (_vm.SelectedImages.Count > 1 ? $"{_vm.SelectedImages.Count} images" : "Image " + (_vm.Images.ToList().FindIndex(i => i.Id == _vm.PrimaryImage) + 1) + " of " + _vm.Images.Count) : "Document";
            _auto.IsChecked = _vm.Document.AutoCanvas; _locked.IsChecked = !_vm.Document.AutoCanvas;
            if (!_width.Input.IsKeyboardFocusWithin) _width.Value = _vm.Document.ExportArea.Width;
            if (!_height.Input.IsKeyboardFocusWithin) _height.Value = _vm.Document.ExportArea.Height;
            AnnotationPanel.Refresh(); EdgePanel.Refresh();
            foreach (var b in _align) b.IsEnabled = _vm.SelectedAnnotations.Count >= 2;
            foreach (var b in _distribute) b.IsEnabled = _vm.SelectedAnnotations.Count >= 3;
            bool secure = _vm.SelectedAnnotations.Count > 0 ? _vm.SelectedAnnotationObjects.All(a => a is RedactionAnnotation) : !_vm.HasSelection && _tool() == ToolKind.Redaction;
            bool visual = _vm.SelectedImages.Count > 0 ? _vm.SelectedLayer?.Effects.Length > 0 : !_vm.HasSelection && _tool() is ToolKind.Blur or ToolKind.Pixelate;
            _privacy.Visibility = secure || visual ? Visibility.Visible : Visibility.Collapsed;
            _privacy.Kind = secure ? NotificationKind.Success : NotificationKind.Warning;
            _privacy.Title = secure ? "Secure redaction" : "Visual effect only";
            _privacy.Message = secure ? "Solid fill. Exported images never show what is underneath." : "Use Redact to hide information securely.";
        }
        finally { _sync = false; }
    }

    private void SetCanvasSize()
    {
        if (_sync) return;
        var area = _vm.Document.ExportArea; int w = (int)_width.Value, h = (int)_height.Value;
        if (w == area.Width && h == area.Height) return;
        _vm.SetCanvas(new PixelRect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }
    private InspectorSection Section(Panel parent, string title, bool expanded = true)
    {
        var section = new InspectorSection(_services, title, title, expanded); parent.Children.Add(section); return section;
    }
    private static void AddNumber(Panel panel, string label, string property, double min, double max, string? tip = null)
    {
        var box = new NumberBox { Label = label, Minimum = min, Maximum = max, ToolTip = tip };
        box.SetBinding(NumberBox.ValueProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        box.Committed += _ => box.GetBindingExpression(NumberBox.ValueProperty)?.UpdateSource(); panel.Children.Add(box);
    }
    private static void AddCheck(Panel panel, string label, string property)
    {
        var box = new CheckBox { Content = label, Margin = new Thickness(0, 4, 0, 4) }; box.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay }); panel.Children.Add(box);
    }
    private static void AddRow(Panel panel, string label, FrameworkElement control)
    {
        AutomationProperties.SetName(control, label);
        // Same 88 DIP label column as NumberBox (80 + 8 gap) and the annotation/edge panels, so every row aligns.
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, ToolTip = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row);
    }
    private static Button AddIcon(Panel panel, string label, string icon, Action run)
    {
        var button = new IconButton { Label = label, Icon = Application.Current?.TryFindResource(icon) as Geometry, ShowLabel = false }; button.Click += (_, _) => run(); panel.Children.Add(button); return button;
    }
    private static void AddButton(Panel panel, string label, Action run) { var b = new Button { Content = label, Margin = new Thickness(0, 4, 0, 4) }; b.Click += (_, _) => run(); panel.Children.Add(b); }
    private static void AddMenu(ContextMenu menu, string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
}
