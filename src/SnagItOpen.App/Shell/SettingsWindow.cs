using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell.Settings;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Shell;

/// <summary>Searchable staged preferences and capture recipes, with reversible theme preview.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly AppSettings _original;
    private AppSettings _draft;
    private readonly AppTheme _originalTheme;
    private readonly Action<AppTheme> _previewTheme;
    private readonly PreferencesPages _pages;
    private readonly ListBox _navigation = new() { MinWidth = 150 };
    private readonly TextBox _search = new() { MinHeight = 28, Margin = new Thickness(0, 0, 0, 16) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly InfoBar _error = new() { Kind = NotificationKind.Error, Title = "Settings were not saved", CanClose = false, Visibility = Visibility.Collapsed };
    private readonly Button _save = new() { Content = "Save", IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _noMatches = new() { Text = "No settings match your search.", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private string _selectedPage = "General";
    private bool _saved, _rolledBack, _closed;
    public CapturePresetsPage PresetsPage => _pages.Presets;
    public IReadOnlySet<string> EditableSettingsFields => _pages.All.SelectMany(p => p.Rows).Where(r => r.Field is not null).Select(r => r.Field!).ToHashSet(StringComparer.Ordinal);
    public IReadOnlyList<string> VisibleSettingLabels => _pages.All.Where(p => p.Visibility == Visibility.Visible).SelectMany(p => p.Rows).Where(r => r.Element.Visibility == Visibility.Visible).Select(r => r.Label).ToArray();

    public SettingsWindow(AppServices services, CommandRegistry? commands = null, Action<AppTheme>? previewTheme = null)
    {
        _services = services; _original = services.Settings; _draft = _original;
        _originalTheme = previewTheme is null ? ThemeService.Current?.Mode ?? _original.ThemeMode : _original.ThemeMode;
        _previewTheme = previewTheme ?? (mode => ThemeService.Current?.SetMode(mode));
        Title = "Settings - SnagItOpen";
        Width = 760; Height = 560; MinWidth = 660; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg.Window");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        FontSize = 12;
        _pages = new PreferencesPages(services, () => _draft, mutate => _draft = mutate(_draft), _previewTheme, commands);
        _pages.Changed += UpdateSaveState;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetName(_search, "Search settings across all pages");
        _search.ToolTip = "Search settings by label";
        _search.TextChanged += (_, _) => FilterPages();
        root.Children.Add(_search);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(156) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        _navigation.ItemsSource = _pages.All.Select(p => p.Title).ToArray();
        AutomationProperties.SetName(_navigation, "Settings pages");
        _navigation.SelectionChanged += (_, _) => { if (_navigation.SelectedItem is string title) { _selectedPage = title; _search.Text = ""; FilterPages(); _scroll.ScrollToTop(); } };
        body.Children.Add(_navigation);
        var pageStack = new StackPanel { Margin = new Thickness(20, 0, 8, 0) };
        foreach (var page in _pages.All) pageStack.Children.Add(page);
        pageStack.Children.Add(_noMatches);
        _scroll.Content = pageStack; Grid.SetColumn(_scroll, 1); body.Children.Add(_scroll);
        Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(_error, 2); root.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _save.Click += (_, _) => Save();
        _save.SetResourceReference(Button.BackgroundProperty, "Accent.Brand");
        _save.SetResourceReference(Button.ForegroundProperty, "Text.OnAccent");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        cancel.Click += (_, _) => CancelChanges();
        actions.Children.Add(_save); actions.Children.Add(cancel);
        Grid.SetRow(actions, 3); root.Children.Add(actions);
        Content = root;
        _navigation.SelectedItem = "General";
        Loaded += (_, _) => { if (Owner is MainWindow owner) owner.SuspendHotkeys(); };
        Closed += (_, _) => { _closed = true; if (!_saved) RollbackPreview(); if (Owner is MainWindow owner) owner.ApplyHotkeys(); };
        UpdateSaveState();
    }

    public void SelectPage(string page)
    {
        bool presets = page.Equals("Capture presets", StringComparison.OrdinalIgnoreCase);
        string wanted = presets ? "Capture" : page;
        string? title = _pages.All.Select(p => p.Title).FirstOrDefault(p => p.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (title is null) throw new ArgumentException("Unknown settings page.", nameof(page));
        _selectedPage = title; _search.Text = ""; _navigation.SelectedItem = title; FilterPages();
        if (presets) Dispatcher.BeginInvoke(() => PresetsPage.BringIntoView());
    }

    public void SearchSettings(string query) { _search.Text = query; FilterPages(); }
    private void FilterPages()
    {
        string query = _search.Text.Trim();
        foreach (var page in _pages.All)
        {
            page.Filter(query);
            page.Visibility = query.Length == 0 ? (page.Title == _selectedPage ? Visibility.Visible : Visibility.Collapsed)
                : page.Matches(query) ? Visibility.Visible : Visibility.Collapsed;
        }
        PresetsPage.Search(query);
        _noMatches.Visibility = _pages.All.Any(p => p.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateSaveState() => _save.IsEnabled = _pages.All.All(p => p.NumbersValid) && PresetsPage.IsValid && _pages.HotkeyError() is null;
    private void ShowError(string message) { _error.Message = message; _error.Visibility = Visibility.Visible; }

    private void Save()
    {
        Keyboard.ClearFocus();
        foreach (var page in _pages.All)
            if (page.CommitNumbers() is { } error) { ShowError(error); SelectPage(page.Title); return; }
        if (!PresetsPage.TryCommitCurrent(out var presetError)) { ShowError(presetError ?? "Check the capture presets."); SelectPage("Capture presets"); return; }
        if (_pages.HotkeyError() is { } hotkeyError) { ShowError(hotkeyError); SelectPage("Shortcuts"); return; }
        if (_draft.StartWithWindows != _original.StartWithWindows && !MainWindow.ApplyStartWithWindows(_draft.StartWithWindows, out var startupError))
        {
            ShowError("Could not change Start with Windows: " + startupError); return;
        }
        bool settingsWritten = false;
        bool settingsExisted = File.Exists(_services.Paths.Settings);
        try
        {
            var next = (_draft with { CloseToTray = _draft.CloseToTray && _draft.ShowTrayIcon }).Sanitize();
            _services.SettingsStore.Save(next);
            settingsWritten = true;
            _services.CapturePresets.Save(PresetsPage.Presets);
            _services.Settings = next;
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (settingsWritten)
            {
                try { if (settingsExisted) _services.SettingsStore.Save(_original); else File.Delete(_services.Paths.Settings); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { _services.Log("Could not restore settings after a failed save: " + rollbackError.Message); }
            }
            if (_draft.StartWithWindows != _original.StartWithWindows) _ = MainWindow.ApplyStartWithWindows(_original.StartWithWindows, out _);
            ShowError(ex.Message);
        }
    }
    private void RollbackPreview()
    {
        if (_rolledBack) return;
        _rolledBack = true; _previewTheme(_originalTheme);
    }
    public void CancelChanges()
    {
        RollbackPreview();
        if (!_closed) Close();
    }
}
