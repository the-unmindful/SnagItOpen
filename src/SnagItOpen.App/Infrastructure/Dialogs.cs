using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SnagItOpen.App.Infrastructure;

public static class Dialogs
{
    public static string? Prompt(Window? owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 280, MinHeight = 28, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(box, label);
        var content = new StackPanel(); content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); content.Children.Add(box);
        var window = new DialogWindow(owner, title, content);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }
    public static void Info(Window? owner, string text) => Message(owner, "SnagItOpen", text, false);
    public static void Error(Window? owner, string text) => Message(owner, "SnagItOpen", text, true);
    private static bool ThemeAvailable => Application.Current?.TryFindResource("Bg.Window") is not null;
    private static void Message(Window? owner, string title, string message, bool error)
    {
        if (!ThemeAvailable) { MessageBox.Show(owner is { IsVisible: true } ? owner : null!, message, title, MessageBoxButton.OK, error ? MessageBoxImage.Warning : MessageBoxImage.Information); return; }
        new DialogWindow(owner, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 }, "OK", null).ShowDialog();
    }
    public static MessageBoxResult Confirm(Window? owner, string title, string message, string primary = "Yes", string? secondary = "No", string? cancel = "Cancel")
    {
        if (!ThemeAvailable)
        {
            var fallback = MessageBox.Show(owner is { IsVisible: true } ? owner : null!, message, title, secondary is null ? (cancel is null ? MessageBoxButton.OK : MessageBoxButton.OKCancel) : (cancel is null ? MessageBoxButton.YesNo : MessageBoxButton.YesNoCancel), MessageBoxImage.Question);
            return fallback == MessageBoxResult.OK ? MessageBoxResult.Yes : fallback;
        }
        var result = MessageBoxResult.Cancel;
        var window = new DialogWindow(owner, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 }, primary, cancel);
        window.Accepted = () => result = MessageBoxResult.Yes;
        if (secondary is not null) window.AddSecondary(secondary, () => { result = MessageBoxResult.No; window.Finish(false); });
        window.ShowDialog(); return result;
    }
}
