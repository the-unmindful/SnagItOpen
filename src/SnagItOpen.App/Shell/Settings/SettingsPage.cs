using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;

namespace SnagItOpen.App.Shell.Settings;

public sealed record SettingRow(string? Field, string Label, FrameworkElement Element, FrameworkElement Control, string Keywords = "");

/// <summary>One settings page with searchable labels and a shared, validated control vocabulary.</summary>
public sealed class SettingsPage : StackPanel
{
    public string Title { get; }
    public List<SettingRow> Rows { get; } = [];
    private readonly List<(NumberBox Number, bool Integer)> _numbers = [];
    private readonly List<FrameworkElement> _notes = [];
    public event Action? Changed;

    public SettingsPage(string title)
    {
        Title = title;
        Margin = new Thickness(0, 0, 0, 20);
        Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
    }

    public void Note(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        Children.Add(note); _notes.Add(note);
    }

    public FrameworkElement Add(string? field, string label, FrameworkElement control, string keywords = "", bool ownLabel = false)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        if (!ownLabel)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var title = new Label { Content = label, Target = control, Padding = new Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            row.Children.Add(title);
            Grid.SetColumn(control, 1);
        }
        row.Children.Add(control);
        AutomationProperties.SetName(control, label);
        Rows.Add(new(field, label, row, control, keywords));
        Children.Add(row);
        return row;
    }

    public CheckBox Check(string field, string label, bool value, Action<bool> set)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
        check.Checked += (_, _) => { set(true); Changed?.Invoke(); };
        check.Unchecked += (_, _) => { set(false); Changed?.Invoke(); };
        Add(field, label, check, ownLabel: true);
        return check;
    }

    public NumberBox Number(string field, string label, double value, double min, double max, Action<double> set, bool integer = true, double step = 1)
    {
        var number = new NumberBox { Label = label, Minimum = min, Maximum = max, Step = step, Value = value };
        number.Committed += v => { if (!integer || v == Math.Truncate(v)) set(v); Changed?.Invoke(); };
        number.ValidityChanged += _ => Changed?.Invoke();
        number.Input.TextChanged += (_, _) =>
        {
            if (number.IsValid && !IsNumberValid(number, integer))
            {
                number.Input.SetResourceReference(Control.BorderBrushProperty, "Status.Error");
                number.Input.ToolTip = "Enter a whole number.";
            }
            Changed?.Invoke();
        };
        _numbers.Add((number, integer));
        Add(field, label, number, ownLabel: true);
        return number;
    }

    private sealed record Option<T>(T Value, string Label);
    public ComboBox Choice<T>(string field, string label, T value, IEnumerable<T> choices, Action<T> set, Func<T, string>? display = null) where T : notnull
    {
        var combo = new ComboBox
        {
            ItemsSource = choices.Select(v => new Option<T>(v, display?.Invoke(v) ?? (v is Enum e ? DisplayNames.For(e) : Convert.ToString(v, CultureInfo.CurrentCulture) ?? ""))).ToArray(),
            DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = value, MinWidth = 120,
        };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is T selected) { set(selected); Changed?.Invoke(); } };
        Add(field, label, combo);
        return combo;
    }

    public TextBox Text(string? field, string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, MinHeight = 28 };
        box.TextChanged += (_, _) => { set(box.Text); Changed?.Invoke(); };
        Add(field, label, box);
        return box;
    }

    public bool Matches(string query) => Rows.Any(row => Matches(row, query));
    private static bool Matches(SettingRow row, string query) => query.Length == 0 ||
        (row.Label + " " + row.Keywords).Contains(query, StringComparison.CurrentCultureIgnoreCase);
    public void Filter(string query)
    {
        foreach (var row in Rows) row.Element.Visibility = Matches(row, query) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var note in _notes) note.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsNumberValid(NumberBox number, bool integer) => number.IsValid &&
        (!integer || double.TryParse(number.Input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value == Math.Truncate(value));

    public bool NumbersValid => _numbers.All(n => !n.Number.IsEnabled || IsNumberValid(n.Number, n.Integer));
    public string? CommitNumbers()
    {
        foreach (var (number, integer) in _numbers)
        {
            if (!number.IsEnabled) continue;
            if (!IsNumberValid(number, integer) || !number.CommitEdit())
                return $"{number.Label}: enter {(integer ? "a whole number" : "a number")} from {number.Minimum:0.###} to {number.Maximum:0.###}.";
        }
        return null;
    }
}
