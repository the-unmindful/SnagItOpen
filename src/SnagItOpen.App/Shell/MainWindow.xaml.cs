using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Library;
using SnagItOpen.App.Shell.DialogWindows;
using SnagItOpen.Core;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Stitching;
using SnagItOpen.Imaging;
using SnagItOpen.Imaging.Effects;
using SnagItOpen.Imaging.Rendering;
using SnagItOpen.Storage.Settings;
using SnagItOpen.Windows.Shell;
using CaptureMode = SnagItOpen.Core.Capture.CaptureMode;

namespace SnagItOpen.App.Shell;

/// <summary>
/// Main editor window. Owns view wiring only: menus, toolbars, keyboard shortcuts, drag/drop, dialogs,
/// tray and global hotkeys. Document logic lives in <see cref="EditorViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private const string ImageIdFormat = "SnagItOpen.ImageId";
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly CaptureCoordinator _capture;
    private readonly Dictionary<ToolKind, RadioButton> _toolButtons = [];
    private readonly AnnotationPropertiesPanel _props;
    private readonly ImageEdgePanel _edges;
    private bool _syncing, _exiting, _propsPending;
    private Point _listDragStart;
    private ImageListItemViewModel? _listDragItem;
    private GlobalHotkeyService? _hotkeys;
    private TrayService? _tray;
    private readonly bool _shutdownOnClose;

    public MainWindow(AppServices services, EditorViewModel vm, CaptureCoordinator capture, bool shutdownOnClose = true)
    {
        _services = services;
        _vm = vm;
        _capture = capture;
        _shutdownOnClose = shutdownOnClose;
        InitializeComponent();
        DataContext = vm;

        Canvas.ViewModel = vm;
        Canvas.StyleProvider = k => _services.ToolStyles.Get(k.ToString(), DefaultStyle(k));
        Canvas.PrototypeProvider = k => _services.AnnotationStyles.Prototype(k.ToString());
        _inspector = new InspectorPanel(services, vm, () => Canvas.Tool, k => _services.ToolStyles.Get(k.ToString(), DefaultStyle(k)));
        InspectorHost.Content = _inspector;
        _props = _inspector.AnnotationPanel;
        _edges = _inspector.EdgePanel;
        ObjectsHost.Content = new ObjectsList(vm);
        _inspector.OutsideChanged += value => Canvas.Outside = value;
        _inspector.SnapChanged += value => Canvas.SnapEnabled = value;
        _inspector.CropToolRequested += () => SelectTool(ToolKind.Crop);
        Canvas.SnapEnabled = _services.Settings.SnapEnabled;
        Canvas.ViewChanged += () => ZoomBox.Value = Math.Round(Canvas.Zoom * 100);
        Canvas.EditTextRequested += OnEditText;
        Canvas.ViewChanged += PositionTextEditor;
        Canvas.ContextMenuOpening += OnCanvasContextMenu;
        Canvas.ContextMenu = new ContextMenu(); // enables ContextMenuOpening; replaced on open
        Canvas.PreviewMouseDown += (_, _) => FinishTextEdit(commit: true);
        Canvas.CutOutRequested += r => _ = CutOutAsync(r);

        BuildToolBar();
        BuildCaptureMenus();
        Canvas.Outside = S.OutsideCanvas;
        InitializeUpgrade();
        GalleryMenu.IsChecked = _services.Settings.ShowCaptureGallery;
        SetGalleryVisible(_services.Settings.ShowCaptureGallery);
        SelectTool(ToolKind.Select);

        vm.ErrorRaised += ShowErrorToast;
        vm.PropertyChanged += OnVmPropertyChanged;

        Drop += OnWindowDrop;
        DragOver += OnWindowDragOver;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Closing += OnClosingWindow;
        SourceInitialized += (_, _) => InitializeShell();
        Loaded += (_, _) => Canvas.Focus();
    }

    private AppSettings S => _services.Settings;

    // ================================================================== shell: tray, hotkeys, activation

    private void InitializeShell()
    {
        try
        {
            _hotkeys = new GlobalHotkeyService(new Win32HotkeyRegistrar());
            _hotkeys.Pressed += name => Dispatcher.BeginInvoke(() => OnHotkey(name));
            ApplyHotkeys();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _services.Log("Hotkeys unavailable: " + ex.Message);
            _vm.Status = "Global hotkeys are unavailable; use the Capture menu.";
        }
        // The tray is required for close-to-tray; hotkeys only work while the app runs.
        if (S.ShowTrayIcon || S.CloseToTray) CreateTray();
    }

    private void CreateTray()
    {
        _tray?.Dispose();
        _tray = new TrayService("SnagItOpen", TrayItems(), ShowEditor);
        UpdateTray();
    }

    /// <summary>Registers hotkeys from settings and capture presets. Conflicts are reported, never fatal.</summary>
    public void ApplyHotkeys()
    {
        if (_hotkeys is null) return;
        var problems = new List<string>();
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in S.Hotkeys) wanted[h.Action] = h.Gesture;
        foreach (var p in _services.CapturePresets.Presets) if (!string.IsNullOrWhiteSpace(p.Hotkey)) wanted["Preset:" + p.Name] = p.Hotkey;
        foreach (var name in _hotkeys.Bindings.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) _hotkeys.Bind(name, null);
        foreach (var (name, gesture) in wanted)
        {
            if (string.IsNullOrWhiteSpace(gesture)) { _hotkeys.Bind(name, null); continue; }
            if (!HotkeyGesture.TryParse(gesture, out var g)) { problems.Add($"{name}: '{gesture}' is not a valid shortcut."); continue; }
            var r = _hotkeys.Bind(name, g);
            if (!r.IsSuccess && name == HotkeyActions.Region && g.Modifiers == ModifierKeys.None && g.Key == Key.Snapshot
                && HotkeyGesture.TryParse("Ctrl+PrintScreen", out var alt) && _hotkeys.Bind(name, alt).IsSuccess)
            {
                // Windows 11 can reserve PrintScreen for Snipping Tool; fall back without losing the capture key.
                problems.Add("PrintScreen is taken by Windows (Settings > Accessibility > Keyboard > \"Use the Print screen key to open screen capture\"). Region capture uses Ctrl+PrintScreen instead.");
                continue;
            }
            if (!r.IsSuccess) problems.Add($"{name}: {r.Message}");
        }
        LastHotkeyProblems = problems;
        UpdateStartCardKeys();
        if (problems.Count > 0)
        {
            _vm.Status = "Hotkeys: " + string.Join(" ", problems);
            if (!IsVisible) _tray?.ShowBalloon("SnagItOpen hotkeys", string.Join(" ", problems));
        }
    }

    public IReadOnlyList<string> LastHotkeyProblems { get; private set; } = [];

    /// <summary>Shows the actually registered global shortcuts on the empty-canvas start card.</summary>
    private void UpdateStartCardKeys()
    {
        StartRegionKeyInline.Text = RegionGesture() is { Length: > 0 } gesture ? gesture : "Set shortcut";
        UpdateGeneratedShortcuts(); UpdateTray(); RefreshCaptureSplitMenu();
    }

    /// <summary>Starts with no visible window: creates the window handle (hotkeys, tray) without showing it.</summary>
    public void StartInTray()
    {
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        if (_tray is null) CreateTray();
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Adds or removes the per-user "run at sign-in" entry (no admin rights needed).</summary>
    public static bool ApplyStartWithWindows(bool enable, out string? error)
    {
        error = null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { error = "Could not find SnagItOpen.exe."; return false; }
                key.SetValue("SnagItOpen", $"\"{exe}\" --tray");
            }
            else key.DeleteValue("SnagItOpen", throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Releases every global hotkey (while the Settings window records new shortcuts).</summary>
    public void SuspendHotkeys()
    {
        if (_hotkeys is null) return;
        foreach (var name in _hotkeys.Bindings.Keys.ToList()) _hotkeys.Bind(name, null);
    }

    public IReadOnlyDictionary<string, HotkeyGesture> ActiveHotkeys =>
        _hotkeys?.Bindings ?? new Dictionary<string, HotkeyGesture>();

    private void OnHotkey(string name)
    {
        if (name.StartsWith("Preset:", StringComparison.Ordinal))
        {
            var p = _services.CapturePresets.Presets.FirstOrDefault(x => "Preset:" + x.Name == name);
            if (p is not null) _ = RunPresetAsync(p);
            return;
        }
        switch (name)
        {
            case HotkeyActions.Region: _ = RunCaptureAsync(CaptureMode.Region); break;
            case HotkeyActions.Window: _ = RunCaptureAsync(CaptureMode.Window); break;
            case HotkeyActions.AppendRegion: _ = RunCaptureAsync(CaptureMode.Region, BaseOptions() with { Destination = CaptureDestination.AppendBelow }); break;
            case HotkeyActions.FullScreen: _ = RunCaptureAsync(CaptureMode.AllMonitors); break;
            case HotkeyActions.LastRegion: _ = RunCaptureAsync(CaptureMode.LastRegion); break;
            case HotkeyActions.Scrolling: OnScrolling(this, new RoutedEventArgs()); break;
        }
    }

    /// <summary>Shows and activates the editor (tray, second instance, after capture).</summary>
    public void ShowEditor()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        Canvas.Focus();
    }

    /// <summary>Handles a message from a second instance (activate or open a file).</summary>
    public async void HandleInstanceMessage(InstanceMessage msg)
    {
        ShowEditor();
        if (msg.Kind == InstanceMessage.Open && msg.Path is { } p) await OpenPathsAsync([p]);
    }

    public async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        var projects = paths.Where(p => string.Equals(Path.GetExtension(p), ".sio", StringComparison.OrdinalIgnoreCase)).ToList();
        if (projects.Count > 0)
        {
            if (ConfirmDiscard()) await _vm.OpenProjectAsync(projects[0]);
            FitSoon();
            return;
        }
        await _vm.ImportFilesAsync(paths);
        FitSoon();
    }

    private void FitSoon() => Dispatcher.BeginInvoke(Canvas.FitToView, System.Windows.Threading.DispatcherPriority.Background);

    // ================================================================== closing

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        SaveWindowPlacement();
        if (!_exiting && S.CloseToTray && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (!ConfirmDiscard()) { e.Cancel = true; _exiting = false; return; }
        _exiting = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _services.SaveSettings(S with { SnapEnabled = Canvas.SnapEnabled });
        if (_shutdownOnClose) Application.Current.Shutdown();
    }

    private void ExitApplication()
    {
        ShowEditor();
        _exiting = true;
        Close();
    }

    /// <summary>Asks about unsaved changes. Returns false when the user cancels.</summary>
    private bool ConfirmDiscard()
    {
        if (!_vm.IsDirty || _vm.IsEmpty) return true;
        var r = Dialogs.Confirm(this, "SnagItOpen", "Save changes to the current composition?", "Save", "Discard", "Cancel");
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return WaitPumping(SaveAsync(forceDialog: false));
        _vm.DiscardRecovery();
        return true;
    }

    /// <summary>
    /// Waits for a UI-thread task from synchronous code (e.g. Closing) by pumping a nested dispatcher
    /// frame, so continuations that need the UI thread can run instead of deadlocking.
    /// </summary>
    private static bool WaitPumping(Task<bool> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        return task.IsCompletedSuccessfully && task.Result;
    }

    // ================================================================== view-model sync

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(EditorViewModel.SelectedImages))
            Dispatcher.BeginInvoke(SyncListSelection, System.Windows.Threading.DispatcherPriority.Background);
        if (e.PropertyName is "" or null or nameof(EditorViewModel.SelectedAnnotations))
            RefreshPropsSoon();
        if (e.PropertyName is "" or null) Dispatcher.BeginInvoke(SyncCanvasBar, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SyncListSelection()
    {
        _syncing = true;
        try
        {
            ImageList.SelectedItems.Clear();
            foreach (var item in _vm.Images)
                if (_vm.SelectedImages.Contains(item.Id)) ImageList.SelectedItems.Add(item);
        }
        finally { _syncing = false; }
    }

    // ================================================================== toolbar and tool styles

    private void SelectTool(ToolKind kind)
    {
        Canvas.CancelGesture();
        Canvas.Tool = kind;
        foreach (var (tool, button) in _allToolButtons) button.IsChecked = tool == kind;
        if (kind != ToolKind.Select && _vm.SelectedAnnotations.Count > 0) _vm.Select(_vm.SelectedImages, []);
        RefreshPropsSoon();
        _vm.Status = ToolCatalog.Get(kind).Description;
    }

    /// <summary>Rebuilds the properties panel once per dispatcher cycle (never while a panel field has focus).</summary>
    private void RefreshPropsSoon()
    {
        if (_propsPending) return;
        _propsPending = true;
        Dispatcher.BeginInvoke(() => { _propsPending = false; _inspector.Refresh(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ToolStyle DefaultStyle(ToolKind k) => k switch
    {
        ToolKind.Highlight => new ToolStyle { Color = new Rgba32(255, 230, 0, 255) },
        ToolKind.Redaction => new ToolStyle { Color = Rgba32.Black },
        ToolKind.Text => new ToolStyle { Color = Rgba32.Red, FontSize = 24, StrokeWidth = 0 },
        ToolKind.Callout => new ToolStyle { Color = Rgba32.Red, FontSize = 20, StrokeWidth = 2, Fill = Rgba32.White },
        ToolKind.Blur => new ToolStyle { EffectStrength = 8 },
        ToolKind.Pixelate => new ToolStyle { EffectStrength = 10 },
        ToolKind.Magnifier => new ToolStyle { Color = new Rgba32(40, 40, 40, 255), StrokeWidth = 3, Zoom = 2 },
        ToolKind.Stamp => new ToolStyle { Color = new Rgba32(30, 150, 60, 255), Symbol = StampSymbols.Check },
        ToolKind.Step => new ToolStyle { Color = Rgba32.Red, FontSize = 20 },
        _ => new ToolStyle(),
    };


    // ================================================================== in-place text editing

    private TextBox? _textBox;
    private TextAnnotation? _textTarget;
    private bool _textIsNew, _textClosing;

    /// <summary>
    /// Opens an on-canvas text box over the annotation (IME works as in any TextBox). Ctrl+Enter or clicking
    /// elsewhere commits; Escape cancels. Tool shortcuts are ignored while it has focus.
    /// </summary>
    /// <summary>Opens the on-canvas editor for an existing text or callout (used by the Objects list).</summary>
    public void EditTextOf(Guid id)
    {
        if (_vm.Document.FindAnnotation(id) is TextAnnotation t) { _vm.Select([], [id]); OnEditText(t, false); }
    }

    private void OnEditText(TextAnnotation t, bool isNew)
    {
        FinishTextEdit(commit: true);
        if (t.Locked) { _vm.Status = "Unlock the text to edit it."; return; }
        _textTarget = t;
        _textIsNew = isNew;
        var color = t is CalloutAnnotation c ? c.TextColor : t.Color;
        var tb = new TextBox
        {
            // An empty style opts out of the themed input template, which would change size and padding vs the renderer.
            Style = new Style(typeof(TextBox)), Margin = new Thickness(0, 2, 0, 2),
            Text = t.Text, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0), Foreground = new SolidColorBrush(color.ToColor()),
            Background = new SolidColorBrush(t.Fill is { A: > 0 } f ? f.ToColor() : Color.FromArgb(200, 255, 255, 255)),
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(t.FontFamily) ? "Segoe UI" : t.FontFamily),
            FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = t.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextAlignment = t.Alignment switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        tb.SetResourceReference(TextBox.BorderBrushProperty, "Accent.Select");
        if (color.A == 0) tb.SetResourceReference(TextBox.CaretBrushProperty, "Text.Primary");
        else tb.CaretBrush = tb.Foreground;
        TextBlock.SetLineHeight(tb, t.FontSize * 1.2 * Canvas.Zoom);
        if (t.Underline) tb.TextDecorations = TextDecorations.Underline;
        System.Windows.Automation.AutomationProperties.SetName(tb, "Annotation text. Ctrl+Enter to finish, Escape to cancel.");
        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { FinishTextEdit(commit: false); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { FinishTextEdit(commit: true); e.Handled = true; }
        };
        tb.LostKeyboardFocus += (_, _) => FinishTextEdit(commit: true);
        tb.TextChanged += (_, _) => PositionTextEditor();
        _textBox = tb;
        if (!isNew) Canvas.EditingAnnotation = t.Id;
        TextEditorLayer.Children.Add(tb);
        PositionTextEditor();
        Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        _vm.Status = "Type the text. Ctrl+Enter or click elsewhere to finish, Esc to cancel.";
    }

    /// <summary>Keeps the editor over the text area at the current zoom, pan and rotation.</summary>
    private void PositionTextEditor()
    {
        if (_textBox is not { } tb || _textTarget is not { } t) return;
        double z = Canvas.Zoom;
        var area = AnnotationRenderer.TextArea(t);
        var tl = Canvas.ToViewPoint(new PointD(area.X, area.Y));
        tb.FontSize = Math.Clamp(t.FontSize * z, 1, 2000);
        tb.Width = Math.Max(40, area.Width * z + 4);
        tb.MinHeight = Math.Max(tb.FontSize * 1.4, area.Height * z);
        System.Windows.Controls.Canvas.SetLeft(tb, tl.X - 2);
        System.Windows.Controls.Canvas.SetTop(tb, tl.Y - 1);
        if (t.Rotation != 0)
        {
            var c = Canvas.ToViewPoint(t.Bounds.Center);
            tb.RenderTransformOrigin = new Point(0, 0);
            tb.RenderTransform = new RotateTransform(t.Rotation, c.X - (tl.X - 2), c.Y - (tl.Y - 1));
        }
        else tb.RenderTransform = Transform.Identity;
    }

    private void FinishTextEdit(bool commit)
    {
        if (_textClosing || _textBox is not { } tb || _textTarget is not { } t) return;
        _textClosing = true;
        try
        {
            var text = tb.Text;
            bool isNew = _textIsNew;
            _textBox = null;
            _textTarget = null;
            TextEditorLayer.Children.Remove(tb);
            Canvas.EditingAnnotation = null;
            if (!commit) { _vm.Status = isNew ? "Text canceled." : "Edit canceled."; return; }
            if (string.IsNullOrWhiteSpace(text))
            {
                if (!isNew) _vm.Commit("Delete text", d => DocumentOps.RemoveAnnotations(d, [t.Id]));
                return;
            }
            if (!isNew && text == t.Text) return;
            var updated = t with { Text = text };
            double h = AnnotationRenderer.MeasureTextHeight(updated);
            updated = updated with { Bounds = updated.Bounds with { Height = Math.Max(updated.Bounds.Height, h) } };
            if (!WpfConvert.IsFontAvailable(updated.FontFamily))
                _vm.Status = $"Font '{updated.FontFamily}' is not installed; a fallback font is shown and exported.";
            if (isNew) _vm.AddAnnotation(updated);
            else _vm.UpdateAnnotation(updated, "Edit text");
        }
        finally
        {
            _textClosing = false;
            Canvas.Focus();
        }
    }

    // ================================================================== edges

    // Image edges (border, shadow, corners, torn) are edited live in ImageEdgePanel.

    // ================================================================== keyboard

    private static bool IsTyping() => Keyboard.FocusedElement is TextBox or ComboBox { IsEditable: true } or PasswordBox;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F1) { OnShortcuts(this, e); e.Handled = true; return; }
        if (key == Key.F4) { OnToggleProperties(this, e); e.Handled = true; return; }
        if (key == Key.F6) { CycleFocusRegion(shift ? -1 : 1); e.Handled = true; return; }
        if (key == Key.K && ctrl) { OnCommandPalette(this, e); e.Handled = true; return; }
        if (IsTyping()) return;
        bool handled = true;
        switch (key)
        {
            case Key.N when ctrl: OnNew(this, e); break;
            case Key.O when ctrl && shift: OnOpenProject(this, e); break;
            case Key.O when ctrl: OnImport(this, e); break;
            case Key.S when ctrl && shift: OnSaveAs(this, e); break;
            case Key.S when ctrl: OnSave(this, e); break;
            case Key.E when ctrl: OnExport(this, e); break;
            case Key.C when ctrl && mods.HasFlag(ModifierKeys.Alt): _vm.CopyStyle(); break;
            case Key.V when ctrl && mods.HasFlag(ModifierKeys.Alt): _vm.PasteStyle(); break;
            case Key.C when ctrl && shift: OnCopy(this, e); break;
            case Key.C when ctrl && _vm.SelectedAnnotations.Count > 0: CopyAnnotationsToClipboard(); break;
            case Key.C when ctrl: OnCopy(this, e); break;
            case Key.X when ctrl && _vm.SelectedAnnotations.Count > 0: CopyAnnotationsToClipboard(); _vm.CutAnnotations(); break;
            case Key.V when ctrl && ClipboardHasAnnotations() && _vm.PasteAnnotations(): break;
            case Key.V when ctrl: OnPaste(this, e); break;
            case Key.D when ctrl && _vm.SelectedAnnotations.Count > 0 && _vm.SelectedImages.Count == 0: _vm.DuplicateAnnotations(); break;
            case Key.OemPlus or Key.Add when !ctrl && _vm.SelectedAnnotations.Count > 0: _vm.AdjustStepNumbers(1); break;
            case Key.OemMinus or Key.Subtract when !ctrl && _vm.SelectedAnnotations.Count > 0: _vm.AdjustStepNumbers(-1); break;
            case Key.Tab when !ctrl && Canvas.IsKeyboardFocusWithin && _vm.HasSelection: CycleAnnotation(shift ? -1 : 1); break;
            case Key.OemCloseBrackets when ctrl && _vm.HasSelection: _vm.ZOrder(shift ? DocumentOps.ZMove.ToFront : DocumentOps.ZMove.Forward); break;
            case Key.OemOpenBrackets when ctrl && _vm.HasSelection: _vm.ZOrder(shift ? DocumentOps.ZMove.ToBack : DocumentOps.ZMove.Backward); break;
            case Key.Z when ctrl && shift: _vm.Redo(); break;
            case Key.Z when ctrl: _vm.Undo(); break;
            case Key.Y when ctrl: _vm.Redo(); break;
            case Key.D when ctrl: _vm.Duplicate(); break;
            case Key.A when ctrl: _vm.SelectAll(); break;
            case Key.K when ctrl: OnCommandPalette(this, e); break;
            case Key.F1: OnShortcuts(this, e); break;
            case Key.F4: OnToggleProperties(this, e); break;
            case Key.F6: CycleFocusRegion(shift ? -1 : 1); break;
            case Key.D2 or Key.NumPad2 when ctrl: Canvas.ZoomToSelection(); break;
            case Key.D0 or Key.NumPad0 when ctrl: Canvas.FitToView(); break;
            case Key.D1 or Key.NumPad1 when ctrl: Canvas.ZoomTo(1); break;
            case Key.OemPlus or Key.Add when ctrl: Canvas.ZoomBy(1.25); break;
            case Key.OemMinus or Key.Subtract when ctrl: Canvas.ZoomBy(0.8); break;
            case Key.L when ctrl && !ObjectsHost.IsKeyboardFocusWithin: OpenLibrary(); break;
            case Key.Delete: _vm.RemoveSelected(); break;
            case Key.Escape:
                if (PropertiesFlyout.IsOpen) { PropertiesFlyout.IsOpen = false; break; }
                if (Canvas.CancelGesture()) break;
                if (_vm.HasSelection) { _vm.ClearSelection(); break; }
                if (Canvas.Tool != ToolKind.Select)
                {
                    SelectTool(ToolKind.Select);
                }
                break;
            case Key.Up or Key.Down when mods == ModifierKeys.Alt && _vm.PrimaryImage is { } id:
                _vm.MoveInOrder(id, key == Key.Up ? -1 : 1); break;
            case Key.Left or Key.Right or Key.Up or Key.Down when Canvas.IsKeyboardFocusWithin && _vm.HasSelection && !ctrl:
                {
                    int step = shift ? 10 : 1;
                    _vm.Nudge(key == Key.Left ? -step : key == Key.Right ? step : 0, key == Key.Up ? -step : key == Key.Down ? step : 0);
                    break;
                }
            case Key.Space when !e.IsRepeat && Canvas.IsKeyboardFocusWithin: Canvas.SetSpace(true); break;
            case Key.Space when e.IsRepeat && Canvas.IsKeyboardFocusWithin: break;
            default:
                handled = !ctrl && mods == ModifierKeys.None && Canvas.IsKeyboardFocusWithin && ToolShortcut(key);
                break;
        }
        if (handled) e.Handled = true;
    }

    // ================================================================== annotation clipboard and context menu

    private const string AnnotationClipFormat = "SnagItOpen.Annotations";

    /// <summary>Copies to the private annotation clipboard and marks the system clipboard, so Ctrl+V knows which paste to do.</summary>
    private void CopyAnnotationsToClipboard()
    {
        _vm.CopyAnnotations();
        try { System.Windows.Clipboard.SetDataObject(new DataObject(AnnotationClipFormat, "1"), copy: false); }
        catch (System.Runtime.InteropServices.ExternalException) { /* private clipboard still works */ }
    }

    private bool ClipboardHasAnnotations()
    {
        if (!_vm.HasAnnotationClipboard) return false;
        try { return System.Windows.Clipboard.ContainsData(AnnotationClipFormat); }
        catch (System.Runtime.InteropServices.ExternalException) { return true; }
    }

    private void CycleAnnotation(int dir)
    {
        var doc = _vm.Document;
        var objects = doc.Images.Where(i => i.Visible).Select(i => i.Id).Concat(doc.Annotations.Where(a => !a.Hidden).Select(a => a.Id)).ToArray();
        if (objects.Length == 0) return;
        int cur = Array.FindIndex(objects, id => _vm.SelectedImages.Contains(id) || _vm.SelectedAnnotations.Contains(id));
        int next = cur < 0 ? (dir > 0 ? 0 : objects.Length - 1) : (cur + dir + objects.Length) % objects.Length;
        if (doc.FindImage(objects[next]) is not null) _vm.Select([objects[next]]); else _vm.Select([], [objects[next]]);
        _vm.Status = $"Object {next + 1} of {objects.Length} selected (Tab / Shift+Tab to move).";
    }

    private void OnCanvasContextMenu(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action a, bool enabled = true, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
            return mi;
        }
        bool anns = _vm.SelectedAnnotations.Count > 0;
        var primary = _vm.PrimaryAnnotation;
        if (primary is TextAnnotation t) Item("Edit text", () => OnEditText(t, false));
        Item("Cut", () => { CopyAnnotationsToClipboard(); _vm.CutAnnotations(); }, anns, "Ctrl+X");
        Item("Copy", CopyAnnotationsToClipboard, anns, "Ctrl+C");
        Item("Paste", () => _vm.PasteAnnotations(), _vm.HasAnnotationClipboard, "Ctrl+V");
        Item("Duplicate", _vm.DuplicateAnnotations, anns, "Ctrl+D");
        Item("Delete", _vm.RemoveSelected, anns || _vm.SelectedImages.Count > 0, "Del");
        menu.Items.Add(new Separator());
        Item("Copy style", _vm.CopyStyle, primary is not null);
        Item("Paste style", _vm.PasteStyle, anns && _vm.HasStyleClipboard);
        if (anns)
        {
            bool allLocked = _vm.SelectedAnnotationObjects.All(x => x.Locked);
            Item(allLocked ? "Unlock" : "Lock", () => _vm.UpdateSelectedAnnotations(x => x with { Locked = !allLocked }, allLocked ? "Unlock" : "Lock"));
        }
        menu.Items.Add(new Separator());
        var arrange = new MenuItem { Header = "Arrange", IsEnabled = anns || _vm.SelectedImages.Count > 0 };
        void Sub(MenuItem parent, string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); parent.Items.Add(mi); }
        Sub(arrange, "Bring to front", () => _vm.ZOrder(DocumentOps.ZMove.ToFront));
        Sub(arrange, "Bring forward", () => _vm.ZOrder(DocumentOps.ZMove.Forward));
        Sub(arrange, "Send backward", () => _vm.ZOrder(DocumentOps.ZMove.Backward));
        Sub(arrange, "Send to back", () => _vm.ZOrder(DocumentOps.ZMove.ToBack));
        menu.Items.Add(arrange);
        var align = new MenuItem { Header = "Align", IsEnabled = _vm.SelectedAnnotations.Count >= 2 };
        Sub(align, "Left", () => _vm.Align(AlignMode.Left));
        Sub(align, "Centre", () => _vm.Align(AlignMode.CenterX));
        Sub(align, "Right", () => _vm.Align(AlignMode.Right));
        Sub(align, "Top", () => _vm.Align(AlignMode.Top));
        Sub(align, "Middle", () => _vm.Align(AlignMode.Middle));
        Sub(align, "Bottom", () => _vm.Align(AlignMode.Bottom));
        align.Items.Add(new Separator());
        var dh = new MenuItem { Header = "Distribute horizontally", IsEnabled = _vm.SelectedAnnotations.Count >= 3 };
        dh.Click += (_, _) => _vm.Distribute(true);
        var dv = new MenuItem { Header = "Distribute vertically", IsEnabled = _vm.SelectedAnnotations.Count >= 3 };
        dv.Click += (_, _) => _vm.Distribute(false);
        align.Items.Add(dh);
        align.Items.Add(dv);
        menu.Items.Add(align);
        if (_vm.SelectedAnnotationObjects.OfType<StepAnnotation>().Any())
        {
            menu.Items.Add(new Separator());
            Item("Step number +1", () => _vm.AdjustStepNumbers(1), true, "+");
            Item("Step number −1", () => _vm.AdjustStepNumbers(-1), true, "−");
            Item("Renumber following steps", _vm.RenumberStepsFromSelected);
        }
        menu.PlacementTarget = Canvas;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private bool ToolShortcut(Key key)
    {
        if (ToolCatalog.ForKey(key) is not { } tool) return false;
        SelectTool(tool.Kind); return true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) Canvas.SetSpace(false);
    }

    // ================================================================== drag & drop

    // ------------------------------------------------------------------ drag the finished image out

    private Point _dragOutStart;
    private bool _dragOutArmed, _dragOutBusy;

    private void OnDragOutDown(object sender, MouseButtonEventArgs e)
    {
        _dragOutStart = e.GetPosition(this);
        _dragOutArmed = true;
    }

    private async void OnDragOutMove(object sender, MouseEventArgs e)
    {
        if (!_dragOutArmed || e.LeftButton != MouseButtonState.Pressed) { _dragOutArmed = false; return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragOutStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragOutStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragOutArmed = false;
        if (_dragOutBusy) return;
        _dragOutBusy = true;
        try
        {
            var data = await BuildDragOutDataAsync();
            if (data is null) return;
            // Our own window must not accept its own export.
            _ignoreSelfDrop = true;
            try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy); }
            finally { _ignoreSelfDrop = false; }
        }
        finally { _dragOutBusy = false; }
    }

    private async void OnDragOutKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        await _vm.CopyImageAsync();
    }

    private bool _ignoreSelfDrop;

    /// <summary>Renders the export area to a PNG in the app temp folder and wraps it as file + PNG + bitmap data.</summary>
    private async Task<DataObject?> BuildDragOutDataAsync(bool clipboardFile = false)
    {
        if (!_vm.HasContent) { _vm.Status = "Nothing to drag yet."; return null; }
        try
        {
            var px = await _services.Export.RenderAsync(_vm.Document);
            var png = await _services.Imaging.InvokeAsync(px.EncodePng);
            var bg = _vm.Document.Background.A == 255 ? _vm.Document.Background : Rgba32.White;
            var opaque = await _services.Imaging.InvokeAsync(() => px.FlattenOnto(bg).ToBitmap());
            var dir = Path.Combine(clipboardFile ? _services.Paths.Cache : _services.Paths.Temp, clipboardFile ? "clip" : "dragout");
            Directory.CreateDirectory(dir);
            CleanupDragOut(dir);
            var baseName = _vm.ProjectPath is { } pp ? Path.GetFileNameWithoutExtension(pp) : "SnagItOpen " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
            var file = Path.Combine(dir, baseName + ".png");
            await File.WriteAllBytesAsync(file, png);
            var data = new DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { file });
            data.SetData("PNG", new MemoryStream(png), autoConvert: false);
            data.SetImage(opaque);
            _vm.Status = $"Dragging {px.Width} × {px.Height} image.";
            return data;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            _vm.Status = "Could not prepare the image: " + ex.Message;
            return null;
        }
    }

    /// <summary>Deletes drag-out files older than a day (targets usually copy the file on drop).</summary>
    private static void CleanupDragOut(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f ? f : [];

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (_ignoreSelfDrop) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = e.Data.GetDataPresent(CaptureDrag.Format) || DroppedFiles(e).Length > 0 ? DragDropEffects.Copy
            : e.Data.GetDataPresent(ImageIdFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (_ignoreSelfDrop) { e.Handled = true; return; }
        // Recent captures (from the strip or the library window) are already stored assets: add directly.
        if (CaptureDrag.Read(e.Data) is { } captures)
        {
            e.Handled = true;
            var ok = captures.Where(a => _services.Assets.Contains(a.Id)).ToList();
            if (ok.Count == 0) { _vm.Status = "Those captures are no longer available."; return; }
            PixelPoint? at = null;
            if (Canvas.IsMouseOver || Canvas.InputHitTest(e.GetPosition(Canvas)) is not null)
            {
                var d = Canvas.ToDocumentPoint(e.GetPosition(Canvas));
                at = new PixelPoint((int)Math.Round(d.X), (int)Math.Round(d.Y));
            }
            Activate();
            _vm.AddLibraryAssets(ok, at);
            if (_vm.Mode != LayoutMode.Free) FitSoon();
            Canvas.Focus();
            return;
        }
        var files = DroppedFiles(e);
        if (files.Length == 0) return;
        e.Handled = true;
        Activate();
        await OpenPathsAsync(files);
    }

    private ImageListItemViewModel? ListItemAt(Point p)
    {
        var el = ImageList.InputHitTest(p) as DependencyObject;
        while (el is not null and not ListBoxItem)
            el = el is Visual ? VisualTreeHelper.GetParent(el) : LogicalTreeHelper.GetParent(el);
        return (el as ListBoxItem)?.DataContext as ImageListItemViewModel;
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _listDragStart = e.GetPosition(ImageList);
        _listDragItem = ListItemAt(_listDragStart);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _listDragItem is null) return;
        var p = e.GetPosition(ImageList);
        if (Math.Abs(p.X - _listDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _listDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _listDragItem;
        _listDragItem = null;
        DragDrop.DoDragDrop(ImageList, new DataObject(ImageIdFormat, item.Id.ToString()), DragDropEffects.Move);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ImageIdFormat)) { e.Effects = DragDropEffects.Move; e.Handled = true; }
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ImageIdFormat) || e.Data.GetData(ImageIdFormat) is not string s || !Guid.TryParse(s, out var id)) return;
        e.Handled = true;
        var target = ListItemAt(e.GetPosition(ImageList));
        int index = target is null ? _vm.Document.LayoutOrder.Length - 1 : Array.IndexOf(_vm.Document.LayoutOrder, target.Id);
        if (index >= 0) _vm.MoveToIndex(id, index);
        _vm.Select([id]);
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        // Items removed because the list was rebuilt are not a user deselection.
        foreach (var r in e.RemovedItems) if (r is ImageListItemViewModel it && !_vm.Images.Contains(it)) return;
        var ids = ImageList.SelectedItems.Cast<ImageListItemViewModel>().Select(i => i.Id).ToList();
        _vm.Select(ids, []);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down && _vm.PrimaryImage is { } id)
        {
            _vm.MoveInOrder(id, key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (key == Key.F2 && _vm.PrimaryImage is { } rid && _vm.SelectedLayer is { } l)
        {
            var name = Dialogs.Prompt(this, "Rename image", "Name:", l.Name ?? "");
            if (name is not null) _vm.RenameImage(rid, name);
            e.Handled = true;
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) { if (_vm.PrimaryImage is { } id) _vm.MoveInOrder(id, -1); }
    private void OnMoveDown(object sender, RoutedEventArgs e) { if (_vm.PrimaryImage is { } id) _vm.MoveInOrder(id, 1); }
    private void OnToggleHidden(object sender, RoutedEventArgs e) { foreach (var id in _vm.SelectedImages.ToList()) _vm.ToggleVisible(id); }

    // ================================================================== file commands

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _vm.NewDocument();
        FitSoon();
    }

    private static string ImageFilter => "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = ImageFilter, Multiselect = true, Title = "Import images", InitialDirectory = ValidDir(S.LastImportDirectory) };
        if (dlg.ShowDialog(this) != true) return;
        await _vm.ImportFilesAsync(dlg.FileNames);
        _services.SaveSettings(S);
        FitSoon();
    }

    private async void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SnagItOpen project|*.sio", Title = "Open project", InitialDirectory = ValidDir(S.LastProjectDirectory) };
        if (dlg.ShowDialog(this) != true || !ConfirmDiscard()) return;
        await _vm.OpenProjectAsync(dlg.FileName);
        FitSoon();
    }

    private static string ValidDir(string? d) => d is not null && Directory.Exists(d) ? d : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync(forceDialog: false);
    private async void OnSaveAs(object sender, RoutedEventArgs e) => await SaveAsync(forceDialog: true);

    private async Task<bool> SaveAsync(bool forceDialog)
    {
        var path = _vm.ProjectPath;
        if (forceDialog || path is null)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "SnagItOpen project|*.sio", DefaultExt = ".sio", Title = "Save editable project",
                FileName = path is null ? "Composition.sio" : Path.GetFileName(path),
                InitialDirectory = ValidDir(path is null ? S.LastProjectDirectory : Path.GetDirectoryName(path)),
            };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        var ok = await _vm.SaveProjectAsync(path);
        _services.SaveSettings(S);
        return ok;
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasContent) { _vm.Status = "Add an image before exporting."; return; }
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image|*.png|JPEG image|*.jpg", DefaultExt = ".png", Title = "Export image",
            FileName = (_vm.ProjectPath is { } p ? Path.GetFileNameWithoutExtension(p) : "Composition") + ".png",
            InitialDirectory = ValidDir(S.LastExportDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        var fmt = ExportOptions.FormatFromPath(dlg.FileName);
        if (dlg.FilterIndex == 2 && fmt != ExportFormat.Jpeg) fmt = ExportFormat.Jpeg;
        await _vm.ExportAsync(dlg.FileName, new ExportOptions(fmt, S.JpegQuality));
        _services.SaveSettings(S);
    }

    private async void OnCopy(object sender, RoutedEventArgs e) => await _vm.CopyImageAsync();
    private async void OnPaste(object sender, RoutedEventArgs e) { await _vm.PasteAsync(); FitSoon(); }

    private async void OnPin(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasContent) { _vm.Status = "Nothing to pin yet."; return; }
        try
        {
            var px = await _services.Export.RenderAsync(_vm.Document);
            new PinnedImageWindow(px.ToBitmap(), _vm.Document.Name, _services, ShowEditor).Show();
            _vm.Notify(NotificationKind.Success, "Pinned");
        }
        catch (InvalidOperationException ex) { _vm.Status = ex.Message; }
    }

    private void OnLibrary(object sender, RoutedEventArgs e) => OpenLibrary();

    private void OpenLibrary()
    {
        var open = OwnedWindows.OfType<LibraryWindow>().FirstOrDefault();
        if (open is not null) { open.Activate(); return; }
        new LibraryWindow(_services, _vm, () => OpenSettingsPage("Output & library")) { Owner = this }.Show();
    }

    private void OnToggleGallery(object sender, RoutedEventArgs e)
    {
        bool show = GalleryMenu.IsChecked;
        SetGalleryVisible(show);
        _services.SaveSettings(S with { ShowCaptureGallery = show });
    }

    private void SetGalleryVisible(bool show)
    {
        if (show && GalleryHost.Child is null) GalleryHost.Child = new CaptureGallery(_services, _vm, OpenLibrary);
        GalleryHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettingsPage("General");

    private void OnExit(object sender, RoutedEventArgs e) => ExitApplication();

    // ================================================================== edit commands

    private void OnUndo(object sender, RoutedEventArgs e) => _vm.Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => _vm.Redo();
    private void OnDuplicate(object sender, RoutedEventArgs e) => _vm.Duplicate();
    private void OnDelete(object sender, RoutedEventArgs e) => _vm.RemoveSelected();
    private void OnSelectAll(object sender, RoutedEventArgs e) => _vm.SelectAll();
    private void OnRotateLeft(object sender, RoutedEventArgs e) => _vm.Rotate(-1);
    private void OnRotateRight(object sender, RoutedEventArgs e) => _vm.Rotate(1);
    private void OnFlipH(object sender, RoutedEventArgs e) => _vm.Flip(true);
    private void OnFlipV(object sender, RoutedEventArgs e) => _vm.Flip(false);
    private void OnToFront(object sender, RoutedEventArgs e) => _vm.ZOrder(DocumentOps.ZMove.ToFront);
    private void OnForward(object sender, RoutedEventArgs e) => _vm.ZOrder(DocumentOps.ZMove.Forward);
    private void OnBackward(object sender, RoutedEventArgs e) => _vm.ZOrder(DocumentOps.ZMove.Backward);
    private void OnToBack(object sender, RoutedEventArgs e) => _vm.ZOrder(DocumentOps.ZMove.ToBack);
    private void OnResetCrop(object sender, RoutedEventArgs e) => _vm.ResetCrop();
    private void OnClearEffects(object sender, RoutedEventArgs e) => _vm.ClearEffects();
    private void OnRenumber(object sender, RoutedEventArgs e) => _vm.RenumberSteps();

    // ================================================================== layout commands

    private void OnVertical(object sender, RoutedEventArgs e) { _vm.Mode = LayoutMode.Vertical; FitSoon(); }
    private void OnHorizontal(object sender, RoutedEventArgs e) { _vm.Mode = LayoutMode.Horizontal; FitSoon(); }
    private void OnFree(object sender, RoutedEventArgs e) => _vm.Mode = LayoutMode.Free;
    private void OnFitCanvas(object sender, RoutedEventArgs e) { _vm.FitCanvas(); FitSoon(); }

    private void OnPresetsOpened(object sender, RoutedEventArgs e)
    {
        PresetsMenu.Items.Clear();
        foreach (var p in _services.LayoutPresets.All)
        {
            var mi = new MenuItem { Header = p.BuiltIn ? p.Name : p.Name + " (custom)" };
            mi.Click += (_, _) => { _vm.ApplyPreset(p); FitSoon(); };
            PresetsMenu.Items.Add(mi);
        }
        var custom = _services.LayoutPresets.All.Where(p => !p.BuiltIn).ToList();
        if (custom.Count == 0) return;
        PresetsMenu.Items.Add(new Separator());
        var del = new MenuItem { Header = "Delete custom preset" };
        foreach (var p in custom)
        {
            var mi = new MenuItem { Header = p.Name };
            mi.Click += (_, _) => { _services.LayoutPresets.Delete(p.Name); _vm.Status = $"Deleted preset {p.Name}."; };
            del.Items.Add(mi);
        }
        PresetsMenu.Items.Add(del);
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.Prompt(this, "Save layout preset", "Preset name (layout and background only, no images):");
        if (name is null) return;
        try
        {
            _services.LayoutPresets.SaveOrReplace(new LayoutPreset(name, _vm.Document.Layout, _vm.Document.Background));
            _vm.Status = $"Saved preset '{name.Trim()}'.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Dialogs.Error(this, ex.Message); }
    }

    private void OnCanvasSize(object sender, RoutedEventArgs e) { SelectTool(ToolKind.Select); ShowInspector(); _inspector.ShowCanvasSection(); }
    private void SyncCanvasBar() => RefreshCanvasWarnings();

    private void OnScaleDocument(object sender, RoutedEventArgs e)
    {
        var area = _vm.Document.ExportArea;
        var request = ScaleDialog.Show(this, area.Width, area.Height);
        if (request is null) return;
        if (request.ScaleContent) _vm.Commit("Scale document", d => DocumentOps.ScaleDocument(d, request.FactorX, request.FactorY));
        else _vm.Commit("Resize canvas", d => DocumentOps.SetExportArea(d, d.ExportArea with { Width = request.Width, Height = request.Height }));
        FitSoon();
    }

    private void OnJoinSeam(object sender, RoutedEventArgs e)
    {
        var doc = _vm.Document;
        var sel = doc.LayoutOrder.Where(_vm.SelectedImages.Contains).Select(id => doc.FindImage(id)!).ToList();
        if (sel.Count != 2) { Dialogs.Info(this, "Select exactly two images to join. The first in combine order stays; repeated rows/columns are removed from the second."); return; }
        var axis = doc.Layout.Mode == LayoutMode.Horizontal ? SeamAxis.Horizontal : SeamAxis.Vertical;
        var (first, second) = (sel[0], sel[1]);
        int suggestion = 1;
        string note = "";
        if (axis == SeamAxis.Vertical && first.HasIdentityOrientation && second.HasIdentityOrientation && first.SourceCrop.Width == second.SourceCrop.Width)
        {
            try
            {
                var a = _services.Cache.GetPixels(first.AssetId).Crop(first.SourceCrop).ToLuma();
                var b = _services.Cache.GetPixels(second.AssetId).Crop(second.SourceCrop with { Y = 0, Height = second.SourceCrop.Bottom }).ToLuma();
                var s = OverlapMatcher.FindVertical(a, b);
                suggestion = Math.Max(1, s.Overlap);
                note = s.IsConfident ? $"\nSuggested overlap: {s.Overlap} rows (match error {s.Error:0.000})." : $"\nNo confident suggestion ({s.Confidence}); check the result.";
            }
            catch (Exception ex) when (ex is AssetNotFoundException or ArgumentException) { }
        }
        var what = axis == SeamAxis.Vertical ? "rows" : "columns";
        var baseSecond = second with
        {
            SourceCrop = axis == SeamAxis.Vertical ? second.SourceCrop with { Y = 0, Height = second.SourceCrop.Bottom } : second.SourceCrop with { X = 0, Width = second.SourceCrop.Right },
        };
        int available = axis == SeamAxis.Vertical ? baseSecond.SourceCrop.Height : baseSecond.SourceCrop.Width;
        var result = SeamDialog.Show(this, available, suggestion, renderPreview: n =>
        {
            try { return _services.Renderer.RenderThumbnail(SeamGeometry.Join(doc, first.Id, second.Id, axis, n), 480); }
            catch (ArgumentException) { return null; }
        }, note: note.Trim(), beforePreview: _services.Renderer.RenderThumbnail(doc, 480));
        if (result is not { } overlap) return;
        if (SeamGeometry.Validate(first, baseSecond, axis, overlap) is { } err) { Dialogs.Error(this, err); return; }
        if (_vm.Commit("Join images", d => SeamGeometry.Join(d, first.Id, second.Id, axis, overlap)))
            _vm.Status = $"Joined with {overlap} overlapping {what} removed. Originals stay editable (crop or undo to adjust).";
    }

    // ================================================================== cut-out

    private async Task CutOutAsync(RectD strip)
    {
        var doc = _vm.Document;
        bool rows = strip.Width >= strip.Height;
        var layer = DocumentOps.HitTestImage(doc, strip.Center);
        // Annotations are canvas objects: overlapping ones are not part of the image, so a plain image
        // layer can always be cut directly; annotations keep their document positions.
        bool simple = layer is not null && layer.HasIdentityOrientation && layer.Effects.Length == 0 && layer.Edge is null;
        try
        {
            if (simple) await CutOutLayerAsync(layer!, strip, rows);
            else await CutOutFlattenedAsync(strip, rows);
        }
        catch (ArgumentOutOfRangeException ex) { _vm.Status = ex.Message.Split('(')[0].Trim(); }
        catch (Exception ex) when (ex is AssetNotFoundException or InvalidOperationException or IOException) { _vm.Status = "Cut out failed: " + ex.Message; }
    }

    private async Task CutOutLayerAsync(ImageLayer layer, RectD strip, bool rows)
    {
        var src = ImageTransform.DocumentRectToSource(layer, strip.Intersect(layer.Bounds.ToRectD())).ToPixelRectRounded().Intersect(layer.SourceCrop);
        if (src.IsEmpty) { _vm.Status = "Drag across the part of the image to remove."; return; }
        var crop = layer.SourceCrop;
        var pixels = await _services.Imaging.InvokeAsync(() =>
        {
            var px = _services.Cache.GetPixels(layer.AssetId).Crop(crop);
            return rows ? StripCutout.RemoveRows(px, src.Y - crop.Y, src.Bottom - crop.Y) : StripCutout.RemoveColumns(px, src.X - crop.X, src.Right - crop.X);
        });
        var asset = await _services.Importer.ImportPixelsAsync(pixels);
        var (sx, sy) = ImageTransform.Scale(layer);
        var b = layer.Bounds;
        var nb = rows
            ? b with { Height = Math.Max(1, MathUtil.RoundAway(pixels.Height * sy)) }
            : b with { Width = Math.Max(1, MathUtil.RoundAway(pixels.Width * sx)) };
        if (_vm.Commit(rows ? "Cut out rows" : "Cut out columns", d => DocumentOps.Reflow(DocumentOps.PruneAssets(DocumentOps.EnsureAsset(d, asset) with
        {
            Images = d.Images.Select(i => i.Id == layer.Id ? i with { AssetId = asset.Id, SourceCrop = asset.FullRect, Bounds = nb } : i).ToArray(),
        }))))
            _vm.Status = $"Removed {(rows ? src.Height : src.Width)} {(rows ? "rows" : "columns")}. Undo restores the original.";
    }

    private async Task CutOutFlattenedAsync(RectD strip, bool rows)
    {
        if (Dialogs.Confirm(this, "Create flattened copy and cut out", "This operation creates a flattened copy. The original images and annotations remain available through Undo.", "Create copy") != MessageBoxResult.Yes) return;
        var doc = _vm.Document;
        var area = doc.ExportArea;
        var s = strip.ToPixelRectRounded().Intersect(area);
        if (s.IsEmpty) { _vm.Status = "The strip is outside the canvas."; return; }
        var flat = await _services.Export.RenderAsync(doc);
        var cut = await _services.Imaging.InvokeAsync(() => rows
            ? StripCutout.RemoveRows(flat, s.Y - area.Y, s.Bottom - area.Y)
            : StripCutout.RemoveColumns(flat, s.X - area.X, s.Right - area.X));
        var asset = await _services.Importer.ImportPixelsAsync(cut);
        var layer = ImageLayer.ForAsset(asset, area.Right + 24, area.Y, "Flattened cut-out");
        if (_vm.Commit("Flattened cut-out", d => DocumentOps.AddImages(DocumentOps.SetMode(d, LayoutMode.Free), [(asset, layer)])))
        {
            _vm.Select([layer.Id]);
            _vm.Status = "Created a flattened copy with the strip removed, placed to the right.";
            FitSoon();
        }
    }

    // ================================================================== capture

    private CaptureOptions BaseOptions() => new()
    {
        DelaySeconds = S.CaptureDelaySeconds, IncludeCursor = S.IncludeCursor, Destination = S.DefaultDestination,
    };

    private void BuildCaptureMenus()
    {
        DestinationMenu.Items.Clear();
        foreach (var (d, label) in new[]
        {
            (CaptureDestination.AppendBelow, "Append below"), (CaptureDestination.AppendRight, "Append right"),
            (CaptureDestination.AddToCanvas, "Add to free canvas"), (CaptureDestination.NewDocument, "New composition"),
            (CaptureDestination.CopyOnly, "Copy to clipboard only"),
        })
        {
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = S.DefaultDestination == d };
            mi.Click += (_, _) => { _services.SaveSettings(S with { DefaultDestination = d }); BuildCaptureMenus(); };
            DestinationMenu.Items.Add(mi);
        }
        DelayMenu.Items.Clear();
        foreach (var s in CaptureOptions.AllowedDelays)
        {
            var mi = new MenuItem { Header = s == 0 ? "No delay" : $"{s} seconds", IsCheckable = true, IsChecked = S.CaptureDelaySeconds == s };
            mi.Click += (_, _) => { _services.SaveSettings(S with { CaptureDelaySeconds = s }); BuildCaptureMenus(); };
            DelayMenu.Items.Add(mi);
        }
        CursorMenu.IsChecked = S.IncludeCursor;
    }

    private void OnToggleCursor(object sender, RoutedEventArgs e) => _services.SaveSettings(S with { IncludeCursor = CursorMenu.IsChecked });

    private void OnCaptureRegion(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.Region);
    private void OnCaptureWindow(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.Window);
    private void OnCaptureMonitor(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.CurrentMonitor);
    private void OnCaptureAll(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.AllMonitors);
    private void OnCaptureLast(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.LastRegion);
    private void OnCaptureEllipse(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.Region, BaseOptions() with { Shape = CaptureShape.Ellipse });
    private void OnCaptureFreehand(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.Region, BaseOptions() with { Shape = CaptureShape.Freehand });
    private void OnCaptureMulti(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.MultiRegion);

    private void OnCaptureFixed(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.Region, pickerAspect: false);
    private void OnCaptureAspect(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureMode.Region, pickerAspect: true);

    /// <summary>Captures first, then routes the result after every overlay has closed.</summary>
    public async Task RunCaptureAsync(CaptureMode mode, CaptureOptions? options = null, CapturePreset? preset = null, bool? pickerAspect = null)
    {
        if (_capture.IsBusy) return;
        Canvas.CancelGesture();
        bool hidden = !IsVisible;
        var o = options ?? BaseOptions();
        var outcome = pickerAspect is { } aspect
            ? await _capture.CaptureWithPickerAsync(aspect, o)
            : await _capture.CaptureAsync(mode, o);
        if (outcome.Status == CaptureStatus.Canceled) { _vm.Status = "Capture canceled."; return; }
        if (outcome.Status == CaptureStatus.Failed) { ShowErrorToast(outcome.Message ?? "Capture failed."); return; }
        if (outcome.Items.Count == 0) return;
        var action = outcome.Action;
        var destination = action switch
        {
            CaptureOverlayAction.Copy => CaptureDestination.CopyOnly,
            CaptureOverlayAction.AppendBelow => CaptureDestination.AppendBelow,
            CaptureOverlayAction.AppendRight => CaptureDestination.AppendRight,
            _ => o.Destination,
        };
        bool outputOnly = action is CaptureOverlayAction.Save or CaptureOverlayAction.Pin or CaptureOverlayAction.Drag;
        bool added = false;
        string? outputPath = null;
        if (outputOnly)
        {
            try { await _vm.StoreCapturesAsync(outcome.Items); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ShowErrorToast(ex.Message); return; }
        }
        else added = await _vm.AddCapturesAsync(outcome.Items, destination, destination != CaptureDestination.AppendRight);
        if (!outputOnly && !_vm.LastCaptureSucceeded) return;
        if (action == CaptureOverlayAction.Save) outputPath = await SaveCapturedImageAsync(outcome.Items[0]);
        else if (action == CaptureOverlayAction.Pin) PinCapture(outcome.Items[0]);
        if (preset is not null) outputPath = await QuickOutputAsync(preset, outcome.Items);
        else if (added && S.CopyAfterCapture) await _vm.CopyImageAsync();
        if (added) { ShowEditor(); FitSoon(); }
        else if ((action == CaptureOverlayAction.Drag || S.DesktopToasts && hidden) && _vm.StatusKind != NotificationKind.Error && !(action == CaptureOverlayAction.Save && outputPath is null))
        {
            var item = outcome.Items[0];
            string title = action == CaptureOverlayAction.Drag ? "Drag the capture to another app" : outputPath is not null ? "Saved to " + Path.GetFileName(outputPath) : action == CaptureOverlayAction.Pin ? "Pinned" : "Copied to clipboard";
            var toast = new DesktopToastWindow(_services, item, title,
                () => _ = EditCaptureAsync(item), () => PinCapture(item), outputPath);
            toast.Show();
            if (action == CaptureOverlayAction.Drag && Mouse.LeftButton == MouseButtonState.Pressed) toast.BeginDrag();
        }
        UpdateRecentProjects();
    }

    private async Task EditCaptureAsync(CaptureItem item)
    {
        var destination = S.DefaultDestination == CaptureDestination.CopyOnly ? CaptureDestination.AddToCanvas : S.DefaultDestination;
        await _vm.AddCapturesAsync([item], destination); ShowEditor(); FitSoon();
    }
    private void PinCapture(CaptureItem item)
    {
        new PinnedImageWindow(item.Pixels.ToBitmap(), "Capture", _services, () => _ = EditCaptureAsync(item)).Show();
        _vm.Notify(NotificationKind.Success, "Pinned");
    }
    private async Task<string?> SaveCapturedImageAsync(CaptureItem item)
    {
        var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "Capture.png", AddExtension = true, InitialDirectory = S.LastExportDirectory ?? "" };
        if (dialog.ShowDialog(IsVisible ? this : null) != true) return null;
        try
        {
            var png = await _services.Imaging.InvokeAsync(item.Pixels.EncodePng);
            await File.WriteAllBytesAsync(dialog.FileName, png);
            _services.SaveSettings(S with { LastExportDirectory = Path.GetDirectoryName(dialog.FileName) });
            _vm.Notify(NotificationKind.Success, "Saved capture", Path.GetFileName(dialog.FileName), new NotificationAction("Show in folder", () => ShowInFolder(dialog.FileName)));
            return dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowErrorToast(ex.Message); return null; }
    }

    private async Task<string?> QuickOutputAsync(CapturePreset preset, IReadOnlyList<CaptureItem> items)
    {
        string? lastPath = null;
        if (preset.OutputFolder is not null)
        {
            try
            {
                Directory.CreateDirectory(preset.OutputFolder);
                foreach (var it in items)
                {
                    var path = CapturePresetStore.ResolveOutputPath(preset, DateTime.Now);
                    var bytes = await _services.Imaging.InvokeAsync(() => preset.OutputFormat == QuickOutputFormat.Jpeg
                        ? it.Pixels.EncodeJpeg(S.JpegQuality, Rgba32.White) : it.Pixels.EncodePng());
                    await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) await fs.WriteAsync(bytes);
                    _vm.Status = $"Saved {Path.GetFileName(path)}."; lastPath = path;
                    _vm.Notify(NotificationKind.Success, "Saved capture", Path.GetFileName(path), new NotificationAction("Show in folder", () => ShowInFolder(path)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowErrorToast($"Quick output failed: {ex.Message}");
            }
        }
        if (preset.CopyToClipboard && items.Count > 0)
        {
            var px = items[0].Pixels;
            var png = await _services.Imaging.InvokeAsync(px.EncodePng);
            var opaque = await _services.Imaging.InvokeAsync(() => px.FlattenOnto(Rgba32.White).ToBitmap());
            var r = await _services.Clipboard.CopyImageAsync(opaque, png);
            if (!r.IsSuccess) ShowErrorToast(r.Message ?? "Copy failed.");
        }
        return lastPath;
    }

    private Task RunPresetAsync(CapturePreset p)
    {
        var o = new CaptureOptions
        {
            DelaySeconds = p.DelaySeconds, IncludeCursor = p.IncludeCursor, Destination = p.Destination, Shape = p.Shape,
            Constraint = new SelectionConstraint { FixedSize = p.FixedSize, AspectRatio = p.AspectRatio },
        };
        if (p.Mode == CaptureMode.Scrolling) { OnScrolling(this, new RoutedEventArgs()); return Task.CompletedTask; }
        return RunCaptureAsync(p.Mode, o, p);
    }

    private void OnCapturePresetsOpened(object sender, RoutedEventArgs e)
    {
        CapturePresetsMenu.Items.Clear();
        foreach (var p in _services.CapturePresets.Presets)
        {
            var mi = new MenuItem { Header = p.Name, InputGestureText = p.Hotkey };
            mi.Click += (_, _) => _ = RunPresetAsync(p);
            CapturePresetsMenu.Items.Add(mi);
        }
        if (CapturePresetsMenu.Items.Count > 0) CapturePresetsMenu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Manage capture presets…" };
        edit.Click += (_, _) => OpenSettingsPage("Capture"); CapturePresetsMenu.Items.Add(edit);
    }

    private void OnScrolling(object sender, RoutedEventArgs e)
    {
        if (_capture.IsBusy || ScrollingCaptureWindow.IsOpen) return;
        new ScrollingCaptureWindow(_services, _capture, _vm, this).Start();
    }

    private void OnInterval(object sender, RoutedEventArgs e)
    {
        if (_capture.IsBusy || IntervalCaptureWindow.IsOpen) return;
        var request = IntervalDialog.Show(this, S.DefaultDestination);
        if (request is null) return;
        new IntervalCaptureWindow(_services, _capture, _vm, this, TimeSpan.FromSeconds(request.Seconds), request.Frames, request.Destination).Start();
    }

    // ================================================================== view and help

    private void OnZoomFit(object sender, RoutedEventArgs e) => Canvas.FitToView();
    private void OnZoom100(object sender, RoutedEventArgs e) => Canvas.ZoomTo(1);
    private void OnZoomIn(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1.25);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Canvas.ZoomBy(0.8);

    private void OnShortcuts(object sender, RoutedEventArgs e)
    {
        RefreshCommandRegistry();
        var entries = _commands.All.Where(c => c.Gesture.Length > 0).Select(c => new ShortcutEntry(c.Category, c.Title, c.Gesture)).ToList();
        entries.AddRange(ActiveHotkeys.Select(kv => new ShortcutEntry("Capture global", kv.Key, kv.Value.ToString())));
        entries.AddRange(new[]
        {
            new ShortcutEntry("Canvas", "Pan", "Space+drag / middle-drag"),
            new ShortcutEntry("Canvas", "Zoom", "Ctrl+wheel"),
            new ShortcutEntry("Canvas", "Cycle selected objects", "Tab / Shift+Tab"),
            new ShortcutEntry("Canvas", "Disable snapping", "Alt+drag"),
            new ShortcutEntry("Capture overlay", "Edit / Copy / Save / Pin", "Enter / Ctrl+C / Ctrl+S / Ctrl+P"),
            new ShortcutEntry("Capture overlay", "Switch mode", "1–6"),
            new ShortcutEntry("Capture overlay", "Nudge selection / edge", "Arrows / Ctrl+Arrows (Shift: 10 px)"),
            new ShortcutEntry("Capture overlay", "Copy pixel hex / RGB; loupe", "C / Shift+C; M"),
            new ShortcutEntry("View", "Cycle regions", "F6 / Shift+F6"),
        });
        new ShortcutsWindow(this, entries, () => OpenSettingsPage("Shortcuts")).Show();
    }

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow(this, _services.Paths.Root).ShowDialog();
}
