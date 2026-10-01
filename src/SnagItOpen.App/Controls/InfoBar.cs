using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnagItOpen.App.Controls;

public enum NotificationKind { Info, Success, Warning, Error }
public sealed record NotificationAction(string Label, Action Execute);
public sealed record Notification(NotificationKind Kind, string Title, string? Message = null, IReadOnlyList<NotificationAction>? Actions = null, ImageSource? Thumbnail = null);

public sealed class InfoBar : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(NotificationKind), typeof(InfoBar), new PropertyMetadata(NotificationKind.Info, Changed));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(InfoBar), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(InfoBar), new PropertyMetadata("", Changed));
    public NotificationKind Kind { get => (NotificationKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    private string? _actionLabel;
    public string? ActionLabel { get => _actionLabel; set { _actionLabel = value; Refresh(); } }
    public Action? Action { get; set; }
    private bool _canClose = true;
    public bool CanClose { get => _canClose; set { _canClose = value; Refresh(); } }
    public event Action? Closed;
    public InfoBar() { Margin = new Thickness(8); Refresh(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((InfoBar)d).Refresh();
    public void Refresh()
    {
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 8, 8, 8) };
        frame.SetResourceReference(Border.BackgroundProperty, "Bg.Surface"); frame.SetResourceReference(Border.BorderBrushProperty, "Status." + Kind);
        var row = new DockPanel(); var close = new IconButton { Label = "Close message", ShowLabel = false, Width = 28, Icon = Geometry.Parse("M4,4 L12,12 M12,4 L4,12"), Visibility = CanClose ? Visibility.Visible : Visibility.Collapsed };
        close.Click += (_, _) => { Visibility = Visibility.Collapsed; Closed?.Invoke(); }; DockPanel.SetDock(close, Dock.Right); row.Children.Add(close);
        if (!string.IsNullOrEmpty(ActionLabel)) { var action = new Button { Content = ActionLabel, Margin = new Thickness(8, 0, 8, 0) }; action.Click += (_, _) => Action?.Invoke(); DockPanel.SetDock(action, Dock.Right); row.Children.Add(action); }
        string iconName = Kind == NotificationKind.Success ? "Check" : Kind.ToString();
        var icon = ControlVisuals.Icon(TryFindResource("Icon." + iconName) as Geometry, this); icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Status." + Kind); icon.Margin = new Thickness(0, 2, 8, 0); DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        var text = new StackPanel(); text.Children.Add(new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(Message)) text.Children.Add(new TextBlock { Text = Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(text); frame.Child = row; Content = frame;
        AutomationProperties.SetName(this, $"{Kind}: {Title}. {Message}"); AutomationProperties.SetLiveSetting(this, Kind == NotificationKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
    }
}
