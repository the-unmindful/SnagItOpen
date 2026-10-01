using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Library;
using SnagItOpen.Core.Capture;
using SnagItOpen.Storage.History;

namespace SnagItOpen.App.Shell;

internal sealed class LibraryWindow : Window
{
    private sealed record Row(CaptureEntry Entry, BitmapSource? Thumb)
    {
        public string Title => Entry.DisplayName;
        public string Info => $"{Entry.Width} × {Entry.Height} · {Entry.CapturedAt:g}";
        public bool Pinned => Entry.Pinned;
        public string AccessibleName => $"{Title}, {Entry.Width} by {Entry.Height}{(Entry.Pinned ? ", pinned" : "")}";
    }
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBox _search = new() { Width = 210, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _sort = new() { Width = 90, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _usage = new() { Margin = new Thickness(6, 6, 8, 0) };
    private readonly ProgressBar _usageBar = new() { Height = 4, Maximum = 1, Margin = new Thickness(6, 0, 6, 4) };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 240, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly ColumnDefinition _previewColumn = new() { Width = new GridLength(0) };
    private readonly ScrollViewer _previewHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly Dictionary<Guid, BitmapSource?> _thumbs = [];
    private CaptureLibraryFilter _filter;
    private bool _grid;
    private Window? _largePreview;

    public LibraryWindow(AppServices services, EditorViewModel vm, Action? openSettings = null)
    {
        _services = services; _vm = vm; _grid = services.UiState.LibraryView != "List";
        Title = "Capture library - SnagItOpen"; Width = 960; Height = 640; MinWidth = 650; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg.Window"); SetResourceReference(ForegroundProperty, "Text.Primary");
        FontFamily = new FontFamily("Segoe UI");
        var root = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(new TextBlock { Text = "Search", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        AutomationProperties.SetName(_search, "Search captures by name, dimensions or date");
        toolbar.Children.Add(_search);
        _sort.ItemsSource = Enum.GetNames<CaptureLibrarySort>(); _sort.SelectedItem = services.UiState.LibrarySort;
        AutomationProperties.SetName(_sort, "Sort captures"); toolbar.Children.Add(_sort);
        foreach (var (label, filter) in new[] { ("All", CaptureLibraryFilter.All), ("Pinned", CaptureLibraryFilter.Pinned), ("Today", CaptureLibraryFilter.Today), ("This week", CaptureLibraryFilter.ThisWeek) })
        {
            var chip = new RadioButton { Content = label, GroupName = "LibraryFilter", IsChecked = filter == CaptureLibraryFilter.All, Margin = new Thickness(5), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(chip, $"Filter {label}");
            chip.Checked += (_, _) => { _filter = filter; Refresh(); };
            toolbar.Children.Add(chip);
        }
        toolbar.Children.Add(Btn("Grid", () => SetView(true)));
        toolbar.Children.Add(Btn("List", () => SetView(false)));
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var usage = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var settings = Btn("Output & library settings", () => openSettings?.Invoke()); settings.IsEnabled = openSettings is not null;
        DockPanel.SetDock(settings, Dock.Right); usage.Children.Add(settings);
        var storage = new StackPanel(); storage.Children.Add(_usageBar); storage.Children.Add(_usage); usage.Children.Add(storage);
        DockPanel.SetDock(usage, Dock.Bottom); root.Children.Add(usage);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(_previewColumn);
        AutomationProperties.SetName(_list, "Capture library");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SetResourceReference(Control.BackgroundProperty, "Bg.Surface");
        var itemStyle = new Style(typeof(ListBoxItem), TryFindResource(typeof(ListBoxItem)) as Style);
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(Row.AccessibleName))));
        itemStyle.Setters.Add(new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(FocusVisualStyleProperty, TryFindResource("FocusRing")));
        _list.ItemContainerStyle = itemStyle;
        _ = new CaptureListDrag(_list, services, o => (o as Row)?.Entry);
        _list.MouseDoubleClick += (_, _) => AddSelected();
        _list.SelectionChanged += (_, _) => UpdatePreview();
        _list.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Visual visual && ItemsControl.ContainerFromElement(_list, visual) is ListBoxItem item && !item.IsSelected)
            { _list.SelectedItems.Clear(); item.IsSelected = true; item.Focus(); }
        };
        _list.ContextMenu = BuildMenu();
        _list.PreviewKeyDown += OnKeys;
        body.Children.Add(_list);
        var previewPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        previewPanel.Children.Add(_preview); previewPanel.Children.Add(_details);
        foreach (var (label, action) in Actions()) previewPanel.Children.Add(Btn(label, action));
        _previewHost.Content = previewPanel;
        Grid.SetColumn(_previewHost, 1); body.Children.Add(_previewHost);
        root.Children.Add(body); Content = root;
        _usage.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _details.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _search.TextChanged += (_, _) => Refresh();
        _sort.SelectionChanged += (_, _) => { services.SaveUiState(services.UiState with { LibrarySort = _sort.SelectedItem as string ?? "Newest" }); Refresh(); };
        services.History.Changed += OnHistoryChanged;
        SetView(_grid, persist: false);
    }

