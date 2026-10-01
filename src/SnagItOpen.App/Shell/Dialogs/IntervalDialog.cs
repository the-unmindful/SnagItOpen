using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Capture;

namespace SnagItOpen.App.Shell.DialogWindows;

public sealed class IntervalDialog : DialogWindow
{
    private readonly NumberBox _seconds = new() { Label = "Interval (seconds)", Minimum = 1, Maximum = 60, Value = 5 };
    private readonly NumberBox _frames = new() { Label = "Maximum frames", Minimum = 1, Maximum = 200, Value = 20 };
    private readonly ComboBox _destination = new() { MinHeight = 28, Margin = new Thickness(0, 4, 0, 0), DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    public IntervalRequest? Result { get; private set; }
    public IntervalDialog(Window? owner, CaptureDestination destination = CaptureDestination.AppendBelow) : base(owner, "Interval capture", new StackPanel(), "Start")
    {
        var body = (StackPanel)Body;
        _destination.ItemsSource = Enum.GetValues<CaptureDestination>().Select(value => new { Value = value, Label = DisplayNames.Destination(value) }).ToArray(); _destination.SelectedValue = destination;
        AutomationProperties.SetName(_destination, "Destination"); _error.SetResourceReference(TextBlock.ForegroundProperty, "Status.Error");
        body.Children.Add(_seconds); body.Children.Add(_frames); body.Children.Add(new TextBlock { Text = "Destination", Margin = new Thickness(0, 12, 0, 0) }); body.Children.Add(_destination); body.Children.Add(_error);
        _seconds.Input.TextChanged += (_, _) => RefreshValidation(); _frames.Input.TextChanged += (_, _) => RefreshValidation();
        Validate = () => _seconds.CommitEdit() && _frames.CommitEdit() && RefreshValidation();
        Accepted = () => Result = new IntervalRequest((int)_seconds.Value, (int)_frames.Value, (CaptureDestination)(_destination.SelectedValue ?? CaptureDestination.AppendBelow)); RefreshValidation();
    }
    private bool RefreshValidation()
    {
        bool secondsValid = DialogValidation.TryWhole(_seconds.Input.Text, out int seconds), framesValid = DialogValidation.TryWhole(_frames.Input.Text, out int frames);
        string? error = !secondsValid || !framesValid ? "Enter whole numbers for interval and frames." : DialogValidation.IntervalError(seconds, frames);
        _error.Text = error ?? ""; PrimaryButton.IsEnabled = error is null; return error is null;
    }
    public static IntervalRequest? Show(Window? owner, CaptureDestination destination = CaptureDestination.AppendBelow) { var dialog = new IntervalDialog(owner, destination); return dialog.ShowDialog() == true ? dialog.Result : null; }
}
