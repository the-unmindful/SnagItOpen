using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SnagItOpen.App.Controls;

public sealed class SplitButton : UserControl
{
    private readonly IconButton _chevron;
    public IconButton PrimaryButton { get; } = new();
    public ContextMenu? Menu { get; set; }
    public string Label { get => PrimaryButton.Label; set { PrimaryButton.Label = value; _chevron.Label = string.IsNullOrWhiteSpace(value) ? "More options" : value + " options"; } }
    public string Shortcut { get => PrimaryButton.Shortcut; set => PrimaryButton.Shortcut = value; }
    public Geometry? Icon { get => PrimaryButton.Icon; set => PrimaryButton.Icon = value; }
    public event RoutedEventHandler? Click;
    public SplitButton()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        PrimaryButton.Click += (s, e) => Click?.Invoke(this, e);
        _chevron = new IconButton { Label = "More options", ShowLabel = false, IconSize = 12, MinWidth = 20, Width = 20, Margin = new Thickness(1, 0, 0, 0) };
        _chevron.Icon = Application.Current?.TryFindResource("Icon.ChevronDown") as Geometry ?? Geometry.Parse("M4,6 L8,10 L12,6");
        _chevron.Click += (_, _) => OpenMenu();
        panel.Children.Add(PrimaryButton); panel.Children.Add(_chevron); Content = panel;
        PreviewKeyDown += (_, e) => { var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key; if (key == System.Windows.Input.Key.Down && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) { OpenMenu(); e.Handled = true; } };
    }
    /// <summary>Applies a keyed button style (for example "PrimaryButton") to both parts so they read as one control.</summary>
    public void SetStyleKey(string key)
    {
        PrimaryButton.SetResourceReference(StyleProperty, key); _chevron.SetResourceReference(StyleProperty, key);
        _chevron.Padding = new Thickness(0);
    }
    public void OpenMenu()
    {
        if (Menu is null) return;
        Menu.PlacementTarget = this; Menu.Placement = PlacementMode.Bottom; Menu.IsOpen = true;
    }
}
