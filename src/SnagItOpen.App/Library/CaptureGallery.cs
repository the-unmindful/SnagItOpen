using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents;
using SnagItOpen.Storage.History;

namespace SnagItOpen.App.Library;

/// <summary>
/// Drag payload for recent captures. Carries asset IDs (for dropping into the editor) plus the PNG
/// files as a file drop list, so captures can also be dragged into other applications.
/// </summary>
public static class CaptureDrag
{
    public const string Format = "SnagItOpen.CaptureAssets";

    public static DataObject Create(IEnumerable<CaptureEntry> entries, Func<string, string> pathFor)
    {
        var list = entries.ToList();
        var data = new DataObject();
        data.SetData(Format, string.Join(";", list.Select(e => $"{e.AssetId}|{e.Width}|{e.Height}")));
        var files = new StringCollection();
        foreach (var e in list)
        {
            var p = pathFor(e.AssetId);
            if (File.Exists(p)) files.Add(p);
        }
        if (files.Count > 0) data.SetFileDropList(files);
        return data;
    }

    /// <summary>Parses and validates a payload; null when the data is not a capture drag.</summary>
    public static IReadOnlyList<ImageAsset>? Read(IDataObject data)
    {
        if (!data.GetDataPresent(Format) || data.GetData(Format) is not string s) return null;
        var list = new List<ImageAsset>();
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split('|');
            if (f.Length == 3 && DocumentValidator.IsHash(f[0]) && int.TryParse(f[1], out var w) && int.TryParse(f[2], out var h)
                && Limits.IsAcceptableImageSize(w, h))
                list.Add(ImageAsset.Create(f[0], w, h));
        }
        return list;
    }

    internal static BitmapSource? LoadThumbnail(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException) { return null; }
    }
}

/// <summary>
/// Makes a capture list draggable. Dragging an already-selected item drags the whole selection (in
/// selection order); clicking it without dragging selects just that item.
/// </summary>
internal sealed class CaptureListDrag
{
    private readonly ListBox _list;
    private readonly AppServices _services;
    private readonly Func<object, CaptureEntry?> _entryOf;
    private Point _start;
    private ListBoxItem? _downItem;
    private bool _deferSelect;

    public CaptureListDrag(ListBox list, AppServices services, Func<object, CaptureEntry?> entryOf)
    {
        _list = list;
        _services = services;
        _entryOf = entryOf;
        list.PreviewMouseLeftButtonDown += OnDown;
        list.PreviewMouseMove += OnMove;
        list.PreviewMouseLeftButtonUp += OnUp;
    }

    private ListBoxItem? ItemAt(object source) =>
        source is Visual v ? ItemsControl.ContainerFromElement(_list, v) as ListBoxItem : null;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_list);
        _downItem = ItemAt(e.OriginalSource);
        _deferSelect = false;
        // Keep a multi-selection intact so it can be dragged as a group.
        if (e.ClickCount == 1 && _downItem is { IsSelected: true } && Keyboard.Modifiers == ModifierKeys.None && _list.SelectedItems.Count > 1)
        {
            _deferSelect = true;
            e.Handled = true;
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _downItem is null) return;
        var p = e.GetPosition(_list);
        if (Math.Abs(p.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _downItem;
        _downItem = null;
        _deferSelect = false;
        var entries = item.IsSelected
            ? _list.SelectedItems.Cast<object>().Select(_entryOf).OfType<CaptureEntry>().ToList()
            : [.. new[] { _entryOf(item.DataContext) }.OfType<CaptureEntry>()];
        if (entries.Count == 0) return;
        DragDrop.DoDragDrop(_list, CaptureDrag.Create(entries, _services.Assets.PathFor), DragDropEffects.Copy);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && _downItem is not null)
        {
            _list.SelectedItems.Clear();
            _downItem.IsSelected = true;
            _downItem.Focus();
        }
        _deferSelect = false;
        _downItem = null;
    }
}