    private static Button Btn(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4), MinHeight = 28 };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }
    private IReadOnlyList<CaptureEntry> Selected() => _list.SelectedItems.Cast<Row>().Select(r => r.Entry).ToList();
    private CaptureEntry? First() => Selected().FirstOrDefault();
    private void AddSelected() => LibraryActions.Add(_services, _vm, Selected());
    private void Rename()
    {
        if (Selected() is not [var entry]) return;
        string? name = Dialogs.Prompt(this, "Rename capture", "Name", entry.DisplayName);
        if (name is not null) _services.History.Rename(entry.Id, name);
    }
    private IEnumerable<(string Label, Action Action)> Actions()
    {
        yield return ("Add to composition", AddSelected);
        yield return ("Open as new", () => LibraryActions.OpenNew(this, _services, _vm, Selected()));
        yield return ("Copy", () => _ = LibraryActions.CopyAsync(this, _services, First()));
        yield return ("Pin to screen", () => LibraryActions.Pin(this, _services, _vm, First()));
        yield return ("Pin / unpin in library", () => { foreach (var entry in Selected()) _services.History.SetPinned(entry.Id, !entry.Pinned); });
        yield return ("Rename…", Rename);
        yield return ("Show in folder", () => LibraryActions.Reveal(_services, First()));
        yield return ("Delete", () => LibraryActions.Delete(this, _services, _vm, Selected()));
    }
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        foreach (var (label, action) in Actions()) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        return menu;
    }
    private void SetView(bool grid, bool persist = true)
    {
        _grid = grid;
        if (persist) _services.SaveUiState(_services.UiState with { LibraryView = grid ? "Grid" : "List" });
        var panel = new FrameworkElementFactory(grid ? typeof(WrapPanel) : typeof(VirtualizingStackPanel));
        _list.ItemsPanel = new ItemsPanelTemplate(panel);
        _list.ItemTemplate = CardTemplate(grid);
        Refresh();
    }
    private static DataTemplate CardTemplate(bool grid)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, grid ? Orientation.Vertical : Orientation.Horizontal);
        panel.SetValue(MarginProperty, new Thickness(4));
        var imageGrid = new FrameworkElementFactory(typeof(Grid));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.WidthProperty, grid ? 160.0 : 96.0); border.SetValue(Border.HeightProperty, grid ? 110.0 : 64.0);
        border.SetResourceReference(Border.BackgroundProperty, "Checker.B"); border.SetResourceReference(Border.BorderBrushProperty, "Stroke.Divider"); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding(nameof(Row.Thumb))); image.SetValue(Image.StretchProperty, Stretch.Uniform); border.AppendChild(image); imageGrid.AppendChild(border);
        var badge = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path)); badge.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "Icon.Pin"); badge.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent.Select");
        badge.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5); badge.SetValue(WidthProperty, 16.0); badge.SetValue(HeightProperty, 16.0); badge.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right); badge.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        badge.SetBinding(VisibilityProperty, new Binding(nameof(Row.Pinned)) { Converter = new BooleanToVisibilityConverter() }); imageGrid.AppendChild(badge); panel.AppendChild(imageGrid);
        var texts = new FrameworkElementFactory(typeof(StackPanel)); texts.SetValue(MarginProperty, grid ? new Thickness(0, 5, 0, 0) : new Thickness(12, 0, 0, 0));
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Title))); name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary"); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); if (grid) name.SetValue(MaxWidthProperty, 160.0);
        var info = new FrameworkElementFactory(typeof(TextBlock)); info.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Info))); info.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary"); info.SetValue(TextBlock.FontSizeProperty, 11.0); if (grid) { info.SetValue(MaxWidthProperty, 160.0); info.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); }
        texts.AppendChild(name); texts.AppendChild(info); panel.AppendChild(texts);
        return new DataTemplate { VisualTree = panel };
    }
    private void OnHistoryChanged() => Dispatcher.BeginInvoke(Refresh);
    private void Refresh()
    {
        if (_services is null) return;
        var selected = Selected().Select(e => e.Id).ToHashSet();
        var entries = _services.History.Entries.Where(e => _services.Assets.Contains(e.AssetId)).ToDictionary(e => e.Id);
        var metadata = entries.Values.Select(e => new CaptureLibraryMetadata(e.Id, e.DisplayName, e.Width, e.Height, e.CapturedAt, e.Pinned, e.Bytes));
        _ = Enum.TryParse<CaptureLibrarySort>(_sort.SelectedItem as string, out var sort);
        var rows = CaptureLibraryQuery.Apply(metadata, _search.Text, _filter, sort, DateTimeOffset.Now).Select(e => new Row(entries[e.Id], Thumb(entries[e.Id]))).ToList();
        _list.ItemsSource = rows; foreach (var row in rows) if (selected.Contains(row.Entry.Id)) _list.SelectedItems.Add(row);
        foreach (var gone in _thumbs.Keys.Where(id => !entries.ContainsKey(id)).ToArray()) _thumbs.Remove(gone);
        var settings = _services.Settings;
        double mb = _services.History.TotalBytes / (1024.0 * 1024);
        _usage.Text = $"{mb:0.#} MB of {settings.HistoryMaxMegabytes} MB · {entries.Count} of {settings.HistoryMaxCount} captures";
        _usageBar.Value = Math.Clamp(Math.Max(mb / settings.HistoryMaxMegabytes, entries.Count / (double)settings.HistoryMaxCount), 0, 1);
        UpdatePreview();
    }
    private BitmapSource? Thumb(CaptureEntry entry)
    {
        if (_thumbs.TryGetValue(entry.Id, out var bitmap)) return bitmap;
        return _thumbs[entry.Id] = CaptureDrag.LoadThumbnail(_services.History.ThumbnailPath(entry));
    }
    private void UpdatePreview()
    {
        if (Selected() is not [var entry])
        {
            _previewColumn.Width = new GridLength(0); _previewHost.Visibility = Visibility.Collapsed; _preview.Source = null; _details.Text = ""; _largePreview?.Close(); _largePreview = null; return;
        }
        _previewColumn.Width = new GridLength(280);
        _previewHost.Visibility = Visibility.Visible;
        _preview.Source = CaptureDrag.LoadThumbnail(_services.Assets.PathFor(entry.AssetId));
        _details.Text = $"{entry.DisplayName}\n{entry.Width} × {entry.Height} px\n{entry.CapturedAt:g}\n{entry.Bytes / 1024.0:0.#} KB{(entry.Pinned ? "\nPinned in library" : "")}";
    }
    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddSelected();
        else if (e.Key == Key.Delete) LibraryActions.Delete(this, _services, _vm, Selected());
        else if (e.Key == Key.F2) Rename();
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _ = LibraryActions.CopyAsync(this, _services, First());
        else if (e.Key == Key.Space)
        {
            if (_largePreview is not null) { _largePreview.Close(); _largePreview = null; }
            else if (First() is { } entry)
            {
                var window = new Window { Owner = this, Title = entry.DisplayName, Width = 900, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new Image { Source = CaptureDrag.LoadThumbnail(_services.Assets.PathFor(entry.AssetId)), Stretch = Stretch.Uniform, Margin = new Thickness(16) } };
                window.KeyDown += (_, key) => { if (key.Key is Key.Escape or Key.Space) window.Close(); };
                window.Closed += (_, _) => _largePreview = null;
                _largePreview = window; window.Show();
            }
        }
        else return;
        e.Handled = true;
    }
    protected override void OnClosed(EventArgs e)
    {
        _services.History.Changed -= OnHistoryChanged; _largePreview?.Close(); _preview.Source = null; base.OnClosed(e);
    }
}
