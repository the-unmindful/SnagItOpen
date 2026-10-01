using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SnagItOpen.App.Controls;

public sealed class SplitButton : UserControl
{
    private readonly Button _chevron;
    public IconButton PrimaryButton { get; } = new();
    public ContextMenu? Menu { get; set; }
    public string Label { get => PrimaryButton.Label; set { PrimaryButton.Label = value; ((IconButton)_chevron).Label = string.IsNullOrWhiteSpace(value) ? "More options" : value + " options"; } }
    public string Shortcut { get => PrimaryButton.Shortcut; set => PrimaryButton.Shortcut = value; }
    public Geometry? Icon { get => PrimaryButton.Icon; set => PrimaryButton.Icon = value; }
    public event RoutedEventHandler? Click;
    public SplitButton()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        PrimaryButton.Click += (s, e) => Click?.Invoke(this, e);
        _chevron = new IconButton { Label = "More options", ShowLabel = false, Width = 28 };
        ((IconButton)_chevron).Icon = Geometry.Parse("M3,6 L8,11 L13,6");
        _chevron.Click += (_, _) => OpenMenu();
        panel.Children.Add(PrimaryButton); panel.Children.Add(_chevron); Content = panel;
        PreviewKeyDown += (_, e) => { var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key; if (key == System.Windows.Input.Key.Down && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) { OpenMenu(); e.Handled = true; } };
    }
    public void OpenMenu()
    {
        if (Menu is null) return;
        Menu.PlacementTarget = this; Menu.Placement = PlacementMode.Bottom; Menu.IsOpen = true;
    }
}