/// <summary>
/// Horizontal strip of recent captures under the canvas. Drag thumbnails onto the canvas (Free mode
/// places them where dropped; Vertical/Horizontal append them), or double-click / press Enter to add.
/// </summary>
internal sealed class CaptureGallery : DockPanel
{
    private sealed record Item(CaptureEntry Entry, BitmapSource? Thumb)
    {
        public string Size => $"{Entry.Width} × {Entry.Height}";
        public string Tip => $"{Entry.DisplayName}\n{Entry.Width} × {Entry.Height} · {Entry.CapturedAt:g}\nDrag onto the canvas or double-click to add.";
        public string AccessibleName => $"{Entry.DisplayName}, {Entry.Width} by {Entry.Height}{(Entry.Pinned ? ", pinned" : "")}";
    }

    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private readonly TextBlock _count;
    private readonly Dictionary<Guid, BitmapSource?> _thumbs = [];
    private readonly Grid _body;
    private readonly Button _collapse;

    public CaptureGallery(AppServices services, EditorViewModel vm, Action openLibrary)
    {
        _services = services;
        _vm = vm;
        Height = 132;
        LastChildFill = true;

        SetResourceReference(BackgroundProperty, "Bg.SurfaceAlt");
        var header = new DockPanel { Margin = new Thickness(6, 0, 6, 0), Height = 28 };
        _collapse = new Button { Content = "▾", Padding = new Thickness(4, 0, 4, 0), MinHeight = 22, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Collapse recent captures" };
        AutomationProperties.SetName(_collapse, "Collapse recent captures");
        _collapse.Click += (_, _) => SetCollapsed(!_services.UiState.CaptureGalleryCollapsed);
        DockPanel.SetDock(_collapse, Dock.Left); header.Children.Add(_collapse);
        var manage = new Button { Content = "Open library", Padding = new Thickness(8, 0, 8, 0), MinHeight = 22, ToolTip = "Open library (Ctrl+L)" };
        AutomationProperties.SetName(manage, "Open capture library"); AutomationProperties.SetAcceleratorKey(manage, "Ctrl+L");
        manage.Click += (_, _) => openLibrary();
        DockPanel.SetDock(manage, Dock.Right);
        _count = new TextBlock { Margin = new Thickness(0, 0, 8, 0), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_count, Dock.Right);
        header.Children.Add(manage);
        header.Children.Add(_count);
        header.Children.Add(new TextBlock
        {
            Text = "Recent captures",
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        SetDock(header, Dock.Top);
        Children.Add(header);

        _list = new ListBox { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0) };
        _list.SetResourceReference(Control.BackgroundProperty, "Bg.Surface");
        AutomationProperties.SetName(_list, "Recent captures");
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
        _list.ItemsPanel = new ItemsPanelTemplate(panel);
        _list.ItemTemplate = BuildTemplate();
        var itemStyle = new Style(typeof(ListBoxItem), TryFindResource(typeof(ListBoxItem)) as Style);
        itemStyle.Setters.Add(new Setter(FocusVisualStyleProperty, TryFindResource("FocusRing")));
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(Item.AccessibleName))));
        itemStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(Item.Tip))));
        _list.ItemContainerStyle = itemStyle;
        _list.MouseDoubleClick += (_, e) => { if (e.ChangedButton == MouseButton.Left) AddSelected(); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddSelected(); e.Handled = true; } };
        _list.PreviewMouseWheel += OnWheel;
        _ = new CaptureListDrag(_list, services, o => (o as Item)?.Entry);
        _list.ContextMenu = BuildContextMenu();
        _list.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Visual v && ItemsControl.ContainerFromElement(_list, v) is ListBoxItem item && !item.IsSelected)
            { _list.SelectedItems.Clear(); item.IsSelected = true; item.Focus(); }
        };

        _empty = new TextBlock
        {
            Text = "No captures yet. Capture a region to add it here.",
            Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 0),
        };
        _body = new Grid();
        _body.Children.Add(_list);
        _body.Children.Add(_empty);
        Children.Add(_body);
        SetCollapsed(services.UiState.CaptureGalleryCollapsed, persist: false);

        Loaded += (_, _) => { _services.History.Changed += OnHistoryChanged; Refresh(); };
        Unloaded += (_, _) => _services.History.Changed -= OnHistoryChanged;
    }

    private static DataTemplate BuildTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(MarginProperty, new Thickness(2));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.WidthProperty, 112.0);
        border.SetValue(Border.HeightProperty, 70.0);
        border.SetResourceReference(Border.BackgroundProperty, "Checker.B");
        border.SetResourceReference(Border.BorderBrushProperty, "Stroke.Divider");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new Binding(nameof(Item.Thumb)));
        img.SetValue(Image.StretchProperty, Stretch.Uniform);
        border.AppendChild(img);
        root.AppendChild(border);
        var size = new FrameworkElementFactory(typeof(TextBlock));
        size.SetBinding(TextBlock.TextProperty, new Binding(nameof(Item.Size)));
        size.SetValue(TextBlock.FontSizeProperty, 11.0);
        size.SetValue(OpacityProperty, 0.7);
        size.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        size.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        root.AppendChild(size);
        return new DataTemplate { VisualTree = root };
    }

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(Refresh);

    private void SetCollapsed(bool collapsed, bool persist = true)
    {
        if (persist) _services.SaveUiState(_services.UiState with { CaptureGalleryCollapsed = collapsed });
        Height = collapsed ? 28 : 132;
        _body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _collapse.Content = collapsed ? "▸" : "▾";
        AutomationProperties.SetName(_collapse, collapsed ? "Expand recent captures" : "Collapse recent captures");
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        void Add(string label, Action<Window> action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { if (Window.GetWindow(this) is { } owner) action(owner); };
            menu.Items.Add(item);
        }
        IReadOnlyList<CaptureEntry> Selected() => _list.SelectedItems.Cast<Item>().Select(i => i.Entry).ToList();
        Add("Add to composition", _ => AddSelected());
        Add("Open as new", owner => LibraryActions.OpenNew(owner, _services, _vm, Selected()));
        Add("Copy", owner => _ = LibraryActions.CopyAsync(owner, _services, Selected().FirstOrDefault()));
        Add("Pin to screen", owner => LibraryActions.Pin(owner, _services, _vm, Selected().FirstOrDefault()));
        Add("Delete", owner => LibraryActions.Delete(owner, _services, _vm, Selected()));
        return menu;
    }

    public void Refresh()
    {
        var entries = _services.History.Entries.Where(e => _services.Assets.Contains(e.AssetId)).ToList();
        var items = entries.Select(e => new Item(e, Thumb(e))).ToList();
        foreach (var gone in _thumbs.Keys.Where(k => entries.All(e => e.Id != k)).ToList()) _thumbs.Remove(gone);
        _list.ItemsSource = items;
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = items.Count == 0 ? "" : items.Count == 1 ? "1 capture" : $"{items.Count} captures";
    }

    private BitmapSource? Thumb(CaptureEntry e)
    {
        if (_thumbs.TryGetValue(e.Id, out var t)) return t;
        return _thumbs[e.Id] = CaptureDrag.LoadThumbnail(_services.History.ThumbnailPath(e));
    }

    private void AddSelected()
    {
        var assets = _list.SelectedItems.Cast<Item>()
            .Where(i => _services.Assets.Contains(i.Entry.AssetId))
            .Select(i => ImageAsset.Create(i.Entry.AssetId, i.Entry.Width, i.Entry.Height)).ToList();
        if (assets.Count > 0) _vm.AddLibraryAssets(assets);
    }

    /// <summary>The strip scrolls sideways; map the vertical wheel to horizontal scrolling.</summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer(_list) is not { } sv) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}
