using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SnagItOpen.App.Controls;

public sealed record SegmentOption(object Value, string Label, Geometry? Icon = null);

public sealed class SegmentedControl : UserControl
{
    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(nameof(SelectedValue), typeof(object), typeof(SegmentedControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty IsMixedProperty = DependencyProperty.Register(nameof(IsMixed), typeof(bool), typeof(SegmentedControl), new PropertyMetadata(false, Changed));
    public object? SelectedValue { get => GetValue(SelectedValueProperty); set => SetCurrentValue(SelectedValueProperty, value); }
    public bool IsMixed { get => (bool)GetValue(IsMixedProperty); set => SetCurrentValue(IsMixedProperty, value); }
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly List<(SegmentOption Option, ToggleButton Button)> _segments = [];
    public IReadOnlyList<ToggleButton> Buttons => _segments.Select(s => s.Button).ToArray();
    public event Action<object?>? SelectionChanged;
    public SegmentedControl() { Content = _panel; KeyboardNavigation.SetTabNavigation(_panel, KeyboardNavigationMode.Once); }
    public SegmentedControl(IEnumerable<SegmentOption> options) : this() => SetOptions(options);
    public void SetOptions(IEnumerable<SegmentOption> options) { _panel.Children.Clear(); _segments.Clear(); foreach (var option in options) Add(option.Value, option.Label, option.Icon); Refresh(); }
    public void Add(object value, string label, Geometry? icon = null)
    {
        var option = new SegmentOption(value, label, icon);
        var button = new ToggleButton { MinHeight = 28, MinWidth = 28, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 2, 0) };
        button.Content = ControlVisuals.IconLabel(icon, label, icon is null, button); ControlVisuals.Describe(button, label, "");
        button.Click += (_, _) => Select(value);
        button.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End) || Keyboard.Modifiers != ModifierKeys.None) return;
            var enabled = _segments.Where(segment => segment.Button.IsEnabled && segment.Button.Visibility == Visibility.Visible).ToList();
            if (enabled.Count == 0) return;
            int current = enabled.FindIndex(segment => segment.Button == button);
            int next = e.Key switch { Key.Home => 0, Key.End => enabled.Count - 1, Key.Left or Key.Up => Math.Max(0, current - 1), _ => Math.Min(enabled.Count - 1, current + 1) };
            Select(enabled[next].Option.Value); enabled[next].Button.Focus(); e.Handled = true;
        };
        _segments.Add((option, button)); _panel.Children.Add(button); Refresh();
    }
    private void Select(object value) { IsMixed = false; SelectedValue = value; Refresh(); SelectionChanged?.Invoke(value); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((SegmentedControl)d).Refresh();
    private void Refresh()
    {
        if (_segments is null) return;
        ToolTip = IsMixed ? "Mixed" : null;
        var tabStop = _segments.FirstOrDefault(segment => !IsMixed && Equals(segment.Option.Value, SelectedValue)).Button
            ?? _segments.FirstOrDefault(segment => segment.Button.IsEnabled).Button;
        foreach (var (option, button) in _segments) { button.IsChecked = !IsMixed && Equals(option.Value, SelectedValue); button.IsTabStop = button == tabStop; button.ToolTip = IsMixed ? $"{option.Label} (Mixed)" : option.Label; }
    }
}
