using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SnagItOpen.App.Controls;

internal static class ControlVisuals
{
    public static Path Icon(Geometry? geometry, FrameworkElement owner, double size = 16)
    {
        var path = new Path { Data = geometry, Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.5, IsHitTestVisible = false };
        path.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { Source = owner });
        return path;
    }

    public static void Describe(FrameworkElement element, string label, string shortcut)
    {
        element.ToolTip = string.IsNullOrEmpty(shortcut) ? label : $"{label} ({shortcut})";
        System.Windows.Automation.AutomationProperties.SetName(element, label);
        System.Windows.Automation.AutomationProperties.SetAcceleratorKey(element, shortcut);
    }

    public static StackPanel IconLabel(Geometry? icon, string label, bool showLabel, FrameworkElement owner)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null) panel.Children.Add(Icon(icon, owner));
        if (showLabel && !string.IsNullOrWhiteSpace(label)) panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(icon is null ? 0 : 6, 0, 0, 0) });
        return panel;
    }
}
