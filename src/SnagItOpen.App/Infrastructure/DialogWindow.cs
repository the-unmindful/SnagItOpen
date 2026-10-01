using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SnagItOpen.App.Infrastructure;

/// <summary>Themed dialog frame shared by simple messages, prompts, and structured input.</summary>
public class DialogWindow : Window
{
    private bool _modal;
    public UIElement Body { get; }
    public Button PrimaryButton { get; }
    public Button? CancelButton { get; }
    public StackPanel ButtonRow { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
    public Func<bool>? Validate { get; set; }
    public Action? Accepted { get; set; }
    public DialogWindow(Window? owner, string title, UIElement body, string primary = "OK", string? cancel = "Cancel")
    {
        Body = body;
        Title = title; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        if (owner is { IsVisible: true }) Owner = owner;
        SetResourceReference(BackgroundProperty, "Bg.Window"); SetResourceReference(ForegroundProperty, "Text.Primary"); SetResourceReference(FontFamilyProperty, "Font.UI"); FontSize = 12;
        var panel = new StackPanel { Margin = new Thickness(24), MinWidth = 340, MaxWidth = 620 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { Content = body, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        PrimaryButton = new Button { Content = primary, IsDefault = true, MinWidth = 88, MinHeight = 28, Margin = new Thickness(8, 0, 0, 0) };
        PrimaryButton.SetResourceReference(StyleProperty, "PrimaryButton"); PrimaryButton.Click += (_, _) => { if (Validate?.Invoke() == false) return; Accepted?.Invoke(); Finish(true); };
        if (cancel is not null) { CancelButton = new Button { Content = cancel, MinWidth = 88, MinHeight = 28 }; CancelButton.Click += (_, _) => Finish(false); ButtonRow.Children.Add(CancelButton); }
        ButtonRow.Children.Add(PrimaryButton); panel.Children.Add(ButtonRow); Content = panel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && PrimaryButton.IsEnabled && e.OriginalSource is not TextBox { AcceptsReturn: true })
            {
                if (Validate?.Invoke() != false) { Accepted?.Invoke(); Finish(true); }
                e.Handled = true;
            }
        };
        AutomationProperties.SetName(this, title);
    }
    public Button AddSecondary(string label, Action action)
    {
        var button = new Button { Content = label, MinWidth = 88, MinHeight = 28, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) => action(); ButtonRow.Children.Insert(Math.Max(0, ButtonRow.Children.Count - 1), button); return button;
    }
    public void Finish(bool accepted) { if (_modal) DialogResult = accepted; else Close(); }
    public new bool? ShowDialog() { _modal = true; try { return base.ShowDialog(); } finally { _modal = false; } }
}
