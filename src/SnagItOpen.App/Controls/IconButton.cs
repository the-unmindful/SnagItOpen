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
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Shortcut { get => (string)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }
    public bool ShowLabel { get => (bool)GetValue(ShowLabelProperty); set => SetValue(ShowLabelProperty, value); }
    public IconButton() { MinWidth = 24; MinHeight = 28; SetResourceReference(StyleProperty, typeof(Button)); Refresh(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((IconButton)d).Refresh();
    private void Refresh() { Content = ControlVisuals.IconLabel(Icon, Label, ShowLabel, this); ControlVisuals.Describe(this, Label, Shortcut); }
}
