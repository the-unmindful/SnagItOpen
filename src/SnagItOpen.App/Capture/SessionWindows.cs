using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.App.Shell.DialogWindows;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Imaging.Effects;
using SnagItOpen.Windows.Capture;

namespace SnagItOpen.App.Capture;

/// <summary>
/// Shared small topmost control panel for capture sessions. It is excluded from screen capture where
/// Windows supports it, placed beside the target region, and minimizes the editor while running.
/// </summary>
internal abstract class SessionPanel : Window
{
    protected readonly AppServices Services;
    protected readonly CaptureCoordinator Coordinator;
    protected readonly EditorViewModel Vm;
    private readonly Window _owner;
    private WindowState _ownerState;
    protected readonly TextBlock StatusText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Margin = new Thickness(0, 0, 0, 8) };
    protected readonly WrapPanel Buttons = new() { MaxWidth = 320 };
    protected PixelRect Region;
    protected bool TopologyChanged;

    protected SessionPanel(AppServices services, CaptureCoordinator coordinator, EditorViewModel vm, Window owner, string title)
    {
        Services = services;
        Coordinator = coordinator;
        Vm = vm;
        _owner = owner;
        Title = title;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = true;
        FontFamily = new FontFamily("Segoe UI");
        SetResourceReference(BackgroundProperty, "Bg.Window");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(StatusText, AutomationLiveSetting.Polite);
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(StatusText);
        root.Children.Add(Buttons);
        Content = root;
        SourceInitialized += (_, _) => AppNative.ExcludeFromCapture(this);
        services.Monitors.TopologyChanged += OnTopology;
    }

    protected Button AddButton(string text, Action onClick, string? tip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3), ToolTip = tip };
        AutomationProperties.SetName(b, text);
        if (tip is not null) AutomationProperties.SetHelpText(b, tip);
        b.Click += (_, _) => onClick();
        Buttons.Children.Add(b);
        return b;
    }

    protected void SetStatus(string s) => StatusText.Text = s;

    private void OnTopology(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        TopologyChanged = true;
        OnTopologyChanged();
    });

    protected virtual void OnTopologyChanged() { }

    /// <summary>Lets the user select the target region. Returns false when canceled.</summary>
    protected async Task<bool> PickAsync(string hint)
    {
        var r = await Coordinator.PickRegionAsync(hint);
        if (r is null) return false;
        Region = r.Value;
        _ownerState = _owner.WindowState;
        _owner.WindowState = WindowState.Minimized;
        return true;
    }

    /// <summary>Shows the panel next to (not over) the region, in physical pixels.</summary>
    protected void ShowBesideRegion()
    {
        Show();
        UpdateLayout();
        var src = PresentationSource.FromVisual(this);
        double s = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        int w = (int)Math.Ceiling(ActualWidth * s), h = (int)Math.Ceiling(ActualHeight * s);
        var monitors = Services.Monitors.GetMonitors();
        var desktop = MonitorTopology.Dominant(monitors, Region)?.WorkArea ?? MonitorTopology.VirtualBounds(monitors);
        int x = Region.Right + 12 + w <= desktop.Right ? Region.Right + 12
              : Region.X - 12 - w >= desktop.X ? Region.X - 12 - w
              : Math.Max(desktop.X, Region.Right - w);
        int y = Math.Clamp(Region.Y, desktop.Y, Math.Max(desktop.Y, desktop.Bottom - h));
        AppNative.PlaceTopmost(this, new PixelRect(x, y, w, h), activate: false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnEscape(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected abstract void OnEscape();

    protected override void OnClosed(EventArgs e)
    {
        Services.Monitors.TopologyChanged -= OnTopology;
        if (_owner.WindowState == WindowState.Minimized) _owner.WindowState = _ownerState == WindowState.Minimized ? WindowState.Normal : _ownerState;
        _owner.Activate();
        base.OnClosed(e);
    }
}

/// <summary>
/// Scrolling capture. Guided: the user scrolls and presses "Capture next". Automatic: bounded wheel input
/// is sent only while the selected target stays in the foreground (clicking elsewhere stops it).
/// Uncertain seams ask for a manual overlap. Finishing adds all frames as separately editable, cropped
/// layers in one undoable step; no giant bitmap is built during acquisition.
/// </summary>
internal sealed class ScrollingCaptureWindow : SessionPanel
{
    public static bool IsOpen { get; private set; }

    private readonly TextBox _header = new() { Text = "0", Width = 48 };
    private readonly TextBox _footer = new() { Text = "0", Width = 48 };
    private readonly Button _next, _auto, _stop, _finish;
    private ScrollingSession<PixelBuffer>? _session;
    private CancellationTokenSource? _autoCts;
    private IntPtr _target;
    private bool _busy, _finished;

    public ScrollingCaptureWindow(AppServices services, CaptureCoordinator coordinator, EditorViewModel vm, Window owner)
        : base(services, coordinator, vm, owner, "Scrolling capture")
    {
        var margins = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        margins.Children.Add(new Label { Content = "Fixed header rows", Target = _header, Padding = new Thickness(0, 3, 4, 0) });
        margins.Children.Add(_header);
        margins.Children.Add(new Label { Content = "footer", Target = _footer, Padding = new Thickness(8, 3, 4, 0) });
        margins.Children.Add(_footer);
        AutomationProperties.SetName(_header, "Fixed header rows to ignore");
        AutomationProperties.SetName(_footer, "Fixed footer rows to ignore");
        ((StackPanel)Content).Children.Insert(1, margins);

        _next = AddButton("Capture next", () => _ = CaptureNextAsync(), "Scroll the content yourself, then capture the next frame");
        _auto = AddButton("Auto scroll", () => _ = AutoAsync(), "Scrolls the selected window automatically until the end");
        _stop = AddButton("Stop", StopAuto);
        _finish = AddButton("Finish", () => _ = FinishAsync());
        AddButton("Cancel", CancelSession);
        UpdateButtons();
    }

    public async void Start()
    {
        IsOpen = true;
        if (!await PickAsync("Select the scrolling content area (without scrollbars). Esc cancels."))
        {
            IsOpen = false;
            Vm.Status = "Scrolling capture canceled.";
            return;
        }
        var center = new PixelPoint(Region.X + Region.Width / 2, Region.Y + Region.Height / 2);
        _target = WindowCatalog.RootAt(center);
        SetStatus("Capturing the first frame…");
        ShowBesideRegion();
        await CaptureNextAsync();
    }

    private void EnsureSession()
    {
        if (_session is not null) return;
        int header = int.TryParse(_header.Text, out var hh) ? Math.Clamp(hh, 0, Region.Height / 3) : 0;
        int footer = int.TryParse(_footer.Text, out var ff) ? Math.Clamp(ff, 0, Region.Height / 3) : 0;
        _header.IsEnabled = _footer.IsEnabled = false;
        _session = new ScrollingSession<PixelBuffer>(p => p.ToLuma(), new ScrollingOptions
        {
            Overlap = new Core.Stitching.OverlapOptions { HeaderRows = header, FooterRows = footer },
        });
    }

    private void UpdateButtons()
    {
        bool running = _autoCts is not null;
        _next.IsEnabled = !_busy && !running && _session?.StopReason is null or ScrollStopReason.None;
        _auto.IsEnabled = _next.IsEnabled && _session is { Frames.Count: > 0 };
        _stop.IsEnabled = running;
        _finish.IsEnabled = !running && !_busy && _session is { Frames.Count: > 0 };
    }

    private string Progress() => _session is null ? "" : $"{_session.Frames.Count} frame(s), {_session.OutputHeight} px tall.";

    private async Task CaptureNextAsync()
    {
        if (_busy || _finished) return;
        _busy = true;
        UpdateButtons();
        try
        {
            EnsureSession();
            if (_session!.Frames.Count >= _session.Options.MaxFrames) { SetStatus($"Frame limit reached. {Progress()} Click Finish."); return; }
            var px = await Coordinator.CaptureRectAsync(Region, false);
            switch (_session.Offer(px))
            {
                case FrameVerdict.Accepted:
                    SetStatus($"{Progress()} Scroll the content and click Capture next, try Auto scroll, or Finish.");
                    break;
                case FrameVerdict.Unchanged:
                    SetStatus($"The content did not change, so the end was probably reached. {Progress()} Finish, or scroll and capture again.");
                    break;
                case FrameVerdict.LowConfidence:
                    ResolvePending();
                    break;
                case FrameVerdict.SizeLimit:
                    SetStatus($"The combined image reached the size limit. {Progress()} Click Finish.");
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            SetStatus("Capture failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>Manual seam correction for an uncertain match; accepted content is always kept.</summary>
    private void ResolvePending()
    {
        if (_session?.Pending is not { } pending || _session.Frames.Count == 0) return;
        var previous = _session.Frames[^1].Frame;
        var m = _session.PendingMatch;
        string why = m?.Confidence switch
        {
            Core.Stitching.OverlapConfidence.LowTexture => "the content has too little detail",
            Core.Stitching.OverlapConfidence.Ambiguous => "several positions match equally well",
            _ => "no matching rows were found",
        };
        var answer = SeamDialog.Show(this, pending.Height, m is { Overlap: > 0 } ? m.Overlap : 0,
            renderPreview: overlap => ScrollingSeamPreview.Build(previous, pending, overlap).ToBitmap(),
            note: $"The overlap could not be determined because {why}. Cancel skips this frame.", allowZero: true,
            beforePreview: ScrollingSeamPreview.Build(previous, pending, 0).ToBitmap());
        if (answer is { } n && _session.AcceptPending(n))
            SetStatus($"Frame added with {n} overlapping rows. {Progress()}");
        else
            SetStatus($"Frame skipped. {Progress()} Scroll and capture again, or Finish.");
        _session.Resume();
    }

    private async Task AutoAsync()
    {
        if (_session is null || _autoCts is not null) return;
        if (_target == IntPtr.Zero || !WindowCatalog.Exists(_target)) { SetStatus("The window under the region is gone. Use Capture next instead."); return; }
        _autoCts = new CancellationTokenSource();
        UpdateButtons();
        SetStatus("Scrolling… Click Stop (or anywhere outside the target) to stop.");
        var center = new PixelPoint(Region.X + Region.Width / 2, Region.Y + Region.Height / 2);
        var scroller = new ScrollInputService();
        try
        {
            ScrollInputService.Activate(_target);
            await Task.Delay(250, _autoCts.Token);
            var reason = await _session.RunAutomaticAsync(
                ct => Coordinator.CaptureRectAsync(Region, false, ct),
                () => !TopologyChanged && scroller.ScrollDown(center, _session.Options.WheelNotches, _target),
                (t, c) => Task.Delay(t, c),
                () => DateTimeOffset.Now,
                _autoCts.Token);
            if (reason == ScrollStopReason.LowConfidence) ResolvePending();
            else SetStatus(reason switch
            {
                ScrollStopReason.Unchanged => $"Reached the end. {Progress()} Click Finish.",
                ScrollStopReason.TargetLost => TopologyChanged ? $"Displays changed; stopped. {Progress()}" : $"Stopped because another window took focus. {Progress()}",
                ScrollStopReason.Unstable => $"The content kept changing (animation?). {Progress()} Capture manually or Finish.",
                ScrollStopReason.FrameLimit => $"Frame limit reached. {Progress()} Click Finish.",
                ScrollStopReason.TimeLimit => $"Time limit reached. {Progress()} Click Finish.",
                ScrollStopReason.SizeLimit => $"Size limit reached. {Progress()} Click Finish.",
                ScrollStopReason.Canceled => $"Stopped. {Progress()}",
                _ => $"Stopped ({reason}). {Progress()}",
            });
            _session.Resume();
        }
        catch (OperationCanceledException) { SetStatus($"Stopped. {Progress()}"); }
        finally
        {
            _autoCts?.Dispose();
            _autoCts = null;
            Activate();
            UpdateButtons();
        }
    }

    private void StopAuto() => _autoCts?.Cancel();

    protected override void OnTopologyChanged()
    {
        _autoCts?.Cancel();
        SetStatus($"Displays changed. {Progress()} Finish to keep the captured frames.");
    }

    protected override void OnEscape()
    {
        if (_autoCts is not null) StopAuto(); else CancelSession();
    }

    private void CancelSession()
    {
        _autoCts?.Cancel();
        _finished = true;
        Vm.Status = "Scrolling capture canceled.";
        Close();
    }

    private async Task FinishAsync()
    {
        if (_session is not { Frames.Count: > 0 } s || _finished) return;
        _finished = true;
        _busy = true;
        UpdateButtons();
        SetStatus("Adding frames…");
        try
        {
            var assets = new List<ImageAsset>();
            foreach (var f in s.Frames) assets.Add(await Services.Importer.ImportPixelsAsync(f.Frame));
            var frames = s.Frames.ToList();
            var ids = new List<Guid>();
            Vm.Commit(frames.Count == 1 ? "Scrolling capture" : $"Scrolling capture ({frames.Count} frames)", d =>
            {
                var doc = DocumentOps.SetMode(d, LayoutMode.Free);
                var content = DocumentBounds.ContentBounds(doc);
                int x = content.IsEmpty ? 0 : (int)Math.Floor(content.X);
                int y = content.IsEmpty ? 0 : (int)Math.Ceiling(content.Bottom) + 16;
                var items = new List<(ImageAsset, ImageLayer)>();
                for (int i = 0; i < frames.Count; i++)
                {
                    var a = assets[i];
                    int overlap = i == 0 ? 0 : Math.Clamp(frames[i].Overlap, 0, a.Height - 1);
                    var layer = ImageLayer.ForAsset(a, x, y, $"Scroll {i + 1}") with
                    {
                        SourceCrop = new PixelRect(0, overlap, a.Width, a.Height - overlap),
                        Bounds = new PixelRect(x, y, a.Width, a.Height - overlap),
                    };
                    y += a.Height - overlap;
                    items.Add((a, layer));
                    ids.Add(layer.Id);
                }
                return DocumentOps.AddImages(doc, items);
            });
            if (ids.Count > 0 && Vm.Document.FindImage(ids[0]) is not null)
            {
                Vm.Select(ids);
                Vm.Status = $"Scrolling capture added: {frames.Count} frame(s), {s.OutputHeight} px tall. Seams stay editable (Layout > Join overlapping images).";
            }
            Close();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _finished = false;
            _busy = false;
            SetStatus("Could not store the frames: " + ex.Message);
            UpdateButtons();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _autoCts?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsOpen = false;
        base.OnClosed(e);
    }
}

/// <summary>
/// Explicitly started interval capture of one region with a visible Stop. Captures run sequentially
/// (never overlapping) and nothing is captured after Stop, a display change, or the frame limit.
/// </summary>
internal sealed class IntervalCaptureWindow : SessionPanel
{
    public static bool IsOpen { get; private set; }

    private readonly TimeSpan _interval;
    private readonly int _maxFrames;
    private readonly CaptureDestination? _destination;
    private readonly CancellationTokenSource _cts = new();
    private bool _discard;

    public IntervalCaptureWindow(AppServices services, CaptureCoordinator coordinator, EditorViewModel vm, Window owner, TimeSpan interval, int maxFrames, CaptureDestination? destination = null)
        : base(services, coordinator, vm, owner, "Interval capture")
    {
        _interval = interval;
        _maxFrames = Math.Clamp(maxFrames, 1, 200);
        _destination = destination;
        AddButton("Stop and keep", () => _cts.Cancel(), "Stops capturing and adds the frames captured so far");
        AddButton("Cancel", () => { _discard = true; _cts.Cancel(); });
    }

    public async void Start()
    {
        IsOpen = true;
        if (!await PickAsync($"Select the area to capture every {_interval.TotalSeconds:0} s. Esc cancels."))
        {
            IsOpen = false;
            Vm.Status = "Interval capture canceled.";
            return;
        }
        SetStatus($"Capturing every {_interval.TotalSeconds:0} s, up to {_maxFrames} frames. Starting now…");
        ShowBesideRegion();

        var session = new IntervalCaptureSession<PixelBuffer>(_interval, _maxFrames);
        session.Tick += (n, iv) => Dispatcher.BeginInvoke(() =>
            SetStatus($"Captured {n} of {_maxFrames}. {(n == 0 ? "Capturing now" : $"Next capture in {iv.TotalSeconds:0} s")}. Click Stop to finish."));
        bool cursor = Services.Settings.IncludeCursor;
        IReadOnlyList<PixelBuffer> frames;
        try
        {
            frames = await session.RunAsync(ct => Coordinator.CaptureRectAsync(Region, cursor, ct),
                (t, c) => Task.Delay(t, c), _cts.Token, () => TopologyChanged);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            frames = session.Frames;
            Vm.Status = "Interval capture stopped: " + ex.Message;
        }

        var kept = frames.ToList();
        Close();
        if (_discard || kept.Count == 0) { Vm.Status = "Interval capture canceled; nothing was added."; return; }
        var dest = _destination ?? (Services.Settings.DefaultDestination == CaptureDestination.CopyOnly ? CaptureDestination.AppendBelow : Services.Settings.DefaultDestination);
        await Vm.AddCapturesAsync(kept.Select(f => new CaptureItem(f, Region)).ToList(), dest);
        if (TopologyChanged) Vm.Status += " (stopped early because displays changed)";
    }

    protected override void OnEscape() => _cts.Cancel();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsOpen = false;
        base.OnClosed(e);
    }
}
