using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SnagItOpen.App.Infrastructure;

namespace SnagItOpen.App.Shell;

public sealed record ShortcutEntry(string Category, string Title, string Gesture);

/// <summary>Receives the current command/tool/hotkey inventory from the composition root.</summary>
public sealed class ShortcutsWindow : DialogWindow
{
    private readonly ShortcutEntry[] _entries;
    private readonly StackPanel _groups = new();
    private readonly TextBox _search = new() { MinHeight = 28, Margin = new Thickness(0, 0, 0, 12) };
    public IReadOnlyList<ShortcutEntry> VisibleEntries { get; private set; } = [];
    public int EntryCount => _entries.Length;
    public string SearchText { get => _search.Text; set => _search.Text = value; }
    public ShortcutsWindow(Window? owner, IEnumerable<ShortcutEntry> entries, Action? changeGlobalShortcuts = null)
        : base(owner, "Keyboard shortcuts", new StackPanel(), "Close", null)
    {
        _entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Gesture)).Distinct().ToArray();
        var body = (StackPanel)Body; body.MinWidth = 520;
        AutomationProperties.SetName(_search, "Search keyboard shortcuts"); _search.ToolTip = "Search actions, keys, or groups";
        body.Children.Add(_search); body.Children.Add(new ScrollViewer { Content = _groups, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (changeGlobalShortcuts is not null) { var change = new Button { Content = "Change global shortcuts…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) }; change.Click += (_, _) => changeGlobalShortcuts(); body.Children.Add(change); }
        _search.TextChanged += (_, _) => Refresh(); Loaded += (_, _) => _search.Focus(); Refresh();
    }
    public static IReadOnlyList<ShortcutEntry> Filter(IEnumerable<ShortcutEntry> entries, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return entries.Where(e => terms.All(term => $"{e.Category} {e.Title} {e.Gesture}".Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    private void Refresh()
    {
        VisibleEntries = Filter(_entries, _search.Text); _groups.Children.Clear();
        foreach (var group in VisibleEntries.GroupBy(e => e.Category))
        {
            var heading = new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 6) }; _groups.Children.Add(heading);
            foreach (var entry in group)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var key = new Label { Content = entry.Gesture, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Right }; key.SetResourceReference(StyleProperty, "KeyCap");
                DockPanel.SetDock(key, Dock.Right); row.Children.Add(key); row.Children.Add(new TextBlock { Text = entry.Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap });
                AutomationProperties.SetName(row, $"{entry.Title}: {entry.Gesture}"); _groups.Children.Add(row);
            }
        }
        if (VisibleEntries.Count == 0) _groups.Children.Add(new TextBlock { Text = "No matching shortcuts", Margin = new Thickness(0, 16, 0, 16) });
    }
}
