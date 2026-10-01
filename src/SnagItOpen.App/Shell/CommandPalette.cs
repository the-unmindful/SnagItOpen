using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace SnagItOpen.App.Shell;
public sealed class CommandPalette : Window
{
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly ListBox _results = new() { MinHeight = 220, MaxHeight = 380 };
    private readonly CommandRegistry _registry;
    private readonly AppServices _services;
    public CommandPalette(Window owner, CommandRegistry registry, AppServices services)
    {
        Owner = owner; _registry = registry; _services = services;
        Title = "Command palette"; Width = 580; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Bg.Surface");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = "Find a command", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        AutomationProperties.SetName(_search, "Search commands"); AutomationProperties.SetName(_results, "Commands");
        stack.Children.Add(_search); stack.Children.Add(_results); Content = stack;
        _search.TextChanged += (_, _) => Refresh(); _results.MouseDoubleClick += (_, _) => Run();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Run(); e.Handled = true; }
            else if (e.Key is Key.Down or Key.Up && _results.Items.Count > 0)
            {
                _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _results.Items.Count - 1);
                _results.ScrollIntoView(_results.SelectedItem); e.Handled = true;
            }
        };
        Loaded += (_, _) => _search.Focus(); Deactivated += (_, _) => Close(); Refresh();
    }
    private void Refresh()
    {
        _results.Items.Clear();
        foreach (var c in _registry.Search(_search.Text, _services.UiState.RecentCommands).Take(80))
        {
            var row = new DockPanel();
            var icon = new Path { Data = TryFindResource(c.Icon) as Geometry, Width = 18, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0) };
            icon.SetResourceReference(Shape.StrokeProperty, "Text.Primary"); icon.StrokeThickness = 1.5;
            DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
            var key = new Label { Content = c.Gesture, Style = TryFindResource("KeyCap") as Style };
            DockPanel.SetDock(key, Dock.Right); row.Children.Add(key);
            var label = new StackPanel(); label.Children.Add(new TextBlock { Text = c.Title });
            var category = new TextBlock { Text = c.Category, FontSize = 11 }; category.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            label.Children.Add(category); row.Children.Add(label);
            var item = new ListBoxItem { Content = row, Tag = c, IsEnabled = c.CanExecute(), ToolTip = c.DisabledReason ?? c.Title };
            AutomationProperties.SetName(item, c.Title); _results.Items.Add(item);
        }
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
    }
    private void Run()
    {
        if (_results.SelectedItem is not ListBoxItem { Tag: AppCommand c } || !c.CanExecute()) return;
        _services.SaveUiState(_services.UiState.WithRecentCommand(c.Id)); Close(); c.Execute();
    }
}
