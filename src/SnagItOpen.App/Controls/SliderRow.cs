using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SnagItOpen.App.Controls;

/// <summary>Continuous previews while dragging; one final commit per completed gesture.</summary>
public sealed class SliderRow : UserControl
{
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _unit = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    private readonly IconButton _reset = new() { Label = "Reset", ShowLabel = false, Width = 28, Margin = new Thickness(4, 0, 0, 0), Icon = Geometry.Parse("M3,4 L3,8 L7,8 M3,8 C3,2 13,2 13,8 C13,14 4,14 3,10") };
    private bool _updating, _dragging, _mouseGesture, _gestureMixed;
    private double _gestureStart;
    public Slider Slider { get; } = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 50, Margin = new Thickness(8, 0, 8, 0) };
    public NumberBox Number { get; } = new() { Width = 62 };
    public string Label { get => _label.Text; set { _label.Text = value; AutomationProperties.SetName(Slider, value); AutomationProperties.SetName(Number, value); AutomationProperties.SetName(Number.Input, value); _reset.Label = "Reset " + value; } }
    public string Unit { get => _unit.Text; set => _unit.Text = value; }
    public double Minimum { get => Slider.Minimum; set { Slider.Minimum = value; Number.Minimum = value; } }
    public double Maximum { get => Slider.Maximum; set { Slider.Maximum = value; Number.Maximum = value; } }
    public double Step { get => Number.Step; set { Number.Step = value; Slider.SmallChange = value; } }
    public double Value { get => Number.Value; set => Update(value, IsMixed); }
    public bool IsMixed { get => Number.IsMixed; set { Number.IsMixed = value; Slider.ToolTip = value ? "Mixed" : null; } }
    private double? _defaultValue;
    public double? DefaultValue { get => _defaultValue; set { _defaultValue = value; _reset.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible; } }
    public event Action<double>? Preview;
    public event Action<double>? Committed;
    public SliderRow()
    {
        var grid = new Grid(); foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        UIElement[] items = [_label, Slider, Number, _unit, _reset];
        for (int i = 0; i < items.Length; i++) { Grid.SetColumn(items[i], i); grid.Children.Add(items[i]); }
        Content = grid; DefaultValue = null;
        Slider.PreviewMouseLeftButtonDown += (_, _) => { if (!_mouseGesture) BeginGesture(); _mouseGesture = true; };
        Slider.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) => { if (e.ChangedButton == MouseButton.Left && _mouseGesture) EndGesture(false); }), true);
        Slider.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) => { if (_mouseGesture && !_dragging && Mouse.LeftButton == MouseButtonState.Released) EndGesture(false); }), true);
        Slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { if (!_mouseGesture) BeginGesture(); _dragging = true; }));
        Slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) => EndGesture(e.Canceled)));
        Slider.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true; Number.IsMixed = false; Number.Value = Slider.Value; _updating = false;
            Preview?.Invoke(Slider.Value); if (!_dragging && !_mouseGesture) Committed?.Invoke(Slider.Value);
        };
        Number.Preview += value => { Update(value, Number.IsMixed); Preview?.Invoke(value); };
        Number.Committed += value => { Update(value); Committed?.Invoke(value); };
        _reset.Click += (_, _) => { if (DefaultValue is { } value) { Update(value); Preview?.Invoke(Value); Committed?.Invoke(Value); } };
    }
    private void BeginGesture() { _gestureStart = Number.Value; _gestureMixed = IsMixed; }
    private void EndGesture(bool canceled)
    {
        if (!_dragging && !_mouseGesture) return;
        _dragging = false; _mouseGesture = false;
        if (canceled) { Update(_gestureStart, _gestureMixed); Preview?.Invoke(_gestureStart); }
        else if (Number.Value != _gestureStart || _gestureMixed) { Update(Number.Value); Committed?.Invoke(Number.Value); }
    }
    public void Update(double value, bool mixed = false)
    {
        _updating = true; Number.Value = Math.Clamp(value, Minimum, Maximum); Number.IsMixed = mixed; Slider.Value = Number.Value; Slider.ToolTip = mixed ? "Mixed" : null; _updating = false;
    }
}
