using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Editor;

public sealed class StyleGallery : UserControl
{
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly Func<Annotation?> _sample;
    private readonly Action _refreshProperties;
    public const string StyleIdFormat = "SnagItOpen.Style", StyleKindFormat = "SnagItOpen.StyleKind";
    private readonly WrapPanel _tiles = new();
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    private GalleryEntry? _pendingApply; private bool _mouseClick, _suppressNextClick;
    private readonly StyleThumbnailRenderer _renderer = new();
    private readonly List<(string Id, Button Button)> _buttons = [];
    private string _kind = "";
    private Point? _dragStart;
    private string? _dragId;
    private ThemeService? _theme;
    private string _shape = "";
    public int TileCount => _buttons.Count;
    public StyleGallery(AppServices services, EditorViewModel vm, Func<Annotation?> sample, Action refreshProperties)
    {
        _services = services; _vm = vm; _sample = sample; _refreshProperties = refreshProperties;
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); if (_pendingApply is { } entry) { _pendingApply = null; Apply(entry); } };
        Content = _tiles; Margin = new Thickness(0, 4, 0, 12); AutomationProperties.SetName(this, "Style gallery");
        Loaded += (_, _) => { _services.AnnotationStyles.Changed += OnStylesChanged; _theme = ThemeService.Current; if (_theme is not null) _theme.ThemeChanged += OnThemeChanged; Refresh(_kind, true); };
        Unloaded += (_, _) => { _services.AnnotationStyles.Changed -= OnStylesChanged; if (_theme is not null) _theme.ThemeChanged -= OnThemeChanged; _theme = null; };
    }
    private void OnStylesChanged() { Refresh(_kind); _refreshProperties(); }
    private void OnThemeChanged(object? sender, EventArgs e) => Refresh(_kind, true);
    public void Refresh(string kind, bool force = false)
    {
        _kind = kind;
        var entries = _services.AnnotationStyles.GalleryFor(kind);
        string shape = kind + ":" + string.Join("|", entries.Select(e => e.Id + ":" + e.Name + ":" + e.Style.GetHashCode()));
        if (!force && shape == _shape) { UpdateSelected(entries); return; }
        string? focused = _buttons.FirstOrDefault(b => b.Button.IsKeyboardFocusWithin).Id;
        _shape = shape; _tiles.Children.Clear(); _buttons.Clear();
        foreach (var entry in entries)
        {
            var button = new Button { Width = 64, Height = 44, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0), Tag = entry.Id, AllowDrop = true };
            string description = Describe(entry.Style); AutomationProperties.SetName(button, $"{entry.Name}, {description}"); button.ToolTip = $"{entry.Name}\n{description}";
            if (entry.Style is MagnifierAnnotation) button.Content = ControlsIcon("Magnify");
            else button.Content = new Image { Source = _renderer.Render(entry.Style, ThemeService.Current?.Effective ?? EffectiveTheme.Light, VisualTreeHelper.GetDpi(this).DpiScaleX), Width = 60, Height = 40, Stretch = Stretch.Uniform };
            button.ToolTip = $"{entry.Name}\n{description}\nDouble-click or drag onto the canvas to insert.";
            button.MouseDoubleClick += (_, e) =>
            {
                // The first click of a double-click must not restyle the selection: cancel its deferred apply.
                _clickTimer.Stop(); _pendingApply = null; _suppressNextClick = true;
                _vm.RequestInsertStyle(entry.Style); e.Handled = true;
            };
            button.Click += (_, _) =>
            {
                if (_suppressNextClick) { _suppressNextClick = false; _mouseClick = false; return; } // second click of a double-click
                if (!_mouseClick) { Apply(entry); return; } // keyboard/automation: immediate
                _mouseClick = false; _pendingApply = entry; _clickTimer.Stop(); _clickTimer.Start(); // wait: it may become a double-click
            };
            button.ContextMenu = BuildMenu(entry);
            button.PreviewKeyDown += (_, e) => TileKey(button, entry, e);
            button.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragId = entry.Id; _mouseClick = true; };
            button.PreviewMouseLeftButtonUp += (_, _) => { _dragStart = null; _dragId = null; };
            button.MouseMove += (_, e) =>
            {
                if (_dragStart is not { } start || _dragId != entry.Id || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - start).Length < 5) return;
                _dragStart = null;
                var data = new DataObject(StyleIdFormat, entry.Id); data.SetData(StyleKindFormat, _kind);
                DragDrop.DoDragDrop(button, data, DragDropEffects.Copy | DragDropEffects.Move); // Move = reorder here, Copy = insert on the canvas
            };
            button.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("SnagItOpen.Style") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            button.Drop += (_, e) =>
            {
                if (e.Data.GetData("SnagItOpen.Style") is not string id) return;
                int index = _services.AnnotationStyles.GalleryFor(_kind, true).ToList().FindIndex(item => item.Id == entry.Id); _services.AnnotationStyles.MoveGallery(_kind, id, index); e.Handled = true;
            };
            _buttons.Add((entry.Id, button)); _tiles.Children.Add(button);
        }
        var add = new IconButton { Label = "Save current style", ShowLabel = false, IsSubtle = false, Width = 64, Height = 44, Margin = new Thickness(0, 0, 4, 4), Icon = Geometry.Parse("M8,3 L8,13 M3,8 L13,8") }; add.Click += (_, _) => SaveCurrent(); _tiles.Children.Add(add);
        UpdateSelected(entries); if (focused is not null) _buttons.FirstOrDefault(b => b.Id == focused).Button?.Focus();
    }
    private FrameworkElement ControlsIcon(string name) => ControlVisuals.Icon(TryFindResource("Icon." + name) as Geometry, this, 20);
    private void UpdateSelected(IReadOnlyList<GalleryEntry> entries)
    {
        var sample = _sample(); foreach (var (id, button) in _buttons)
        {
            var entry = entries.FirstOrDefault(e => e.Id == id); bool selected = entry is not null && sample is not null && SameStyle(entry.Style, sample);
            button.SetResourceReference(BorderBrushProperty, selected ? "Accent.Select" : "Stroke.Control");
        }
    }
    private static bool SameStyle(Annotation left, Annotation right) => (AnnotationStyle.Transfer(left, right) with { Hidden = right.Hidden, Name = right.Name }) == right;
    private static string Describe(Annotation style) => $"{style.Kind}, {style.Color.ToHex()}, {style.StrokeWidth:0.#} px";
    public void Apply(GalleryEntry entry)
    {
        if (_vm.SelectedAnnotations.Count > 0) _vm.UpdateSelectedAnnotations(a => ApplyStyle(entry.Style, a), $"Style {entry.Name}");
        else _services.AnnotationStyles.SetPrototype(_kind, entry.Style);
        _vm.Status = $"Style applied: {entry.Name}."; _vm.Notify(NotificationKind.Success, "Style applied", entry.Name); _refreshProperties();
    }
    public static Annotation ApplyStyle(Annotation style, Annotation target)
    {
        var result = AnnotationStyle.Transfer(style, target) with { Hidden = target.Hidden, Name = target.Name };
        if (style is LineAnnotation { Control: not null } && result is LineAnnotation line)
        {
            var mid = (line.Start + line.End) * 0.5; var delta = line.End - line.Start;
            var normal = delta.Length < 0.0001 ? new SnagItOpen.Core.Geometry.PointD(0, -1) : new SnagItOpen.Core.Geometry.PointD(delta.Y / delta.Length, -delta.X / delta.Length);
            return line with { Control = mid + normal * (Math.Max(20, delta.Length) * 0.4) };
        }
        return result is TextAnnotation text && !string.IsNullOrEmpty(text.Text) ? SnagItOpen.Imaging.Rendering.AnnotationRenderer.Fit(text) : result;
    }
    private void SaveCurrent()
    {
        if (_sample() is not { } sample) return;
        var name = Dialogs.Prompt(Window.GetWindow(this), "Save style", "Style name:", $"{_kind} {_services.AnnotationStyles.GalleryFor(_kind).Count(e => !e.BuiltIn) + 1}");
        if (name is null) return;
        Try(() => _services.AnnotationStyles.SaveGallery(_kind, name, sample)); Refresh(_kind);
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _vm.Status = ex.Message; }
    }
    private ContextMenu BuildMenu(GalleryEntry entry)
    {
        var menu = new ContextMenu();
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => { Try(action); Refresh(_kind); }; menu.Items.Add(item); }
        Add("Insert on canvas", () => _vm.RequestInsertStyle(entry.Style));
        Add("Apply to selection", () => Apply(entry)); Add("Set as tool default", () => _services.AnnotationStyles.SetPrototype(_kind, entry.Style));
        if (!entry.BuiltIn) Add("Update from selection", () =>
        {
            if (_vm.PrimaryAnnotation is not { } selected || selected.Kind != _kind) { _vm.Status = $"Select one {_kind.ToLowerInvariant()} whose look should replace “{entry.Name}”."; return; }
            if (System.Windows.MessageBox.Show(Window.GetWindow(this), $"Replace the saved style “{entry.Name}” with the selected {_kind.ToLowerInvariant()}'s look?", "Update style", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            if (_services.AnnotationStyles.UpdateGalleryStyle(_kind, entry.Id, selected)) _vm.Status = $"Style “{entry.Name}” updated.";
        });
        if (!entry.BuiltIn) Add("Rename…", () => { string? name = Dialogs.Prompt(Window.GetWindow(this), "Rename style", "Style name:", entry.Name); if (name is not null) _services.AnnotationStyles.RenameGallery(_kind, entry.Id, name); });
        Add("Duplicate", () => _services.AnnotationStyles.DuplicateGallery(_kind, entry.Id)); Add("Move left", () => _services.AnnotationStyles.MoveGalleryBy(_kind, entry.Id, -1)); Add("Move right", () => _services.AnnotationStyles.MoveGalleryBy(_kind, entry.Id, 1));
        Add(entry.BuiltIn ? "Hide" : "Delete", () => _services.AnnotationStyles.DeleteGallery(_kind, entry.Id)); Add("Restore built-in styles", () => _services.AnnotationStyles.RestoreBuiltIns(_kind)); return menu;
    }
    private void TileKey(Button button, GalleryEntry entry, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Enter) { _vm.RequestInsertStyle(entry.Style); e.Handled = true; return; }
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        int direction = key is Key.Left or Key.Up ? -1 : 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right) { _services.AnnotationStyles.MoveGalleryBy(_kind, entry.Id, direction); Refresh(_kind); _buttons.FirstOrDefault(b => b.Id == entry.Id).Button?.Focus(); }
        else
        {
            int columns = Math.Max(1, (int)(_tiles.ActualWidth / 60)); int delta = key is Key.Up or Key.Down ? direction * columns : direction;
            int index = _buttons.FindIndex(b => b.Button == button); if (_buttons.Count > 0) _buttons[Math.Clamp(index + delta, 0, _buttons.Count - 1)].Button.Focus();
        }
        e.Handled = true;
    }
}
