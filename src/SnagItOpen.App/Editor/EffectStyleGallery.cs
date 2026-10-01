using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Editor;

/// <summary>Static-icon presets for effects whose appearance depends on image pixels.</summary>
public sealed class EffectStyleGallery : UserControl
{
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly Action _refreshProperties;
    private readonly WrapPanel _tiles = new();
    private readonly List<(string Id, Button Button)> _buttons = [];
    private string _kind = "Blur", _shape = "";
    private Point? _dragStart;
    private string? _dragId;
    public int TileCount => _buttons.Count;
    public IReadOnlyList<Button> Tiles => _buttons.Select(item => item.Button).ToArray();

    public EffectStyleGallery(AppServices services, EditorViewModel vm, Action refreshProperties)
    {
        _services = services; _vm = vm; _refreshProperties = refreshProperties;
        Content = _tiles; Margin = new Thickness(0, 4, 0, 12); AutomationProperties.SetName(this, "Effect style gallery");
        Loaded += (_, _) => { _services.ToolStyles.Changed += OnStylesChanged; Refresh(_kind); };
        Unloaded += (_, _) => _services.ToolStyles.Changed -= OnStylesChanged;
    }
    private void OnStylesChanged() { Refresh(_kind); _refreshProperties(); }
    public void Refresh(ToolKind tool) => Refresh(tool.ToString());
    public void Refresh(string kind)
    {
        _kind = kind;
        var entries = _services.ToolStyles.EffectGalleryFor(kind);
        string shape = kind + ":" + string.Join("|", entries.Select(entry => entry.Id + ":" + entry.Name + ":" + entry.Style.EffectStrength));
        if (_shape == shape) { UpdateSelected(entries); return; }
        string? focused = _buttons.FirstOrDefault(item => item.Button.IsKeyboardFocusWithin).Id;
        _shape = shape; _tiles.Children.Clear(); _buttons.Clear();
        foreach (var entry in entries)
        {
            string description = $"{kind}, {(kind == "Blur" ? "radius" : "block size")} {entry.Style.EffectStrength} px";
            var button = new Button { Width = 56, Height = 40, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0), Tag = entry.Id, AllowDrop = true, ToolTip = entry.Name + "\n" + description };
            AutomationProperties.SetName(button, entry.Name + ", " + description);
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = ControlVisuals.Icon(null, button, 18); icon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "Icon." + kind); content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = entry.Style.EffectStrength.ToString(), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }); button.Content = content;
            button.Click += (_, _) => Apply(entry); button.ContextMenu = BuildMenu(entry); button.PreviewKeyDown += (_, e) => TileKey(button, entry, e);
            button.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragId = entry.Id; };
            button.PreviewMouseLeftButtonUp += (_, _) => { _dragStart = null; _dragId = null; };
            button.MouseMove += (_, e) =>
            {
                if (_dragStart is not { } start || _dragId != entry.Id || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - start).Length < 5) return;
                _dragStart = null; DragDrop.DoDragDrop(button, new DataObject("SnagItOpen.EffectStyle", entry.Id), DragDropEffects.Move);
            };
            button.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("SnagItOpen.EffectStyle") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            button.Drop += (_, e) =>
            {
                if (e.Data.GetData("SnagItOpen.EffectStyle") is not string id) return;
                int index = _services.ToolStyles.EffectGalleryFor(_kind, true).ToList().FindIndex(item => item.Id == entry.Id);
                _services.ToolStyles.MoveEffectGallery(_kind, id, index); Refresh(_kind); e.Handled = true;
            };
            _buttons.Add((entry.Id, button)); _tiles.Children.Add(button);
        }
        var add = new IconButton { Label = "Save current effect style", ShowLabel = false, Width = 56, Height = 40, Margin = new Thickness(0, 0, 4, 4) };
        add.SetResourceReference(IconButton.IconProperty, "Icon.Plus"); add.Click += (_, _) => SaveCurrent(); _tiles.Children.Add(add);
        UpdateSelected(entries); if (focused is not null) _buttons.FirstOrDefault(item => item.Id == focused).Button?.Focus();
    }
    private void UpdateSelected(IReadOnlyList<EffectGalleryEntry> entries)
    {
        int strength = BuiltInEffectStyles.ClampStrength(_kind, _services.ToolStyles.Get(_kind).EffectStrength);
        foreach (var (id, button) in _buttons)
            button.SetResourceReference(BorderBrushProperty, entries.FirstOrDefault(entry => entry.Id == id)?.Style.EffectStrength == strength ? "Accent.Select" : "Stroke.Control");
    }
    public void Apply(EffectGalleryEntry entry)
    {
        _services.ToolStyles.Set(_kind, _services.ToolStyles.Get(_kind) with { EffectStrength = BuiltInEffectStyles.ClampStrength(_kind, entry.Style.EffectStrength) });
        _vm.Status = $"{_kind} default: {entry.Name}."; _vm.Notify(NotificationKind.Success, "Style applied", entry.Name);
        Refresh(_kind); _refreshProperties();
    }
    private void SaveCurrent()
    {
        string? name = Dialogs.Prompt(Window.GetWindow(this), "Save style", "Style name:", $"{_kind} {_services.ToolStyles.EffectGalleryFor(_kind).Count(entry => !entry.BuiltIn) + 1}");
        if (name is not null) Try(() => _services.ToolStyles.SaveEffectGallery(_kind, name, _services.ToolStyles.Get(_kind)));
        Refresh(_kind);
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _vm.Status = ex.Message; }
    }
    private ContextMenu BuildMenu(EffectGalleryEntry entry)
    {
        var menu = new ContextMenu();
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => { Try(action); Refresh(_kind); }; menu.Items.Add(item); }
        Add("Apply", () => Apply(entry)); Add("Set as tool default", () => Apply(entry));
        if (!entry.BuiltIn) Add("Rename…", () => { string? name = Dialogs.Prompt(Window.GetWindow(this), "Rename style", "Style name:", entry.Name); if (name is not null) _services.ToolStyles.RenameEffectGallery(_kind, entry.Id, name); });
        Add("Duplicate", () => _services.ToolStyles.DuplicateEffectGallery(_kind, entry.Id)); Add("Move left", () => _services.ToolStyles.MoveEffectGalleryBy(_kind, entry.Id, -1)); Add("Move right", () => _services.ToolStyles.MoveEffectGalleryBy(_kind, entry.Id, 1));
        Add(entry.BuiltIn ? "Hide" : "Delete", () => _services.ToolStyles.DeleteEffectGallery(_kind, entry.Id)); Add("Restore built-in styles", () => _services.ToolStyles.RestoreBuiltInEffects(_kind)); return menu;
    }
    private void TileKey(Button button, EffectGalleryEntry entry, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        int direction = key is Key.Left or Key.Up ? -1 : 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right)
        {
            _services.ToolStyles.MoveEffectGalleryBy(_kind, entry.Id, direction); Refresh(_kind); _buttons.FirstOrDefault(item => item.Id == entry.Id).Button?.Focus();
        }
        else
        {
            int columns = Math.Max(1, (int)(_tiles.ActualWidth / 60)); int delta = key is Key.Up or Key.Down ? direction * columns : direction;
            int index = _buttons.FindIndex(item => item.Button == button); if (_buttons.Count > 0) _buttons[Math.Clamp(index + delta, 0, _buttons.Count - 1)].Button.Focus();
        }
        e.Handled = true;
    }
}
