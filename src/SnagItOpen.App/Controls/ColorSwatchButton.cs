using System.Windows;
using System.Windows.Controls;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.App.Controls;

public sealed class ColorSwatchButton : Button
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(Rgba32?), typeof(ColorSwatchButton), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsMixedProperty = DependencyProperty.Register(nameof(IsMixed), typeof(bool), typeof(ColorSwatchButton), new PropertyMetadata(false, Changed));
    public Rgba32? Value { get => (Rgba32?)GetValue(ValueProperty); set => SetCurrentValue(ValueProperty, value); }
    public bool IsMixed { get => (bool)GetValue(IsMixedProperty); set => SetCurrentValue(IsMixedProperty, value); }
    public bool AllowNone { get; set; } = true;
    public IReadOnlyList<Rgba32> Recent { get; set; } = [];
    private string _label = "Colour";
    public string Label { get => _label; set { _label = value; Refresh(); } }
    public event Action<Rgba32?>? Preview;
    public event Action<Rgba32?>? Committed;
    public event Action? Canceled;
    public ColorSwatchButton() { MinHeight = 28; SetResourceReference(StyleProperty, typeof(Button)); Click += (_, _) => OpenPicker(); Refresh(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((ColorSwatchButton)d).Refresh();
    private void Refresh()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ColorPicker.Chip(IsMixed ? null : Value));
        string text = IsMixed ? "Mixed" : Value is { } c ? (c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}") : "None";
        row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Content = row; ControlVisuals.Describe(this, $"{Label}: {text}", "");
    }
    public void OpenPicker()
    {
        var original = Value; bool mixed = IsMixed;
        ColorPicker.Open(this, original, AllowNone, Recent,
            value => { IsMixed = false; Value = value; Preview?.Invoke(value); },
            value => { IsMixed = false; Value = value; Committed?.Invoke(value); },
            () => { Value = original; IsMixed = mixed; Preview?.Invoke(original); Canceled?.Invoke(); });
    }
}
