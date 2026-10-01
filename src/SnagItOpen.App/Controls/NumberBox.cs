using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SnagItOpen.App.Controls;

public sealed class NumberBox : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumberBox), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.MinValue, Changed));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.MaxValue, Changed));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberBox), new PropertyMetadata(1d));
    public static readonly DependencyProperty IsMixedProperty = DependencyProperty.Register(nameof(IsMixed), typeof(bool), typeof(NumberBox), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(NumberBox), new PropertyMetadata("", Changed));
    public double Value { get => (double)GetValue(ValueProperty); set => SetCurrentValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public bool IsMixed { get => (bool)GetValue(IsMixedProperty); set => SetCurrentValue(IsMixedProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public TextBox Input { get; } = new() { MinWidth = 48, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
    public bool IsValid { get; private set; } = true;
    public event Action<double>? Preview;
    public event Action<double>? Committed;
    public event Action<bool>? ValidityChanged;
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.SizeWE };
    private bool _updating, _scrubbing, _changedDuringScrub, _startMixed;
    private Point _start;
    private double _startValue;

    public NumberBox()
    {
        var panel = new DockPanel(); DockPanel.SetDock(_label, Dock.Left); panel.Children.Add(_label); panel.Children.Add(Input); Content = panel;
        Input.TextChanged += (_, _) => Validate();
        Input.GotKeyboardFocus += (_, _) => Input.SelectAll();
        Input.LostKeyboardFocus += (_, _) => CommitEdit();
        Input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Revert(); e.Handled = true; }
            else if (e.Key == Key.Enter) { CommitEdit(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down) { Nudge(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
        };
        Input.PreviewMouseWheel += (_, e) => { if (Input.IsKeyboardFocused) { Nudge(e.Delta > 0 ? 1 : -1); e.Handled = true; } };
        _label.MouseLeftButtonDown += (_, e) => { Input.Focus(); _scrubbing = true; _changedDuringScrub = false; _start = e.GetPosition(this); _startValue = Value; _startMixed = IsMixed; _label.CaptureMouse(); e.Handled = true; };
        _label.MouseMove += (_, e) =>
        {
            if (!_scrubbing) return;
            double pixels = e.GetPosition(this).X - _start.X;
            double next = NumberInput.StepValue(_startValue, Step, (int)(pixels / 3), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Minimum, Maximum);
            if (next == Value && !IsMixed) return;
            IsMixed = false; Value = next; _changedDuringScrub = true; Preview?.Invoke(next);
        };
        _label.MouseLeftButtonUp += (_, e) => { EndScrub(); e.Handled = true; };
        _label.LostMouseCapture += (_, _) => EndScrub();
        PreviewKeyDown += (_, e) => { if (_scrubbing && e.Key == Key.Escape) { CancelScrub(); e.Handled = true; } };
        Refresh();
    }
    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false; _label.ReleaseMouseCapture();
        if (_changedDuringScrub) Committed?.Invoke(Value);
    }
    private void CancelScrub()
    {
        _scrubbing = false; _label.ReleaseMouseCapture(); Value = _startValue; IsMixed = _startMixed; Refresh(); Preview?.Invoke(_startValue);
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((NumberBox)d).Refresh();
    private void Refresh()
    {
        if (Input is null || _label is null) return;
        _updating = true; _label.Text = Label; _label.Visibility = string.IsNullOrEmpty(Label) ? Visibility.Collapsed : Visibility.Visible;
        Input.Text = NumberInput.Format(Value, IsMixed, CultureInfo.CurrentCulture);
        string accessibleLabel = string.IsNullOrWhiteSpace(Label) ? AutomationProperties.GetName(this) : Label;
        if (!string.IsNullOrWhiteSpace(accessibleLabel)) AutomationProperties.SetName(Input, accessibleLabel);
        _updating = false; SetValid(true);
    }
    private void Validate()
    {
        if (_updating) return;
        SetValid(IsMixed && Input.Text == "—" || NumberInput.TryParse(Input.Text, Minimum, Maximum, CultureInfo.CurrentCulture, out _));
    }
    private void SetValid(bool valid)
    {
        bool changed = IsValid != valid; IsValid = valid;
        Input.SetResourceReference(BorderBrushProperty, valid ? "Stroke.Control" : "Status.Error");
        Input.ToolTip = valid ? null : $"Enter a number from {Minimum:0.###} to {Maximum:0.###}.";
        if (changed) ValidityChanged?.Invoke(valid);
    }
    public bool CommitEdit()
    {
        if (IsMixed && Input.Text == "—") return true;
        if (!NumberInput.TryParse(Input.Text, Minimum, Maximum, CultureInfo.CurrentCulture, out var value)) { SetValid(false); return false; }
        bool changed = IsMixed || value != Value; IsMixed = false; Value = value; Refresh();
        if (changed) { Preview?.Invoke(value); Committed?.Invoke(value); }
        return true;
    }
    public void Revert() => Refresh();
    private void Nudge(int direction)
    {
        double start = NumberInput.TryParse(Input.Text, Minimum, Maximum, CultureInfo.CurrentCulture, out var typed) ? typed : Value;
        double value = NumberInput.StepValue(start, Step, direction, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Minimum, Maximum);
        bool changed = IsMixed || value != Value; IsMixed = false; Value = value; Refresh();
        if (changed) { Preview?.Invoke(value); Committed?.Invoke(value); }
    }
}
