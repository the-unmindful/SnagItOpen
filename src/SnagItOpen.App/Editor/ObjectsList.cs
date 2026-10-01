using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;

namespace SnagItOpen.App.Editor;

/// <summary>
/// Every annotation as a row, topmost first. Click selects (Ctrl/Shift extend), drag a row to restack,
/// the eye button hides/shows and the lock button locks/unlocks. Each change is one undo step.
/// Annotations always stay above images; this list only orders annotations among themselves.
/// </summary>
internal sealed class ObjectsList : DockPanel
{
    private const string DragFormat = "SnagItOpen.AnnotationRow";

    private sealed record Row(Annotation A, int Index)
    {
        public string IconKey => A switch
        {
            ArrowAnnotation => "Icon.Arrow", LineAnnotation => "Icon.Line", RectangleAnnotation => "Icon.Rectangle", EllipseAnnotation => "Icon.Ellipse",
            CalloutAnnotation => "Icon.Callout", TextAnnotation => "Icon.Text", HighlightAnnotation => "Icon.Highlight", StepAnnotation => "Icon.Step",
            FreehandAnnotation => "Icon.Pen", RedactionAnnotation => "Icon.Redact", MagnifierAnnotation => "Icon.Magnify", StampAnnotation => "Icon.Stamp", _ => "Icon.Select",
        };

        public string Name
        {
            get
            {
                string detail = A switch
                {
                    TextAnnotation t when !string.IsNullOrWhiteSpace(t.Text) => $": {Trim(t.Text)}",
                    StepAnnotation s => $" {s.Label}",
                    StampAnnotation st => st.AssetId is null ? $" ({st.Symbol})" : " (image)",
                    _ => "",
                };
                return A.Kind + detail;
            }
        }

        private static string Trim(string s)
        {
            var line = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return line.Length > 24 ? line[..24] + "…" : line;
        }
    }

    private readonly EditorViewModel _vm;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private bool _syncing;
    private Point _dragStart;
    private Row? _dragRow;

