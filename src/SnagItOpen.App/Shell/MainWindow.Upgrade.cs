using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Library;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Shell;

public partial class MainWindow
{
    private readonly InspectorPanel _inspector;
    private readonly List<(ToolKind Kind, RadioButton Button)> _allToolButtons = [];
    private readonly List<IconButton> _commandButtons = [];
    private readonly CommandRegistry _commands = new();
    private SplitButton? _captureButton;
    private bool _layersExpanded, _syncLayout;
    private PixelRect? _normalPhysicalBounds;
    public CommandRegistry Commands => _commands;
    private void BuildToolsMenu()
    {
        var menu = new MenuItem { Header = "_Tools" };
        foreach (var tool in ToolCatalog.All)
        {
            var item = new MenuItem { Header = tool.Name, InputGestureText = tool.Shortcut.ToString(), ToolTip = tool.Description, Tag = "tool:" + tool.Kind };
            item.Click += (_, _) => SelectTool(tool.Kind); menu.Items.Add(item);
        }
        MainMenu.Items.Insert(Math.Min(2, MainMenu.Items.Count), menu);
    }
    private void UpdateGeneratedShortcuts()
    {
        StartShortcuts.Children.Clear();
        void Row(string title, string gesture)
        {
            var row = new DockPanel { Width = 228, Margin = new Thickness(0, 2, 12, 2) };
            var key = new Label { Content = gesture, Style = TryFindResource("KeyCap") as Style }; DockPanel.SetDock(key, Dock.Right); row.Children.Add(key);
            row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }); StartShortcuts.Children.Add(row);
        }
        Row("Capture region", RegionGesture() is { Length: > 0 } gesture ? gesture : "Not set");
        Row("Capture window", ActiveHotkeys.TryGetValue(HotkeyActions.Window, out var hotkey) ? hotkey.ToString() : "Not set");
        foreach (var title in new[] { "Undo", "Copy image", "Fit to window", "Export image…" })
            if (_commands.All.FirstOrDefault(c => c.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase)) is { } command) Row(command.Title, command.Gesture);
        Row("Select / Crop", ToolCatalog.Get(ToolKind.Select).Shortcut + " / " + ToolCatalog.Get(ToolKind.Crop).Shortcut);
        Row("Arrow / Rectangle / Text", string.Join(" / ", new[] { ToolKind.Arrow, ToolKind.Rectangle, ToolKind.Text }.Select(k => ToolCatalog.Get(k).Shortcut)));
    }
    private void ShowErrorToast(string message)
    {
        _vm.SetStatus(NotificationKind.Error, message);
        if (IsVisible) Toasts.Show(new(NotificationKind.Error, "Could not complete the action", message.Split('\n')[0], [new("Details", () => Dialogs.Error(this, message))]));
        else DesktopToastWindow.ShowError(_services, message, () => Dialogs.Error(IsVisible ? this : null, message));
    }
    private static void ShowInFolder(string path) => OutputFolders.Show(path);

    private void InitializeUpgrade()
    {
        BuildCommandBar(); BuildToolsMenu();
        foreach (var splitter in WorkspaceGrid.Children.OfType<GridSplitter>())
        {
            splitter.MouseEnter += (_, _) => splitter.SetResourceReference(BackgroundProperty, "Accent.SelectSoft");
            splitter.MouseLeave += (_, _) => splitter.SetResourceReference(BackgroundProperty, "Stroke.Divider");
            splitter.DragCompleted += (_, _) => _services.SaveUiState(_services.UiState with { LeftPanelWidth = LeftTabs.IsVisible ? ListColumn.ActualWidth : _services.UiState.LeftPanelWidth, RightPanelWidth = InspectorHost.IsVisible ? PropsColumn.ActualWidth : _services.UiState.RightPanelWidth });
        }
        ZoomBox.Committed += value => Canvas.ZoomTo(value / 100);
        OutsideWarning.Action = () => { _vm.FitCanvas(); FitSoon(); }; OutsideWarning.ActionLabel = "Fit to content";
        HiddenWarning.Action = () => _vm.Commit("Show all", d => DocumentOps.Reflow(d with
        {
            Images = d.Images.Select(i => i with { Visible = true }).ToArray(),
            Annotations = d.Annotations.Select(a => a with { Hidden = false }).ToArray(),
        })); HiddenWarning.ActionLabel = "Show all";
        _vm.NotificationRaised += notification => Toasts.Show(notification);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.IsBusy)) RefreshCommandState();
            if (e.PropertyName == nameof(EditorViewModel.StatusKind)) UpdateStatusIcon();
            if (e.PropertyName is null or "") { RefreshCanvasWarnings(); UpdateRecentProjects(); RefreshCommandState(); }
            if (e.PropertyName is null or "")
            {
                if (_vm.Document.Layout.Mode == LayoutMode.Free && _vm.Document.Images.Length > 0) CoachMark("free-snap", "Precise placement", "Hold Alt while moving to turn snapping off.");
                if (_vm.Document.Annotations.Any(a => a is Core.Documents.Annotations.RedactionAnnotation)) CoachMark("redaction", "Secure redaction", "Redaction hides pixels securely. Blur is a visual effect.");
            }
        };
        SizeChanged += (_, _) => { UpdateResponsiveLayout(); RememberPhysicalBounds(); };
        LocationChanged += (_, _) => RememberPhysicalBounds();
        PinnedImageWindow.Changed += UpdateTray;
        _services.History.Changed += UpdateTray;
        Closed += (_, _) => { PinnedImageWindow.Changed -= UpdateTray; _services.History.Changed -= UpdateTray; };
        PropertiesFlyout.Closed += (_, _) => RestoreInspectorParent();
        if (ThemeService.Current is { } theme) theme.ThemeChanged += (_, _) => { RefreshCommandState(); RefreshCanvasWarnings(); UpdateTray(); };
        Loaded += (_, _) =>
        {
            RestoreWindowPlacement(); UpdateRecentProjects(); UpdateResponsiveLayout(); RefreshCanvasWarnings();
            NameControls(this); RefreshCommandRegistry(); UpdateGeneratedShortcuts();
            if (_services.IsFirstRun && !_services.UiState.HasShownTip("first-run"))
            {
                FirstRunTip.Message = RegionGesture() is { Length: > 0 } gesture
                    ? $"SnagItOpen keeps running in the tray. Press {gesture} to capture from anywhere."
                    : "Choose Capture to start. Set a global shortcut in Settings to capture from anywhere.";
                FirstRunTip.Action = () => OnSettings(this, new RoutedEventArgs()); FirstRunTip.ActionLabel = "Change shortcut";
                FirstRunTip.Visibility = Visibility.Visible;
                FirstRunTip.Closed += () => _services.SaveUiState(_services.UiState.WithTipShown("first-run"));
                _services.SaveUiState(_services.UiState.WithTipShown("first-run"));
            }
        };
    }
    private void CoachMark(string key, string title, string message)
    {
        if (_services.UiState.HasShownTip(key)) return;
        Toasts.Show(new(NotificationKind.Info, title, message)); _services.SaveUiState(_services.UiState.WithTipShown(key));
    }
    private void UpdateStatusIcon()
    {
        StatusIcon.Data = TryFindResource(_vm.StatusKind == NotificationKind.Success ? "Icon.Check" : "Icon." + _vm.StatusKind) as Geometry ?? TryFindResource("Icon.Info") as Geometry;
        StatusIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Status." + _vm.StatusKind);
    }

    private void BuildToolBar()
    {
        ToolRail.Children.Clear(); ToolsBar.Items.Clear(); _allToolButtons.Clear();
        int group = -1;
        foreach (var tool in ToolCatalog.All)
        {
            if (group >= 0 && group != tool.Group) ToolRail.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
            group = tool.Group;
            var rail = new ToolRailButton { Label = tool.Name, Shortcut = tool.Shortcut.ToString(), Icon = TryFindResource(tool.Icon) as Geometry, GroupName = "tool-rail", ToolTip = tool.Description + " (" + tool.Shortcut + ")" };
            AutomationProperties.SetHelpText(rail, tool.Description); rail.Click += (_, _) => SelectTool(tool.Kind);
            ToolRail.Children.Add(rail); _allToolButtons.Add((tool.Kind, rail)); _toolButtons[tool.Kind] = rail;
            var classic = new RadioButton { GroupName = "classic-tool", ToolTip = tool.Description + " (" + tool.Shortcut + ")", Margin = new Thickness(2) };
            classic.Content = ControlVisuals.IconLabel(TryFindResource(tool.Icon) as Geometry, tool.Name, true, classic);
            AutomationProperties.SetName(classic, tool.Name + " tool"); AutomationProperties.SetAcceleratorKey(classic, tool.Shortcut.ToString());
            classic.Click += (_, _) => SelectTool(tool.Kind); ToolsBar.Items.Add(classic); _allToolButtons.Add((tool.Kind, classic));
        }
        ToolRail.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right)) return;
            int index = ToolCatalog.All.ToList().FindIndex(t => t.Kind == Canvas.Tool);
            int next = (index + (e.Key is Key.Down or Key.Right ? 1 : -1) + ToolCatalog.All.Count) % ToolCatalog.All.Count;
            SelectTool(ToolCatalog.All[next].Kind); _toolButtons[Canvas.Tool].Focus(); e.Handled = true;
        };
    }

    private void BuildCommandBar()
    {
        _captureButton = new SplitButton { Label = "Capture", Icon = TryFindResource("Icon.Capture") as Geometry, Shortcut = RegionGesture() };
        _captureButton.Click += OnCaptureRegion; CommandBar.Children.Add(_captureButton);
        CommandButton("Import", "Icon.Import", "Ctrl+O", OnImport);
        CommandButton("Paste", "Icon.Paste", "Ctrl+V", OnPaste);
        Divider();
        foreach (var (name, icon, run) in new (string, string, RoutedEventHandler)[]
        { ("Vertical", "Icon.Vertical", OnVertical), ("Horizontal", "Icon.Horizontal", OnHorizontal), ("Free", "Icon.Free", OnFree) }) CommandButton(name, icon, "", run);
        Divider();
        CommandButton("Undo", "Icon.Undo", "Ctrl+Z", OnUndo);
        CommandButton("Redo", "Icon.Redo", "Ctrl+Y", OnRedo);
        CommandButton("Properties", "Icon.Settings", "F4", OnToggleProperties);
        Divider();
        var drag = CommandButton("Drag out", "Icon.DragOut", "Enter", (_, _) => { });
        drag.PreviewMouseLeftButtonDown += OnDragOutDown; drag.PreviewMouseMove += OnDragOutMove; drag.KeyDown += OnDragOutKey;
        var copy = new SplitButton { Label = "Copy", Icon = TryFindResource("Icon.Copy") as Geometry, Shortcut = "Ctrl+Shift+C" };
        copy.SetStyleKey("PrimaryButton");
        copy.Click += OnCopy; copy.Menu = new ContextMenu();
        AddItem(copy.Menu, "Copy image", () => OnCopy(this, new RoutedEventArgs()));
        AddItem(copy.Menu, "Copy as file", () => _ = CopyAsFileAsync());
        AddItem(copy.Menu, "Pin to screen", () => OnPin(this, new RoutedEventArgs())); CommandBar.Children.Add(copy);
        copy.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(EditorViewModel.HasContent)));
        CommandButton("Export", "Icon.Export", "Ctrl+E", OnExport).SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(EditorViewModel.HasContent)));
        CommandButton("Save", "Icon.Save", "Ctrl+S", OnSave);
        CommandBar.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            var targets = CommandBar.Children.OfType<FrameworkElement>().SelectMany(child => child is SplitButton split ? new FrameworkElement[] { split.PrimaryButton } : child.Focusable ? [child] : []).Where(child => child.IsEnabled && child.IsVisible).ToArray();
            if (targets.Length == 0) return;
            int index = Array.FindIndex(targets, child => child.IsKeyboardFocusWithin);
            targets[(index + (e.Key == Key.Right ? 1 : -1) + targets.Length) % targets.Length].Focus(); e.Handled = true;
        };
        RefreshCaptureSplitMenu();
    }

    private IconButton CommandButton(string label, string icon, string shortcut, RoutedEventHandler run)
    {
        var button = new IconButton { Label = label, Shortcut = shortcut, Icon = TryFindResource(icon) as Geometry, Margin = new Thickness(2, 0, 2, 0) };
        button.Click += run; CommandBar.Children.Add(button); _commandButtons.Add(button); return button;
    }
    private void Divider() { var line = new Border { Width = 1, Height = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) }; line.SetResourceReference(Border.BackgroundProperty, "Stroke.Divider"); CommandBar.Children.Add(line); }
    private string RegionGesture() => ActiveHotkeys.TryGetValue(HotkeyActions.Region, out var key) ? key.ToString() : S.GestureFor(HotkeyActions.Region);
    private void RefreshCaptureSplitMenu()
    {
        if (_captureButton is null) return;
        _captureButton.Shortcut = RegionGesture();
        var source = MainMenu.Items.OfType<MenuItem>().FirstOrDefault(m => CleanHeader(m.Header) == "Capture");
        if (source is null) return;
        _captureButton.Menu = new ContextMenu();
        foreach (var item in source.Items) _captureButton.Menu.Items.Add(CloneMenuEntry(item));
    }
    private static object CloneMenuEntry(object item)
    {
        if (item is not MenuItem original) return new Separator();
        var copy = new MenuItem { Header = original.Header, InputGestureText = original.InputGestureText, IsCheckable = original.IsCheckable, IsChecked = original.IsChecked, IsEnabled = original.IsEnabled };
        foreach (var child in original.Items) copy.Items.Add(CloneMenuEntry(child));
        copy.Click += (_, e) => { if (e.OriginalSource != copy) return; if (original.IsCheckable) original.SetCurrentValue(MenuItem.IsCheckedProperty, copy.IsChecked); original.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, original)); };
        copy.SubmenuOpened += (_, _) => { original.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, original)); copy.Items.Clear(); foreach (var child in original.Items) copy.Items.Add(CloneMenuEntry(child)); };
        return copy;
    }

    private void RefreshCommandState()
    {
        foreach (var button in _commandButtons)
        {
            if (button.Label is "Paste" or "Import" or "Properties") button.IsEnabled = !_vm.IsBusy;
            else if (button.Label == "Drag out") button.IsEnabled = _vm.HasContent;
            else if (button.Label == "Undo") { button.IsEnabled = _vm.History.CanUndo; button.ToolTip = _vm.UndoLabel; }
            else if (button.Label == "Redo") { button.IsEnabled = _vm.History.CanRedo; button.ToolTip = _vm.RedoLabel; }
        }
        void UpdateMenus(ItemsControl parent, string category)
        {
            foreach (var menu in parent.Items.OfType<MenuItem>())
            {
                if (menu.Items.Count > 0) UpdateMenus(menu, category.Length == 0 ? CleanHeader(menu.Header) : category);
                else if (category is "Edit" or "File" or "Capture") menu.IsEnabled = CanExecuteMenu(category, CleanHeader(menu.Header));
            }
        }
        UpdateMenus(MainMenu, "");
        RefreshCaptureSplitMenu();
    }

    private void UpdateResponsiveLayout()
    {
        if (_syncLayout) return;
        _syncLayout = true;
        try
        {
            bool left = !_services.UiState.LeftPanelCollapsed && (ActualWidth >= 1100 || _layersExpanded);
            ListColumn.Width = new GridLength(left ? _services.UiState.LeftPanelWidth : 36);
            LeftTabs.Visibility = left ? Visibility.Visible : Visibility.Collapsed; LayersStrip.Visibility = left ? Visibility.Collapsed : Visibility.Visible;
            bool right = !_services.UiState.RightPanelCollapsed && ActualWidth >= 860;
            PropsColumn.Width = new GridLength(right ? _services.UiState.RightPanelWidth : ActualWidth >= 860 ? 36 : 0);
            InspectorHost.Visibility = right ? Visibility.Visible : Visibility.Collapsed; InspectorStrip.Visibility = !right && ActualWidth >= 860 ? Visibility.Visible : Visibility.Collapsed;
            bool classic = _services.UiState.ClassicToolbar;
            ClassicMenu.IsChecked = classic; ToolsBar.Visibility = classic ? Visibility.Visible : Visibility.Collapsed; RailScroll.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
            foreach (var button in _commandButtons) button.ShowLabel = ActualWidth >= 1100;
            foreach (var split in CommandBar.Children.OfType<SplitButton>()) split.PrimaryButton.ShowLabel = ActualWidth >= 1100;
        }
        finally { _syncLayout = false; }
    }
    private void OnToggleLayers(object sender, RoutedEventArgs e)
    {
        _layersExpanded = !_layersExpanded;
        _services.SaveUiState(_services.UiState with { LeftPanelCollapsed = LeftTabs.Visibility == Visibility.Visible }); UpdateResponsiveLayout();
    }
    private void OnToggleProperties(object sender, RoutedEventArgs e)
    {
        if (ActualWidth < 860) { if (PropertiesFlyout.IsOpen) PropertiesFlyout.IsOpen = false; else ShowInspector(); return; }
        _services.SaveUiState(_services.UiState with { RightPanelCollapsed = InspectorHost.Visibility == Visibility.Visible }); UpdateResponsiveLayout();
    }
    private void ShowInspector()
    {
        if (ActualWidth < 860)
        {
            InspectorHost.Content = null; FlyoutHost.Child = _inspector;
            FlyoutHost.MaxHeight = Math.Max(240, CanvasRegion.ActualHeight);
            PropertiesFlyout.HorizontalOffset = Math.Max(0, CanvasRegion.ActualWidth - 300); PropertiesFlyout.IsOpen = true;
        }
        else { _services.SaveUiState(_services.UiState with { RightPanelCollapsed = false }); UpdateResponsiveLayout(); }
    }
    private void RestoreInspectorParent() { FlyoutHost.Child = null; InspectorHost.Content = _inspector; }
    private void OnClassicToolbar(object sender, RoutedEventArgs e) { _services.SaveUiState(_services.UiState with { ClassicToolbar = ClassicMenu.IsChecked }); UpdateResponsiveLayout(); }
    private void CycleFocusRegion(int direction)
    {
        FrameworkElement[] regions = [CommandBar, _services.UiState.ClassicToolbar ? ToolsBar : ToolRail, LeftTabs.IsVisible ? LeftTabs : LayersStrip, Canvas, _inspector.IsVisible ? _inspector : InspectorStrip.IsVisible ? InspectorStrip : CommandBar, StatusRegion];
        int current = Array.FindIndex(regions, r => r.IsKeyboardFocusWithin);
        for (int i = 1; i <= regions.Length; i++)
        {
            var target = regions[(current + direction * i + regions.Length * 2) % regions.Length];
            if (!target.IsVisible) continue;
            if (target == Canvas) Canvas.Focus(); else target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); return;
        }
    }
    private void RefreshCanvasWarnings()
    {
        OutsideWarning.Visibility = !_vm.Document.AutoCanvas && DocumentBounds.HasContentOutside(_vm.Document) ? Visibility.Visible : Visibility.Collapsed;
        HiddenWarning.Visibility = Canvas.IsAllContentHidden ? Visibility.Visible : Visibility.Collapsed;
        _inspector.Refresh();
    }
    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;
        RememberPhysicalBounds();
        _services.SaveUiState(_services.UiState with { Window = new(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized) { PhysicalBounds = _normalPhysicalBounds },
            LeftPanelWidth = LeftTabs.IsVisible ? ListColumn.ActualWidth : _services.UiState.LeftPanelWidth,
            RightPanelWidth = InspectorHost.IsVisible ? PropsColumn.ActualWidth : _services.UiState.RightPanelWidth });
    }
    private void RestoreWindowPlacement()
    {
        if (_services.UiState.Window is not { } placement) return;
        var monitors = _services.Monitors.GetMonitors();
        if (placement.PhysicalBounds is { } physical && !physical.IsEmpty)
        {
            var nativePlacement = new WindowPlacement(physical.X, physical.Y, physical.Width, physical.Height, false);
            var nativeScreens = monitors.Select(m => new ScreenArea(m.WorkArea.X, m.WorkArea.Y, m.WorkArea.Width, m.WorkArea.Height)).ToArray();
            if (!UiState.IsPlacementVisible(nativePlacement, nativeScreens)) return;
            AppNative.PlaceWindow(this, physical); _normalPhysicalBounds = physical;
            if (placement.Maximized) WindowState = WindowState.Maximized;
            return;
        }
        var screens = monitors.Select(m => new ScreenArea(m.Bounds.X * 96.0 / m.DpiX, m.Bounds.Y * 96.0 / m.DpiY, m.Bounds.Width * 96.0 / m.DpiX, m.Bounds.Height * 96.0 / m.DpiY)).ToArray();
        if (!UiState.IsPlacementVisible(placement, screens)) return;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = placement.Left; Top = placement.Top; Width = placement.Width; Height = placement.Height;
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }
    private void RememberPhysicalBounds() { if (IsVisible && WindowState == WindowState.Normal) _normalPhysicalBounds = AppNative.WindowBounds(this); }

    private static string CleanHeader(object? header) => header?.ToString()?.Replace("_", "").Trim() ?? "";
    private bool CanExecuteMenu(string category, string title)
    {
        if (_vm.IsBusy && category is not ("View" or "Help" or "Settings")) return false;
        if (category == "Capture") return !_capture.IsBusy;
        if (category == "Edit")
        {
            if (title.StartsWith("Undo", StringComparison.Ordinal)) return _vm.History.CanUndo;
            if (title.StartsWith("Redo", StringComparison.Ordinal)) return _vm.History.CanRedo;
            if (title is "Duplicate" or "Delete" || title.StartsWith("Bring", StringComparison.Ordinal) || title.StartsWith("Send", StringComparison.Ordinal)) return _vm.HasSelection;
            if (title.StartsWith("Rotate", StringComparison.Ordinal) || title.StartsWith("Flip", StringComparison.Ordinal) || title is "Reset crop" or "Remove blur/pixelate") return _vm.SelectedImages.Count > 0;
            if (title == "Renumber steps") return _vm.Document.Annotations.OfType<Core.Documents.Annotations.StepAnnotation>().Any();
            if (title == "Select all") return !_vm.IsEmpty;
        }
        if (category == "File" && (title is "Copy image" or "Pin to screen" or "Export image…")) return _vm.HasContent;
        return true;
    }
    private string? CaptureMenuGesture(string title)
    {
        string? action = title switch { "Region" => HotkeyActions.Region, "Window (visible area)" => HotkeyActions.Window, "All monitors" => HotkeyActions.FullScreen, "Last region" => HotkeyActions.LastRegion, "Scrolling capture…" => HotkeyActions.Scrolling, _ => null };
        return action is null ? null : ActiveHotkeys.TryGetValue(action, out var key) ? key.ToString() : "";
    }
    public void RefreshCommandRegistry()
    {
        _commands.Clear();
        OnPresetsOpened(this, new RoutedEventArgs()); OnCapturePresetsOpened(this, new RoutedEventArgs());
        void RegisterMenus(ItemsControl parent, string category)
        {
            foreach (var menu in parent.Items.OfType<MenuItem>())
            {
                var title = CleanHeader(menu.Header);
                if (title.Length == 0 || title.StartsWith('(')) continue;
                if (menu.Items.Count > 0) { RegisterMenus(menu, category.Length == 0 ? title : category); continue; }
                string id = menu.Tag is string existing && existing.StartsWith("tool:", StringComparison.Ordinal) ? existing : "menu:" + category + "/" + title;
                menu.Tag = id;
                if (category == "Capture" && CaptureMenuGesture(title) is { } gesture) menu.InputGestureText = gesture;
                _commands.Register(new(id, title, category, "Icon.More", menu.InputGestureText ?? "", category + " " + title,
                    () => menu.IsEnabled && CanExecuteMenu(category, CleanHeader(menu.Header)), () => { if (menu.IsCheckable) menu.SetCurrentValue(MenuItem.IsCheckedProperty, !menu.IsChecked); menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu)); }, "Unavailable for the current selection"));
            }
        }
        RegisterMenus(MainMenu, "");
        foreach (var tool in ToolCatalog.All) _commands.Register(new("tool:" + tool.Kind, tool.Name, "Tools", tool.Icon, tool.Shortcut.ToString(), tool.Description, () => true, () => SelectTool(tool.Kind)));
        foreach (var preset in _services.LayoutPresets.All) _commands.Register(new("layout-preset:" + preset.Name, preset.Name, "Layout presets", "Icon.Free", "", "layout combine", () => true, () => { _vm.ApplyPreset(preset); FitSoon(); }));
        foreach (var preset in _services.CapturePresets.Presets) _commands.Register(new("capture-preset:" + preset.Name, preset.Name, "Capture presets", "Icon.Capture", preset.Hotkey ?? "", "capture screenshot", () => !_capture.IsBusy, () => _ = RunPresetAsync(preset)));
        foreach (var page in new[] { "General", "Capture", "Shortcuts", "Output & library", "Appearance", "Advanced" }) _commands.Register(new("settings:" + page, page + " settings", "Settings", "Icon.Settings", "", "preferences", () => true, () => OpenSettingsPage(page)));
        foreach (var button in _commandButtons)
        {
            var existing = _commands.All.FirstOrDefault(c => c.Gesture == button.Shortcut && c.Gesture.Length > 0);
            if (existing is not null) { button.Shortcut = existing.Gesture; continue; }
            _commands.Register(new("toolbar:" + button.Label, button.Label, "Command bar", "Icon.More", button.Label == "Drag out" ? "" : button.Shortcut, button.Label, () => button.IsEnabled, () => { if (button.Label == "Drag out") _ = _vm.CopyImageAsync(); else button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }));
        }
        _commands.Register(new("copy-file", "Copy as file", "Output", "Icon.Copy", "", "clipboard Explorer PNG", () => _vm.HasContent, () => _ = CopyAsFileAsync(), "Add visible content first"));
    }
    private void OnCommandPalette(object sender, RoutedEventArgs e) { RefreshCommandRegistry(); new CommandPalette(this, _commands, _services).Show(); }
    private void OnZoomSelection(object sender, RoutedEventArgs e) => Canvas.ZoomToSelection();
    private void OnFitWidth(object sender, RoutedEventArgs e) => Canvas.FitWidth();
    private void OnZoomMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu(); AddItem(menu, "Fit (Ctrl+0)", Canvas.FitToView); AddItem(menu, "Fit width", Canvas.FitWidth);
        foreach (var percent in new[] { 50, 100, 200, 400 }) AddItem(menu, percent + "%", () => Canvas.ZoomTo(percent / 100.0));
        AddItem(menu, "Zoom to selection (Ctrl+2)", Canvas.ZoomToSelection); menu.PlacementTarget = sender as UIElement; menu.IsOpen = true;
    }
    private async Task CopyAsFileAsync()
    {
        var data = await BuildDragOutDataAsync(clipboardFile: true); if (data is null) return;
        try { Clipboard.SetDataObject(data, true); _vm.SetStatus(NotificationKind.Success, "Copied image as a file."); _vm.Notify(NotificationKind.Success, "Copied as file"); }
        catch (System.Runtime.InteropServices.ExternalException ex) { ShowErrorToast("Could not copy the file to the clipboard: " + ex.Message); }
    }
    private void UpdateRecentProjects()
    {
        RecentProjectsMenu.Items.Clear(); StartProjects.Children.Clear(); StartCaptures.Children.Clear();
        foreach (var path in _services.UiState.RecentProjects.Take(10))
        {
            bool exists = File.Exists(path);
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path) + (exists ? "" : " (Not found)"), IsEnabled = exists, ToolTip = path };
            item.Click += async (_, _) => { if (ConfirmDiscard()) { await _vm.OpenProjectAsync(path); FitSoon(); } };
            var context = new ContextMenu(); AddItem(context, "Remove from list", () => { _services.SaveUiState(_services.UiState.WithoutRecentProject(path)); UpdateRecentProjects(); }); item.ContextMenu = context;
            RecentProjectsMenu.Items.Add(item);
            if (StartProjects.Children.Count < 5)
            {
                var button = new Button { Content = Path.GetFileNameWithoutExtension(path) + (exists ? "" : " (Not found)"), IsEnabled = exists, ToolTip = path, ContextMenu = context, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
                button.Click += async (_, _) => { if (ConfirmDiscard()) { await _vm.OpenProjectAsync(path); FitSoon(); } }; StartProjects.Children.Add(button);
            }
        }
        if (StartProjects.Children.Count == 0) StartProjects.Children.Add(new TextBlock { Text = "Saved projects appear here", FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        if (RecentProjectsMenu.Items.Count == 0) RecentProjectsMenu.Items.Add(new MenuItem { Header = "(No recent projects)", IsEnabled = false });
        foreach (var entry in _services.History.Entries.OrderByDescending(e => e.CapturedAt).Take(6))
        {
            var button = new Button { Content = new Image { Source = CaptureDrag.LoadThumbnail(_services.History.ThumbnailPath(entry)), Width = 60, Height = 42, Stretch = Stretch.Uniform }, Margin = new Thickness(2), ToolTip = entry.DisplayName };
            AutomationProperties.SetName(button, entry.DisplayName); button.Click += (_, _) => _vm.AddLibraryAssets([ImageAsset.Create(entry.AssetId, entry.Width, entry.Height)]); StartCaptures.Children.Add(button);
        }
    }
    private void OpenSettingsPage(string page)
    {
        var window = new SettingsWindow(_services, _commands) { Owner = this };
        window.SelectPage(page);
        if (window.ShowDialog() == true) { ApplyHotkeys(); BuildCaptureMenus(); if (S.ShowTrayIcon || S.CloseToTray) CreateTray(); else { _tray?.Dispose(); _tray = null; } UpdateResponsiveLayout(); }
        GalleryMenu.IsChecked = S.ShowCaptureGallery; SetGalleryVisible(S.ShowCaptureGallery);
        Canvas.SnapEnabled = S.SnapEnabled; Canvas.Outside = S.OutsideCanvas;
        _inspector.Refresh(); RefreshCommandRegistry(); RefreshCommandState();
    }
    private static void AddItem(ItemsControl menu, string title, Action run) { var item = new MenuItem { Header = title }; item.Click += (_, _) => run(); menu.Items.Add(item); }
    private static void NameControls(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Focusable: true } element && string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
            {
                string label = element switch { ContentControl control => CleanHeader(control.Content), TextBox => element.Name, _ => element.Name };
                if (label.Length == 0) label = element.ToolTip?.ToString() ?? element.GetType().Name;
                AutomationProperties.SetName(element, label);
            }
            NameControls(child);
        }
    }
}
