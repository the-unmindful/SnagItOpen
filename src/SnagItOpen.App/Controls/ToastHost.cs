using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnagItOpen.App.Controls;

public sealed class NotificationLifetime(bool hasActions)
{
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromSeconds(hasActions ? 8 : 4);
    public bool Advance(TimeSpan elapsed, bool paused)
    {
        if (!paused && elapsed > TimeSpan.Zero) Remaining -= elapsed;
        return Remaining <= TimeSpan.Zero;
    }
}

/// <summary>At most three notifications; expiry pauses while a card is hovered or contains focus.</summary>
public sealed class ToastHost : StackPanel
{
    private sealed record Entry(Notification Notification, ToastCard Card, NotificationLifetime Lifetime);
    private readonly List<Entry> _entries = [];
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    public int VisibleCount => _entries.Count;
    public IReadOnlyList<Notification> Notifications => _entries.Select(e => e.Notification).ToArray();
    public ToastHost()
    {
        Orientation = Orientation.Vertical; HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Bottom; Margin = new Thickness(16); MaxWidth = 380;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => { var elapsed = _clock.Elapsed; _clock.Restart(); Advance(elapsed); };
        Loaded += (_, _) => StartClock(); Unloaded += (_, _) => { _timer.Stop(); _clock.Stop(); };
        AutomationProperties.SetName(this, "Notifications");
    }
    public void Show(Notification notification)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Show(notification)); return; }
        while (_entries.Count >= 3) Remove(_entries[0]);
        var card = Build(notification); var entry = new Entry(notification, card, new NotificationLifetime(notification.Actions is { Count: > 0 }));
        _entries.Add(entry); Children.Add(card); card.CloseRequested += () => Remove(entry);
        if (IsLoaded) StartClock();
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) UIElementAutomationPeer.CreatePeerForElement(card)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    public void Clear() { _entries.Clear(); Children.Clear(); _timer.Stop(); _clock.Reset(); }
    public void Advance(TimeSpan elapsed)
    {
        foreach (var entry in _entries.ToArray())
            if (entry.Lifetime.Advance(elapsed, entry.Card.IsMouseOver || entry.Card.IsKeyboardFocusWithin)) Remove(entry);
    }
    private void StartClock() { if (_timer.IsEnabled || _entries.Count == 0) return; _clock.Restart(); _timer.Start(); }
    private void Remove(Entry entry) { _entries.Remove(entry); Children.Remove(entry.Card); if (_entries.Count == 0) { _timer.Stop(); _clock.Reset(); } }
    private ToastCard Build(Notification notification)
    {
        var card = new ToastCard { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), MinWidth = 260 };
        card.SetResourceReference(Border.BackgroundProperty, "Bg.Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Stroke.Control");
        AutomationProperties.SetName(card, $"{notification.Title}. {notification.Message}"); AutomationProperties.SetLiveSetting(card, notification.Kind == NotificationKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        var dock = new DockPanel();
        var close = new IconButton { Label = "Dismiss notification", ShowLabel = false, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top, Icon = Geometry.Parse("M4,4 L12,12 M12,4 L4,12") };
        close.Click += (_, _) => card.RequestClose(); DockPanel.SetDock(close, Dock.Right); dock.Children.Add(close);
        string iconName = notification.Kind == NotificationKind.Success ? "Check" : notification.Kind.ToString();
        var icon = ControlVisuals.Icon(TryFindResource("Icon." + iconName) as Geometry, card); icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Status." + notification.Kind); icon.Margin = new Thickness(0, 2, 8, 0); icon.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(icon, Dock.Left); dock.Children.Add(icon);
        if (notification.Thumbnail is not null) { var image = new Image { Source = notification.Thumbnail, Width = 48, Height = 48, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top }; DockPanel.SetDock(image, Dock.Left); dock.Children.Add(image); }
        var text = new StackPanel(); text.Children.Add(new TextBlock { Text = notification.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(notification.Message)) text.Children.Add(new TextBlock { Text = notification.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (notification.Actions is { Count: > 0 })
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var action in notification.Actions.Take(2)) { var button = new Button { Content = action.Label, Margin = new Thickness(0, 0, 4, 0) }; button.Click += (_, _) => { action.Execute(); card.RequestClose(); }; buttons.Children.Add(button); }
            text.Children.Add(buttons);
        }
        dock.Children.Add(text); card.Child = dock; return card;
    }
    private sealed class ToastCard : Border
    {
        public event Action? CloseRequested;
        public void RequestClose() => CloseRequested?.Invoke();
        protected override AutomationPeer OnCreateAutomationPeer() => new ToastPeer(this);
    }
    private sealed class ToastPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => "Notification";
    }
}
