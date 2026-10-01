using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Documents;

namespace SnagItOpen.App.Shell.DialogWindows;

public sealed class ScaleDialog : DialogWindow
{
    private readonly int _originalWidth, _originalHeight;
    private readonly NumberBox _percent = new() { Label = "Percent", Minimum = 0.01, Maximum = 10000, Value = 100 };
    private readonly NumberBox _width = new() { Label = "Width (px)", Minimum = 1, Maximum = Limits.MaxDimension };
    private readonly NumberBox _height = new() { Label = "Height (px)", Minimum = 1, Maximum = Limits.MaxDimension };
    private readonly CheckBox _lock = new() { Content = "Keep aspect ratio", IsChecked = true, Margin = new Thickness(0, 8, 0, 8) };
    private readonly RadioButton _content = new() { Content = "Scale content and canvas", IsChecked = true, GroupName = "ScaleScope" };
    private readonly RadioButton _canvas = new() { Content = "Canvas only", GroupName = "ScaleScope" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private bool _updating;
    public ScaleRequest? Result { get; private set; }
    public ScaleDialog(Window? owner, int width, int height) : base(owner, "Scale document", new StackPanel(), "Scale")
    {
        _originalWidth = Math.Max(1, width); _originalHeight = Math.Max(1, height); _width.Value = _originalWidth; _height.Value = _originalHeight;
        var body = (StackPanel)Body;
        body.Children.Add(_percent); body.Children.Add(new TextBlock { Text = "Or enter a target size", Margin = new Thickness(0, 12, 0, 4) }); body.Children.Add(_width); body.Children.Add(_height); body.Children.Add(_lock); body.Children.Add(_content); body.Children.Add(_canvas); body.Children.Add(_error);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Status.Error");
        _percent.Committed += value => { if (_updating) return; Sync(Math.Round(_originalWidth * value / 100), Math.Round(_originalHeight * value / 100)); };
        _width.Committed += value => { if (_updating) return; Sync(value, _lock.IsChecked == true ? Math.Round(value * _originalHeight / _originalWidth) : _height.Value); };
        _height.Committed += value => { if (_updating) return; Sync(_lock.IsChecked == true ? Math.Round(value * _originalWidth / _originalHeight) : _width.Value, value); };
        foreach (var box in new[] { _percent, _width, _height }) box.Input.TextChanged += (_, _) => RefreshValidation();
        _lock.Checked += (_, _) => Sync(_width.Value, Math.Round(_width.Value * _originalHeight / _originalWidth));
        Validate = () => _percent.CommitEdit() && _width.CommitEdit() && _height.CommitEdit() && RefreshValidation();
        Accepted = () => Result = new ScaleRequest((int)_width.Value, (int)_height.Value, _content.IsChecked == true, _width.Value / _originalWidth, _height.Value / _originalHeight);
        RefreshValidation();
    }
    private void Sync(double width, double height)
    {
        _updating = true; _width.Value = Math.Max(1, width); _height.Value = Math.Max(1, height); _percent.Value = _width.Value * 100 / _originalWidth; _updating = false; RefreshValidation();
    }
    private bool RefreshValidation()
    {
        if (_updating) return false;
        double width = 0, height = 0;
        bool parsed = double.TryParse(_width.Input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out width) && double.TryParse(_height.Input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out height);
        string? error = parsed ? DialogValidation.ScaleError(width, height) : "Enter whole pixel dimensions.";
        if (!_percent.IsValid) error = "Enter a valid percentage.";
        _error.Text = error ?? ""; PrimaryButton.IsEnabled = error is null; return error is null;
    }
    public static ScaleRequest? Show(Window? owner, int width, int height) { var dialog = new ScaleDialog(owner, width, height); return dialog.ShowDialog() == true ? dialog.Result : null; }
}
