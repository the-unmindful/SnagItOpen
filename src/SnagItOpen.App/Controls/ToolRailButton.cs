using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace SnagItOpen.App.Controls;

public sealed class ToolRailButton : RadioButton
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(ToolRailButton), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ToolRailButton), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(nameof(Shortcut), typeof(string), typeof(ToolRailButton), new PropertyMetadata("", Changed));
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Shortcut { get => (string)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }
    public ToolRailButton()
    {
        Width = Height = 36; Margin = new Thickness(0, 2, 0, 2); Padding = new Thickness(8);
        SetResourceReference(StyleProperty, typeof(RadioButton));
        SetResourceReference(FocusVisualStyleProperty, "FocusRing");
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
              <Border x:Name="Bg" CornerRadius="4" Background="Transparent">
                <Grid><Border x:Name="Selected" Width="3" HorizontalAlignment="Left" Background="{DynamicResource Accent.Select}" Visibility="Collapsed"/><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bg" Property="Background" Value="{DynamicResource Bg.ControlHover}"/><Setter Property="Foreground" Value="{DynamicResource Text.Selected}"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Bg" Property="Background" Value="{DynamicResource Bg.Selected}"/><Setter TargetName="Selected" Property="Visibility" Value="Visible"/><Setter Property="Foreground" Value="{DynamicResource Text.Selected}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        Refresh();
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((ToolRailButton)d).Refresh();
    private void Refresh() { Content = ControlVisuals.IconLabel(Icon, Label, false, this); ControlVisuals.Describe(this, Label, Shortcut); }
}
