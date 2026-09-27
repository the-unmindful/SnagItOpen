using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Library;
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
    private static readonly (ToolKind Kind, string Label, string Tip)[] ToolDefs =
    [
        (ToolKind.Select, "Select", "Select, move and resize (V)"),
        (ToolKind.Crop, "Crop", "Drag over an image to keep that area (C)"),
        (ToolKind.Arrow, "Arrow", "Arrow (A)"),
        (ToolKind.Line, "Line", "Straight line (L)"),
        (ToolKind.Rectangle, "Rect", "Rectangle (R)"),
        (ToolKind.Ellipse, "Ellipse", "Ellipse (E)"),
        (ToolKind.Text, "Text", "Text (T)"),
        (ToolKind.Callout, "Callout", "Text box with a pointer"),
        (ToolKind.Highlight, "Highlight", "Translucent highlighter (H)"),
        (ToolKind.Step, "Step", "Numbered step (N)"),
        (ToolKind.Freehand, "Pen", "Freehand drawing (P)"),
        (ToolKind.Redaction, "Redact", "Opaque redaction box (secure in exported images)"),
        (ToolKind.Blur, "Blur", "Blur part of an image (visual only, not secure)"),
        (ToolKind.Pixelate, "Pixelate", "Pixelate part of an image (visual only, not secure)"),
        (ToolKind.Magnifier, "Magnify", "Drag over a detail to show it enlarged"),
        (ToolKind.Stamp, "Stamp", "Click to place a symbol"),
        (ToolKind.CutOut, "Cut out", "Drag across a strip to remove it (wide drag removes rows, tall drag removes columns)"),
    ];

    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly CaptureCoordinator _capture;
    private readonly Dictionary<ToolKind, RadioButton> _toolButtons = [];
    private readonly AnnotationPropertiesPanel _props;
    private bool _syncing, _exiting, _trayHintShown, _propsPending;
    private Point _listDragStart;
    private ImageListItemViewModel? _listDragItem;
    private GlobalHotkeyService? _hotkeys;
    private TrayService? _tray;

    public MainWindow(AppServices services, EditorViewModel vm, CaptureCoordinator capture)
    {
        _services = services;
        _vm = vm;
        _capture = capture;
        InitializeComponent();
        DataContext = vm;

        AlignBox.ItemsSource = Enum.GetValues<CrossAlignment>();
        Canvas.ViewModel = vm;
        Canvas.StyleProvider = k => _services.ToolStyles.Get(k.ToString(), DefaultStyle(k));
        Canvas.PrototypeProvider = k => _services.AnnotationStyles.Prototype(k.ToString());
        _props = new AnnotationPropertiesPanel(services, vm, () => Canvas.Tool, k => _services.ToolStyles.Get(k.ToString(), DefaultStyle(k)));
        AnnotationPanelHost.Content = _props;
        // Changes made while a panel field had focus (e.g. undo) show once focus leaves the panel.
        _props.IsKeyboardFocusWithinChanged += (_, e) => { if (e.NewValue is false) RefreshPropsSoon(); };
        Canvas.SnapEnabled = _services.Settings.SnapEnabled;
        SnapBox.IsChecked = _services.Settings.SnapEnabled;
        Canvas.ViewChanged += () => ZoomText.Text = $"{Canvas.Zoom:P0}";
        Canvas.EditTextRequested += OnEditText;
        Canvas.ViewChanged += PositionTextEditor;
        Canvas.ContextMenuOpening += OnCanvasContextMenu;
        Canvas.ContextMenu = new ContextMenu(); // enables ContextMenuOpening; replaced on open
        Canvas.PreviewMouseDown += (_, _) => FinishTextEdit(commit: true);
        Canvas.CutOutRequested += r => _ = CutOutAsync(r);

        BuildToolBar();
        BuildCaptureMenus();
        GalleryMenu.IsChecked = _services.Settings.ShowCaptureGallery;
        SetGalleryVisible(_services.Settings.ShowCaptureGallery);
        SelectTool(ToolKind.Select);

        vm.ErrorRaised += msg => Dialogs.Error(this, msg);
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
        if (S.ShowTrayIcon) CreateTray();
    }

    private void CreateTray()
    {
        _tray?.Dispose();
        _tray = new TrayService("SnagItOpen", [
            new TrayMenuItem("Open editor", ShowEditor),
            TrayMenuItem.Separator,
            new TrayMenuItem("Capture region", () => Dispatcher.BeginInvoke(() => _ = RunCaptureAsync(CaptureMode.Region))),
            new TrayMenuItem("Capture window", () => Dispatcher.BeginInvoke(() => _ = RunCaptureAsync(CaptureMode.Window))),
            new TrayMenuItem("Capture all monitors", () => Dispatcher.BeginInvoke(() => _ = RunCaptureAsync(CaptureMode.AllMonitors))),
            new TrayMenuItem("Scrolling capture", () => Dispatcher.BeginInvoke(() => OnScrolling(this, new RoutedEventArgs()))),
            TrayMenuItem.Separator,
            new TrayMenuItem("Quit SnagItOpen", () => Dispatcher.BeginInvoke(ExitApplication)),
        ], ShowEditor);
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
            if (!r.IsSuccess) problems.Add($"{name}: {r.Message}");
        }
        if (problems.Count > 0) _vm.Status = "Some hotkeys are unavailable: " + string.Join(" ", problems);
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
        if (!_exiting && S.CloseToTray && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown) { _tray.ShowBalloon("SnagItOpen is still running", "Use the tray icon or hotkeys to capture. Choose Quit from the tray menu to exit."); _trayHintShown = true; }
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
        Application.Current.Shutdown();
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
        var r = MessageBox.Show(this, "Save changes to the current composition?", "SnagItOpen",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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

    private void BuildToolBar()
    {
        foreach (var (kind, label, tip) in ToolDefs)
        {
            var b = new RadioButton { Content = label, GroupName = "tool", ToolTip = tip, Margin = new Thickness(1), Padding = new Thickness(6, 2, 6, 2) };
            System.Windows.Automation.AutomationProperties.SetName(b, label + " tool");
            b.Click += (_, _) => SelectTool(kind);
            _toolButtons[kind] = b;
            ToolsBar.Items.Add(b);
        }
    }

    private void SelectTool(ToolKind kind)
    {
        Canvas.CancelGesture();
        Canvas.Tool = kind;
        if (_toolButtons.TryGetValue(kind, out var b)) b.IsChecked = true;
        if (kind != ToolKind.Select && _vm.SelectedAnnotations.Count > 0) _vm.Select(_vm.SelectedImages, []);
        RefreshPropsSoon();
        _vm.Status = ToolDefs.First(t => t.Kind == kind).Tip;
    }

    /// <summary>Rebuilds the properties panel once per dispatcher cycle (never while a panel field has focus).</summary>
    private void RefreshPropsSoon()
    {
        if (_propsPending) return;
        _propsPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _propsPending = false;
            if (_props.IsKeyboardFocusWithin) return;
            _props.Refresh();
        }, System.Windows.Threading.DispatcherPriority.Background);
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

    private void OnSnapChanged(object sender, RoutedEventArgs e) => Canvas.SnapEnabled = SnapBox.IsChecked == true;

    // ================================================================== in-place text editing

    private TextBox? _textBox;
    private TextAnnotation? _textTarget;
    private bool _textIsNew, _textClosing;

    /// <summary>
    /// Opens an on-canvas text box over the annotation (IME works as in any TextBox). Ctrl+Enter or clicking
    /// elsewhere commits; Escape cancels. Tool shortcuts are ignored while it has focus.
    /// </summary>
    private void OnEditText(TextAnnotation t, bool isNew)
    {
        FinishTextEdit(commit: true);
        if (t.Locked) { _vm.Status = "Unlock the text to edit it."; return; }
        _textTarget = t;
        _textIsNew = isNew;
        var color = t is CalloutAnnotation c ? c.TextColor : t.Color;
        var tb = new TextBox
        {
            Text = t.Text, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
            Padding = new Thickness(0), Foreground = new SolidColorBrush(color.ToColor()),
            Background = new SolidColorBrush(t.Fill is { A: > 0 } f ? f.ToColor() : Color.FromArgb(200, 255, 255, 255)),
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(t.FontFamily) ? "Segoe UI" : t.FontFamily),
            FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = t.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextAlignment = t.Alignment switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
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

    private void OnApplyEdge(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(EdgeBorder.Text, out var bw) || !int.TryParse(EdgeShadow.Text, out var sh) || !int.TryParse(EdgeRadius.Text, out var cr)
            || bw is < 0 or > 100 || sh is < 0 or > 100 || cr is < 0 or > 500)
        {
            _vm.Status = "Border and shadow must be 0–100 px, corner radius 0–500 px.";
            return;
        }
        _vm.SetEdge(new EdgeStyle
        {
            BorderWidth = bw, ShadowSize = sh, CornerRadius = cr,
            TornSides = EdgeTorn.IsChecked == true ? TornSides.Bottom : TornSides.None,
            TornSeed = Random.Shared.Next(1, 1_000_000),
        });
    }

    private void OnRemoveEdge(object sender, RoutedEventArgs e) => _vm.SetEdge(null);

    // ================================================================== keyboard

    private static bool IsTyping() => Keyboard.FocusedElement is TextBox or ComboBox { IsEditable: true } or PasswordBox;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTyping()) return;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool handled = true;
        switch (key)
        {
            case Key.N when ctrl: OnNew(this, e); break;
            case Key.O when ctrl && shift: OnOpenProject(this, e); break;
            case Key.O when ctrl: OnImport(this, e); break;
            case Key.S when ctrl && shift: OnSaveAs(this, e); break;
            case Key.S when ctrl: OnSave(this, e); break;
            case Key.E when ctrl: OnExport(this, e); break;
            case Key.C when ctrl && shift: OnCopy(this, e); break;
            case Key.C when ctrl && _vm.SelectedAnnotations.Count > 0: CopyAnnotationsToClipboard(); break;
            case Key.C when ctrl: OnCopy(this, e); break;
            case Key.X when ctrl && _vm.SelectedAnnotations.Count > 0: CopyAnnotationsToClipboard(); _vm.CutAnnotations(); break;
            case Key.V when ctrl && ClipboardHasAnnotations() && _vm.PasteAnnotations(): break;
            case Key.V when ctrl: OnPaste(this, e); break;
            case Key.D when ctrl && _vm.SelectedAnnotations.Count > 0 && _vm.SelectedImages.Count == 0: _vm.DuplicateAnnotations(); break;
            case Key.OemPlus or Key.Add when !ctrl && _vm.SelectedAnnotations.Count > 0: _vm.AdjustStepNumbers(1); break;
            case Key.OemMinus or Key.Subtract when !ctrl && _vm.SelectedAnnotations.Count > 0: _vm.AdjustStepNumbers(-1); break;
            case Key.Tab when !ctrl && Canvas.IsKeyboardFocusWithin && _vm.Document.Annotations.Length > 0: CycleAnnotation(shift ? -1 : 1); break;
            case Key.Z when ctrl && shift: _vm.Redo(); break;
            case Key.Z when ctrl: _vm.Undo(); break;
            case Key.Y when ctrl: _vm.Redo(); break;
            case Key.D when ctrl: _vm.Duplicate(); break;
            case Key.A when ctrl: _vm.SelectAll(); break;
            case Key.D0 or Key.NumPad0 when ctrl: Canvas.FitToView(); break;
            case Key.D1 or Key.NumPad1 when ctrl: Canvas.ZoomTo(1); break;
            case Key.OemPlus or Key.Add when ctrl: Canvas.ZoomBy(1.25); break;
            case Key.OemMinus or Key.Subtract when ctrl: Canvas.ZoomBy(0.8); break;
            case Key.Delete: _vm.RemoveSelected(); break;
            case Key.Escape:
                if (!Canvas.CancelGesture())
                {
                    if (Canvas.Tool != ToolKind.Select) SelectTool(ToolKind.Select);
                    else _vm.ClearSelection();
                }
                break;
            case Key.Up or Key.Down when mods == ModifierKeys.Alt && _vm.PrimaryImage is { } id:
                _vm.MoveInOrder(id, key == Key.Up ? -1 : 1); break;
            case Key.Left or Key.Right or Key.Up or Key.Down when !ImageList.IsKeyboardFocusWithin && _vm.HasSelection && !ctrl:
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
        var anns = _vm.Document.Annotations;
        int cur = _vm.PrimaryAnnotation is { } a ? Array.FindIndex(anns, x => x.Id == a.Id) : -1;
        int next = cur < 0 ? (dir > 0 ? 0 : anns.Length - 1) : ((cur + dir) % anns.Length + anns.Length) % anns.Length;
        _vm.Select([], [anns[next].Id]);
        _vm.Status = $"{anns[next].Kind} {next + 1} of {anns.Length} selected (Tab / Shift+Tab to move).";
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
        ToolKind? t = key switch
        {
            Key.V => ToolKind.Select, Key.C => ToolKind.Crop, Key.A => ToolKind.Arrow, Key.L => ToolKind.Line,
            Key.R => ToolKind.Rectangle, Key.E => ToolKind.Ellipse, Key.T => ToolKind.Text, Key.H => ToolKind.Highlight,
            Key.N => ToolKind.Step, Key.P => ToolKind.Freehand, _ => null,
        };
        if (t is null) return false;
        SelectTool(t.Value);
        return true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) Canvas.SetSpace(false);
    }

    // ================================================================== drag & drop

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f ? f : [];

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(CaptureDrag.Format) || DroppedFiles(e).Length > 0 ? DragDropEffects.Copy
            : e.Data.GetDataPresent(ImageIdFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
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
            new PinnedImageWindow(px.ToBitmap(), _vm.Document.Name).Show();
        }
        catch (InvalidOperationException ex) { _vm.Status = ex.Message; }
    }

    private void OnLibrary(object sender, RoutedEventArgs e) => OpenLibrary();

    private void OpenLibrary()
    {
        var open = OwnedWindows.OfType<LibraryWindow>().FirstOrDefault();
        if (open is not null) { open.Activate(); return; }
        new LibraryWindow(_services, _vm) { Owner = this }.Show();
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

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_services) { Owner = this };
        if (w.ShowDialog() != true) return;
        ApplyHotkeys();
        if (S.ShowTrayIcon && _tray is null) CreateTray();
        else if (!S.ShowTrayIcon && _tray is not null) { _tray.Dispose(); _tray = null; }
        BuildCaptureMenus();
    }

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

    private void OnCanvasSize(object sender, RoutedEventArgs e)
    {
        var a = _vm.Document.ExportArea;
        var text = Dialogs.Prompt(this, "Canvas size", "Canvas rectangle in document pixels (x, y, width, height). Content is not moved or scaled:", $"{a.X}, {a.Y}, {a.Width}, {a.Height}");
        if (text is null) return;
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, out _))) { _vm.Status = "Enter four whole numbers: x, y, width, height."; return; }
        var n = parts.Select(int.Parse).ToArray();
        _vm.SetCanvas(new PixelRect(n[0], n[1], n[2], n[3]));
        FitSoon();
    }

    private void OnScaleDocument(object sender, RoutedEventArgs e)
    {
        var text = Dialogs.Prompt(this, "Scale document", "Scale content and canvas by percent (e.g. 50 or 200):", "100");
        if (text is null) return;
        if (!double.TryParse(text.Trim().TrimEnd('%'), out var pct) || pct is < 1 or > 1000) { _vm.Status = "Enter a percentage between 1 and 1000."; return; }
        if (Math.Abs(pct - 100) < 1e-9) return;
        _vm.ScaleDocument(pct / 100);
        FitSoon();
    }

    private void OnJoinSeam(object sender, RoutedEventArgs e)
    {
        var doc = _vm.Document;
        var sel = doc.LayoutOrder.Where(_vm.SelectedImages.Contains).Select(id => doc.FindImage(id)!).ToList();
        if (sel.Count != 2) { Dialogs.Info(this, "Select exactly two images to join. The first in combine order stays; repeated rows/columns are removed from the second."); return; }
        var axis = doc.Layout.Mode == LayoutMode.Horizontal ? SeamAxis.Horizontal : SeamAxis.Vertical;
        var (first, second) = (sel[0], sel[1]);
        string suggestion = "0";
        string note = "";
        if (axis == SeamAxis.Vertical && first.HasIdentityOrientation && second.HasIdentityOrientation && first.SourceCrop.Width == second.SourceCrop.Width)
        {
            try
            {
                var a = _services.Cache.GetPixels(first.AssetId).Crop(first.SourceCrop).ToLuma();
                var b = _services.Cache.GetPixels(second.AssetId).Crop(second.SourceCrop with { Y = 0, Height = second.SourceCrop.Bottom }).ToLuma();
                var s = OverlapMatcher.FindVertical(a, b);
                suggestion = s.Overlap.ToString();
                note = s.IsConfident ? $"\nSuggested overlap: {s.Overlap} rows (match error {s.Error:0.000})." : $"\nNo confident suggestion ({s.Confidence}); check the result.";
            }
            catch (Exception ex) when (ex is AssetNotFoundException or ArgumentException) { }
        }
        var what = axis == SeamAxis.Vertical ? "rows" : "columns";
        var text = Dialogs.Prompt(this, "Join overlapping images", $"Repeated {what} to remove from the start of the second image:{note}", suggestion);
        if (text is null) return;
        if (!int.TryParse(text, out var overlap)) { _vm.Status = "Enter a whole number."; return; }
        var baseSecond = second with
        {
            SourceCrop = axis == SeamAxis.Vertical ? second.SourceCrop with { Y = 0, Height = second.SourceCrop.Bottom } : second.SourceCrop with { X = 0, Width = second.SourceCrop.Right },
        };
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
        var r = MessageBox.Show(this,
            "This area includes annotations, effects or a rotated image, which cannot be remapped across a removed strip.\n\nCreate a flattened copy of the composition with the strip removed? The original stays editable.",
            "Create flattened copy and cut out", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
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

    private void OnCaptureFixed(object sender, RoutedEventArgs e)
    {
        var text = Dialogs.Prompt(this, "Fixed-size capture", "Size in physical pixels (width x height):", "640x480");
        if (text is null) return;
        var p = text.ToLowerInvariant().Split(['x', '×', ',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 2 || !int.TryParse(p[0], out var w) || !int.TryParse(p[1], out var h) || w < 1 || h < 1 || !Limits.IsAcceptableImageSize(w, h))
        { _vm.Status = "Enter a size such as 640x480."; return; }
        _ = RunCaptureAsync(CaptureMode.Region, BaseOptions() with { Constraint = new SelectionConstraint { FixedSize = new PixelSize(w, h) } });
    }

    private void OnCaptureAspect(object sender, RoutedEventArgs e)
    {
        var text = Dialogs.Prompt(this, "Fixed aspect capture", "Aspect ratio (width:height):", "16:9");
        if (text is null) return;
        var p = text.Split([':', '/', 'x', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 2 || !double.TryParse(p[0], out var a) || !double.TryParse(p[1], out var b) || a <= 0 || b <= 0 || a / b is < 0.01 or > 100)
        { _vm.Status = "Enter a ratio such as 16:9."; return; }
        _ = RunCaptureAsync(CaptureMode.Region, BaseOptions() with { Constraint = new SelectionConstraint { AspectRatio = a / b } });
    }

    /// <summary>Runs one capture and routes it into the composition. Never changes anything on cancel.</summary>
    public async Task RunCaptureAsync(CaptureMode mode, CaptureOptions? options = null, CapturePreset? preset = null)
    {
        if (_capture.IsBusy) return;
        Canvas.CancelGesture();
        var o = options ?? BaseOptions();
        var outcome = await _capture.CaptureAsync(mode, o);
        if (outcome.Status == CaptureStatus.Canceled) { _vm.Status = "Capture canceled."; return; }
        if (outcome.Status == CaptureStatus.Failed)
        {
            _vm.Status = outcome.Message;
            if (IsVisible) Dialogs.Error(this, outcome.Message ?? "Capture failed.");
            else _tray?.ShowBalloon("Capture failed", outcome.Message ?? "");
            return;
        }
        bool vertical = o.Destination != CaptureDestination.AppendRight;
        bool added = await _vm.AddCapturesAsync(outcome.Items, o.Destination, vertical);
        if (preset is not null) await QuickOutputAsync(preset, outcome.Items);
        else if (added && S.CopyAfterCapture) await _vm.CopyImageAsync();
        if (added)
        {
            ShowEditor();
            FitSoon();
        }
    }

    private async Task QuickOutputAsync(CapturePreset preset, IReadOnlyList<CaptureItem> items)
    {
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
                    _vm.Status = $"Saved {Path.GetFileName(path)}.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _vm.Status = $"Quick output failed: {ex.Message}";
            }
        }
        if (preset.CopyToClipboard && items.Count > 0)
        {
            var px = items[0].Pixels;
            var png = await _services.Imaging.InvokeAsync(px.EncodePng);
            var opaque = await _services.Imaging.InvokeAsync(() => px.FlattenOnto(Rgba32.White).ToBitmap());
            var r = await _services.Clipboard.CopyImageAsync(opaque, png);
            if (!r.IsSuccess) _vm.Status = r.Message;
        }
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
        var save = new MenuItem { Header = "Save current capture settings as preset…" };
        save.Click += (_, _) => SaveCapturePreset();
        CapturePresetsMenu.Items.Add(save);
        var edit = new MenuItem { Header = "Edit presets file (output folder, hotkeys)…" };
        edit.Click += (_, _) => EditPresetsFile();
        CapturePresetsMenu.Items.Add(edit);
        var reload = new MenuItem { Header = "Reload presets" };
        reload.Click += (_, _) => { _services.CapturePresets.Load(); ApplyHotkeys(); _vm.Status = _services.CapturePresets.LastWarning ?? $"Loaded {_services.CapturePresets.Presets.Count} capture presets."; };
        CapturePresetsMenu.Items.Add(reload);
    }

    private void SaveCapturePreset()
    {
        var name = Dialogs.Prompt(this, "Capture preset", "Preset name (uses the current delay, cursor and destination for a region capture):");
        if (name is null) return;
        var list = _services.CapturePresets.Presets.Where(p => !string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(new CapturePreset
        {
            Name = name.Trim(), Mode = CaptureMode.Region, DelaySeconds = S.CaptureDelaySeconds,
            IncludeCursor = S.IncludeCursor, Destination = S.DefaultDestination,
        });
        try { _services.CapturePresets.Save(list); _vm.Status = $"Saved capture preset '{name.Trim()}'."; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Dialogs.Error(this, ex.Message); }
    }

    private void EditPresetsFile()
    {
        var path = _services.Paths.CapturePresets;
        if (!File.Exists(path)) _services.CapturePresets.Save(_services.CapturePresets.Presets);
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception ex) { Dialogs.Error(this, ex.Message); }
        _vm.Status = "Edit the presets file, save it, then choose Reload presets. Fields: name, mode, delaySeconds, destination, hotkey, outputFolder, fileNameTemplate, outputFormat, copyToClipboard.";
    }

    private void OnScrolling(object sender, RoutedEventArgs e)
    {
        if (_capture.IsBusy || ScrollingCaptureWindow.IsOpen) return;
        new ScrollingCaptureWindow(_services, _capture, _vm, this).Start();
    }

    private void OnInterval(object sender, RoutedEventArgs e)
    {
        if (_capture.IsBusy || IntervalCaptureWindow.IsOpen) return;
        var text = Dialogs.Prompt(this, "Interval capture", "Seconds between captures (1–60) and maximum frames (1–100):", "5, 20");
        if (text is null) return;
        var p = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 2 || !int.TryParse(p[0], out var sec) || !int.TryParse(p[1], out var max) || sec is < 1 or > 60 || max is < 1 or > 100)
        { _vm.Status = "Enter an interval of 1–60 seconds and 1–100 frames, e.g. 5, 20."; return; }
        new IntervalCaptureWindow(_services, _capture, _vm, this, TimeSpan.FromSeconds(sec), max).Start();
    }

    // ================================================================== view and help

    private void OnZoomFit(object sender, RoutedEventArgs e) => Canvas.FitToView();
    private void OnZoom100(object sender, RoutedEventArgs e) => Canvas.ZoomTo(1);
    private void OnZoomIn(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1.25);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Canvas.ZoomBy(0.8);

    private void OnShortcuts(object sender, RoutedEventArgs e)
    {
        var hk = string.Join("\n", ActiveHotkeys.Select(kv => $"  {kv.Value}   {kv.Key}"));
        Dialogs.Info(this,
            "Editor\n" +
            "  Ctrl+O import · Ctrl+Shift+O open project · Ctrl+S save · Ctrl+Shift+S save as\n" +
            "  Ctrl+V paste · Ctrl+Shift+C copy image · Ctrl+E export\n" +
            "  Ctrl+Z undo · Ctrl+Y redo · Ctrl+D duplicate · Delete remove · Ctrl+A select all\n" +
            "  Arrows nudge 1 px (Shift: 10 px) · Alt+Up/Down reorder · F2 rename (image list)\n" +
            "  Ctrl+wheel zoom · Space+drag or middle-drag pan · Ctrl+0 fit · Ctrl+1 100%\n" +
            "  Shift while resizing unlocks aspect · Alt while moving disables snapping · Esc cancels\n" +
            "  Tools: V select, C crop, A arrow, L line, R rectangle, E ellipse, T text, H highlight, N step, P pen\n\n" +
            "Capture selection\n" +
            "  Drag or click a window · arrows move 1 px (Shift: 10) · Space press/release · Enter confirm · Esc cancel\n\n" +
            "Global hotkeys\n" + (hk.Length == 0 ? "  (none registered)" : hk));
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        Dialogs.Info(this, $"SnagItOpen {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n\n" +
            "Local screen capture and image combining for Windows. Everything stays on this computer.\n" +
            "Not affiliated with TechSmith.\n\n" +
            $"Data folder: {_services.Paths.Root}\n" +
            $"Capture history: {_services.History.Entries.Count} items, {_services.History.TotalBytes / (1024.0 * 1024):0.0} MiB");
}
