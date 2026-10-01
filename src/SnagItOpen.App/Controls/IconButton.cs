using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnagItOpen.App.Controls;

public class IconButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(IconButton), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(IconButton), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(nameof(Shortcut), typeof(string), typeof(IconButton), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(nameof(ShowLabel), typeof(bool), typeof(IconButton), new PropertyMetadata(true, Changed));
    public static readonly DependencyProperty IsSubtleProperty = DependencyProperty.Register(nameof(IsSubtle), typeof(bool), typeof(IconButton), new PropertyMetadata(true, SubtleChanged));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(IconButton), new PropertyMetadata(16.0, Changed));
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Shortcut { get => (string)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }
    public bool ShowLabel { get => (bool)GetValue(ShowLabelProperty); set => SetValue(ShowLabelProperty, value); }
    /// <summary>Borderless chrome button (default). False uses the bordered standard Button style.</summary>
    public bool IsSubtle { get => (bool)GetValue(IsSubtleProperty); set => SetValue(IsSubtleProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public IconButton() { MinWidth = 28; MinHeight = 28; ApplyStyle(); Refresh(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((IconButton)d).Refresh();
    private static void SubtleChanged(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((IconButton)d).ApplyStyle();
    private void ApplyStyle() { if (IsSubtle) SetResourceReference(StyleProperty, "SubtleButton"); else SetResourceReference(StyleProperty, typeof(Button)); }
    private void Refresh()
    {
        bool iconOnly = Icon is not null && (!ShowLabel || string.IsNullOrWhiteSpace(Label));
        // Icon-only buttons need the full content box for the icon; the style padding would clip it.
        if (iconOnly) Padding = new Thickness(0); else ClearValue(PaddingProperty);
        Content = ControlVisuals.IconLabel(Icon, Label, ShowLabel, this, IconSize);
        ControlVisuals.Describe(this, Label, Shortcut);
    }
}