    public ObjectsList(EditorViewModel vm)
    {
        _vm = vm;
        LastChildFill = true;

        var hint = new TextBlock
        {
            Text = "Top draws on top. Drag to restack. Space: visibility. Ctrl+L: lock.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11, Margin = new Thickness(2, 2, 2, 4),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        SetDock(hint, Dock.Top);
        Children.Add(hint);

        _list = new ListBox
        {
            SelectionMode = SelectionMode.Extended, AllowDrop = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "Objects, top to bottom");
        _list.SelectionChanged += OnSelectionChanged;
        _list.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(_list); _dragRow = RowAt(e.OriginalSource); };
        _list.PreviewMouseMove += OnMouseMove;
        _list.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        _list.Drop += OnDrop;
        _list.KeyDown += OnKeyDown;

        _empty = new TextBlock
        {
            Text = "No annotations yet. Draw with the tools above.", Opacity = 0.65, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var body = new Grid();
        body.Children.Add(_list);
        body.Children.Add(_empty);
        Children.Add(body);

        _vm.CanvasInvalidated += RefreshSoon;
        Loaded += (_, _) => Refresh();
    }

    private bool _pending;

    private void RefreshSoon()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.BeginInvoke(() => { _pending = false; Refresh(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Refresh()
    {
        if (!IsLoaded) return;
        var anns = _vm.Document.Annotations;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var focusRow = focused is not null ? RowAt(focused) : null;
        string? focusToggle = (focused as ToggleButton)?.Tag as string;
        var rows = new List<Row>();
        for (int i = anns.Length - 1; i >= 0; i--) rows.Add(new Row(anns[i], i));

        _syncing = true;
        try
        {
            _list.Items.Clear();
            foreach (var r in rows) _list.Items.Add(BuildItem(r));
            foreach (ListBoxItem item in _list.Items)
                if (item.Tag is Row r && _vm.SelectedAnnotations.Contains(r.A.Id)) item.IsSelected = true;
            if (focusRow is not null)
                foreach (ListBoxItem item in _list.Items)
                    if (item.Tag is Row r && r.A.Id == focusRow.A.Id)
                    {
                        var toggle = (item.Content as Grid)?.Children.OfType<ToggleButton>().FirstOrDefault(t => (string?)t.Tag == focusToggle);
                        if (toggle is not null) toggle.Focus(); else item.Focus();
                        break;
                    }
        }
        finally { _syncing = false; }
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ListBoxItem BuildItem(Row r)
    {
        var a = r.A;
        var grid = new Grid { Margin = new Thickness(1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = Icon(r.IconKey);
        var annotationBrush = new SolidColorBrush(Color.FromArgb(255, a.Color.R, a.Color.G, a.Color.B));
        annotationBrush.Freeze();
        glyph.Stroke = annotationBrush;
        var name = new TextBlock
        {
            Text = r.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0), Opacity = a.Hidden ? 0.45 : 1,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        bool redaction = a is RedactionAnnotation;
        var eye = Toggle(a.Hidden ? "Icon.EyeOff" : "Icon.Eye", !a.Hidden, "Visibility", $"Visibility of {r.Name}",
            redaction ? "Redactions are always drawn in exports" : a.Hidden ? "Show" : "Hide",
            () => _vm.Commit(a.Hidden ? "Show" : "Hide", d => DocumentOps.UpdateAnnotations(d, [a.Id], x => x with { Hidden = !x.Hidden })));
        var lck = Toggle(a.Locked ? "Icon.Lock" : "Icon.Unlock", a.Locked, "Lock", $"Lock {r.Name}", a.Locked ? "Unlock" : "Lock",
            () => _vm.Commit(a.Locked ? "Unlock" : "Lock", d => DocumentOps.UpdateAnnotations(d, [a.Id], x => x with { Locked = !x.Locked })));
        lck.Opacity = a.Locked ? 1 : 0.55;

        Grid.SetColumn(name, 1);
        Grid.SetColumn(eye, 2);
        Grid.SetColumn(lck, 3);
        grid.Children.Add(glyph);
        grid.Children.Add(name);
        grid.Children.Add(eye);
        grid.Children.Add(lck);

        var item = new ListBoxItem { Content = grid, Tag = r, Padding = new Thickness(2) };
        AutomationProperties.SetName(item, $"{r.Name}{(a.Hidden ? ", hidden" : "")}{(a.Locked ? ", locked" : "")}");
        item.MouseDoubleClick += (_, _) =>
        {
            if (a is TextAnnotation && Window.GetWindow(this) is Shell.MainWindow mw) mw.EditTextOf(a.Id);
        };
        return item;
    }

    private static Path Icon(string key)
    {
        var icon = new Path
        {
            Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        icon.SetResourceReference(Path.DataProperty, key);
        icon.SetResourceReference(Path.StrokeProperty, "Text.Primary");
        return icon;
    }

    private static ToggleButton Toggle(string icon, bool isChecked, string tag, string name, string tip, Action a)
    {
        var b = new ToggleButton
        {
            Content = Icon(icon), IsChecked = isChecked, Tag = tag, ToolTip = tip, Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(1, 0, 0, 0),
            BorderThickness = new Thickness(0), Focusable = true, MinWidth = 24, MinHeight = 24,
        };
        b.SetResourceReference(Control.BackgroundProperty, "Bg.Surface");
        AutomationProperties.SetName(b, name);
        b.Checked += (_, _) => a();
        b.Unchecked += (_, _) => a();
        b.Click += (_, e) => e.Handled = true;
        return b;
    }

    private static Row? RowAt(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not ListBoxItem) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.Tag as Row;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var ids = _list.SelectedItems.Cast<ListBoxItem>().Select(i => ((Row)i.Tag).A.Id).ToList();
        _vm.Select([], ids);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is null) return;
        var p = e.GetPosition(_list);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop(_list, new DataObject(DragFormat, row.A.Id.ToString()), DragDropEffects.Move);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string s || !Guid.TryParse(s, out var id)) return;
        e.Handled = true;
        int count = _vm.Document.Annotations.Length;
        // Rows are topmost first: dropping on a row puts the item at that row's draw index; empty space = bottom.
        var target = RowAt(e.OriginalSource);
        int index = target?.Index ?? 0;
        if (index < 0 || index >= count) return;
        _vm.Commit("Restack", d => DocumentOps.MoveAnnotationToIndex(d, id, index));
        _vm.Select([], [id]);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { _vm.RemoveSelected(); e.Handled = true; }
        var row = RowAt(e.OriginalSource) ?? (_list.SelectedItem as ListBoxItem)?.Tag as Row;
        if (row is null) return;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not ToggleButton)
        {
            _vm.Commit(row.A.Hidden ? "Show" : "Hide", d => DocumentOps.UpdateAnnotations(d, [row.A.Id], a => a with { Hidden = !a.Hidden }));
            e.Handled = true;
        }
        else if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _vm.Commit(row.A.Locked ? "Unlock" : "Lock", d => DocumentOps.UpdateAnnotations(d, [row.A.Id], a => a with { Locked = !a.Locked }));
            e.Handled = true;
        }
    }
}
