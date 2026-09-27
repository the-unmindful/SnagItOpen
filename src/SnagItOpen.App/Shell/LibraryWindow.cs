using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Documents;
using SnagItOpen.Storage.History;

namespace SnagItOpen.App.Shell;

/// <summary>Recent captures: combine selected, rename, pin, delete, reveal file. Shows storage usage.</summary>
internal sealed class LibraryWindow : Window
{
    private sealed record Row(CaptureEntry Entry, BitmapSource? Thumb)
    {
        public string Title => (Entry.Pinned ? "📌 " : "") + Entry.DisplayName;
        public string Info => $"{Entry.Width} × {Entry.Height} · {Entry.CapturedAt:g}";
        public string AccessibleName => $"{Entry.DisplayName}, {Entry.Width} by {Entry.Height}{(Entry.Pinned ? ", pinned" : "")}";
    }

    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly ListBox _list;
    private readonly TextBlock _usage;

    public LibraryWindow(AppServices services, EditorViewModel vm)
    {
        _services = services;
        _vm = vm;
        Title = "Recent captures - SnagItOpen";
        Width = 520;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox { SelectionMode = SelectionMode.Extended, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_list, "Recent captures");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemTemplate = BuildTemplate();
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(Row.AccessibleName))));
        _list.ItemContainerStyle = itemStyle;
        _list.MouseDoubleClick += (_, _) => AddSelected();
        _ = new Library.CaptureListDrag(_list, services, o => (o as Row)?.Entry);

        _usage = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Opacity = 0.75 };

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Btn("Add to composition", AddSelected, "Adds the selected captures in the order you selected them"));
        buttons.Children.Add(Btn("Rename…", RenameSelected));
        buttons.Children.Add(Btn("Pin / unpin", TogglePin, "Pinned captures are never removed automatically"));
        buttons.Children.Add(Btn("Delete", DeleteSelected));
        buttons.Children.Add(Btn("Show file", RevealSelected));
        buttons.Children.Add(Btn("Close", Close));

        var root = new DockPanel { Margin = new Thickness(10) };
        var hint = new TextBlock
        {
            Text = "Drag captures onto the editor canvas, or select them (Ctrl/Shift+click) and choose Add. They are added in selection order.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(_usage, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(_usage);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;

        _services.History.Changed += OnHistoryChanged;
        Refresh();
    }

    private static Button Btn(string text, Action a, string? tip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3), ToolTip = tip };
        b.Click += (_, _) => a();
        return b;
    }

    private static DataTemplate BuildTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(DockPanel));
        grid.SetValue(MarginProperty, new Thickness(2));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.WidthProperty, 96.0);
        border.SetValue(Border.HeightProperty, 64.0);
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Row.Thumb)));
        img.SetValue(Image.StretchProperty, Stretch.Uniform);
        border.AppendChild(img);
        grid.AppendChild(border);
        var texts = new FrameworkElementFactory(typeof(StackPanel));
        texts.SetValue(MarginProperty, new Thickness(8, 0, 0, 0));
        texts.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var t1 = new FrameworkElementFactory(typeof(TextBlock));
        t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Title)));
        t1.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var t2 = new FrameworkElementFactory(typeof(TextBlock));
        t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Info)));
        t2.SetValue(OpacityProperty, 0.7);
        texts.AppendChild(t1);
        texts.AppendChild(t2);
        grid.AppendChild(texts);
        return new DataTemplate { VisualTree = grid };
    }

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var selected = _list.SelectedItems.Cast<Row>().Select(r => r.Entry.Id).ToHashSet();
        var rows = _services.History.Entries.Select(e => new Row(e, LoadThumb(e))).ToList();
        _list.ItemsSource = rows;
        foreach (var r in rows) if (selected.Contains(r.Entry.Id)) _list.SelectedItems.Add(r);
        double mb = _services.History.TotalBytes / (1024.0 * 1024.0);
        var s = _services.Settings;
        _usage.Text = $"{rows.Count} captures · {mb:0.0} MiB used · automatic cleanup above {s.HistoryMaxCount} captures or {s.HistoryMaxMegabytes} MiB (pinned captures are kept).";
    }

    private BitmapSource? LoadThumb(CaptureEntry e)
    {
        var p = _services.History.ThumbnailPath(e);
        if (p is null || !File.Exists(p)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(p);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    private List<CaptureEntry> Selected() => _list.SelectedItems.Cast<Row>().Select(r => r.Entry).ToList();

    private void AddSelected()
    {
        var sel = Selected().Where(e => _services.Assets.Contains(e.AssetId)).ToList();
        if (sel.Count == 0) { Dialogs.Info(this, "Select one or more captures first."); return; }
        _vm.AddLibraryAssets(sel.Select(e => ImageAsset.Create(e.AssetId, e.Width, e.Height)).ToList());
    }

    private void RenameSelected()
    {
        if (Selected() is not [var e]) { Dialogs.Info(this, "Select exactly one capture to rename."); return; }
        var name = Dialogs.Prompt(this, "Rename capture", "Name:", e.Name ?? e.DisplayName);
        if (name is not null) _services.History.Rename(e.Id, name);
    }

    private void TogglePin()
    {
        foreach (var e in Selected()) _services.History.SetPinned(e.Id, !e.Pinned);
    }

    private void DeleteSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"Delete {sel.Count} capture(s) from the library? Images used by the open composition, its undo history or recovery drafts are kept.",
                "SnagItOpen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var protect = _vm.ProtectedAssets();
        protect.UnionWith(_services.Recovery.ReferencedAssets());
        _services.History.Delete(sel.Select(e => e.Id).ToArray(), protect);
    }

    private void RevealSelected()
    {
        if (Selected() is not [var e, ..]) return;
        var path = _services.Assets.PathFor(e.AssetId);
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _services.History.Changed -= OnHistoryChanged;
        base.OnClosed(e);
    }
}
