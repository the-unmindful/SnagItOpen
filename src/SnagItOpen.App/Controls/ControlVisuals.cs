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
        // Geometries are authored on a 16x16 grid: never stretch them (that changes size and weight per icon).
        var path = new Path
        {
            Data = geometry, Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.5, IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true,
        };
        if (Math.Abs(size - 16) > 0.01) path.LayoutTransform = new ScaleTransform(size / 16, size / 16);
        path.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { Source = owner });
        return path;
    }

    public static void Describe(FrameworkElement element, string label, string shortcut)
    {
        element.ToolTip = string.IsNullOrEmpty(shortcut) ? label : $"{label} ({shortcut})";
        System.Windows.Automation.AutomationProperties.SetName(element, label);
        System.Windows.Automation.AutomationProperties.SetAcceleratorKey(element, shortcut);
    }

    public static StackPanel IconLabel(Geometry? icon, string label, bool showLabel, FrameworkElement owner, double iconSize = 16)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) panel.Children.Add(Icon(icon, owner, iconSize));
        if (showLabel && !string.IsNullOrWhiteSpace(label)) panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(icon is null ? 0 : 6, 0, 0, 0) });
        return panel;
    }
}
