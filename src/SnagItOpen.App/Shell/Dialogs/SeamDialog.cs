using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;

namespace SnagItOpen.App.Shell.DialogWindows;

public sealed class SeamDialog : DialogWindow
{
    private readonly NumberBox _overlap = new() { Label = "Overlap (px)", Minimum = 1 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Image _preview = new() { MaxWidth = 480, MaxHeight = 220, Stretch = Stretch.Uniform };
    private readonly int _available;
    private readonly bool _allowZero;
    private readonly Action<double>? _previewAction;
    private readonly Func<int, ImageSource?>? _renderPreview;
    public int? Result { get; private set; }
    public SeamDialog(Window? owner, int available, int initial = 1, Action<double>? preview = null, Func<int, ImageSource?>? renderPreview = null, string? note = null, bool allowZero = false, ImageSource? beforePreview = null,
        string title = "Join overlapping images", string intro = "Remove repeated pixels from the start of the second image.", string primary = "Join")
        : base(owner, title, new StackPanel(), primary)
    {
        _available = available; _allowZero = allowZero; _previewAction = preview; _renderPreview = renderPreview; _overlap.Minimum = allowZero ? 0 : 1; _overlap.Maximum = Math.Max(_overlap.Minimum, available - 1); _overlap.Value = Math.Clamp(initial, _overlap.Minimum, _overlap.Maximum);
        var body = (StackPanel)Body;
        body.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        if (!string.IsNullOrEmpty(note)) body.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(_overlap); body.Children.Add(_error);
        var previews = new System.Windows.Controls.Primitives.UniformGrid { Columns = beforePreview is null ? 1 : 2, Margin = new Thickness(0, 12, 0, 0) };
        if (beforePreview is not null)
        {
            var before = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; before.Children.Add(new TextBlock { Text = "Before", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            before.Children.Add(new Image { Source = beforePreview, MaxWidth = 232, MaxHeight = 220, Stretch = Stretch.Uniform }); previews.Children.Add(before); _preview.MaxWidth = 232;
        }
        var after = new StackPanel(); after.Children.Add(new TextBlock { Text = "After", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) }); after.Children.Add(_preview); previews.Children.Add(after); body.Children.Add(previews);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Status.Error");
        _overlap.Input.TextChanged += (_, _) => RefreshPreview();
        Validate = () => _overlap.CommitEdit() && RefreshPreview(); Accepted = () => Result = (int)_overlap.Value; RefreshPreview();
    }
    private bool RefreshPreview()
    {
        bool valid = DialogValidation.TryWhole(_overlap.Input.Text, out int overlap); string? error = valid ? DialogValidation.OverlapError(overlap, _available, _allowZero) : "Enter a whole number of pixels.";
        _error.Text = error ?? ""; PrimaryButton.IsEnabled = error is null;
        if (error is null) { _previewAction?.Invoke(overlap); _preview.Source = _renderPreview?.Invoke(overlap); _preview.Visibility = _preview.Source is null ? Visibility.Collapsed : Visibility.Visible; }
        return error is null;
    }
    public static int? Show(Window? owner, int maxOverlap, int initial = 1, Action<double>? preview = null, Func<int, ImageSource?>? renderPreview = null, string? note = null, bool allowZero = false, ImageSource? beforePreview = null,
        string title = "Join overlapping images", string intro = "Remove repeated pixels from the start of the second image.", string primary = "Join")
    {
        var dialog = new SeamDialog(owner, maxOverlap, initial, preview, renderPreview, note, allowZero, beforePreview, title, intro, primary); return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
