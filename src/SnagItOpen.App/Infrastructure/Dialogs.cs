using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SnagItOpen.App.Infrastructure;

/// <summary>Small code-built dialogs (single text prompt, multi-line text editor).</summary>
public static class Dialogs
{
    /// <summary>Asks for one line of text. Returns null when canceled.</summary>
    public static string? Prompt(Window? owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 280, Margin = new Thickness(0, 6, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        var w = Build(owner, title, label, box, out var ok);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true && ok() ? box.Text : null;
    }

    /// <summary>Multi-line text editing (Enter inserts a line, Ctrl+Enter confirms).</summary>
    public static string? EditText(Window? owner, string title, string initial)
    {
        var box = new TextBox
        {
            Text = initial, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            MinWidth = 360, MinHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 10),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Annotation text");
        var w = Build(owner, title, "Text (Ctrl+Enter to finish):", box, out var ok);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { w.DialogResult = true; e.Handled = true; }
        };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true && ok() ? box.Text : null;
    }

    private static Window Build(Window? owner, string title, string label, TextBox box, out Func<bool> ok)
    {
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        if (owner is { IsVisible: true }) w.Owner = owner;
        var okBtn = new Button { Content = "OK", IsDefault = !box.AcceptsReturn, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        okBtn.Click += (_, _) => w.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        w.Content = panel;
        ok = () => true;
        return w;
    }

    public static void Info(Window? owner, string text) =>
        MessageBox.Show(owner is { IsVisible: true } ? owner : null!, text, "SnagItOpen", MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(Window? owner, string text) =>
        MessageBox.Show(owner is { IsVisible: true } ? owner : null!, text, "SnagItOpen", MessageBoxButton.OK, MessageBoxImage.Warning);
}
