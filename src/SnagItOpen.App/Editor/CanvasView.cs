using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Layout;
using SnagItOpen.Imaging.Effects;
using SnagItOpen.Imaging.Rendering;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Editor;

/// <summary>Pointer tools available on the canvas.</summary>
public enum ToolKind
{
    Select, Crop, Rectangle, Ellipse, Arrow, Line, Text, Callout, Highlight, Step, Freehand,
    Redaction, Blur, Pixelate, Magnifier, Stamp, CutOut,
}

/// <summary>
/// Editor canvas. Renders the shared scene through <see cref="DocumentRenderer"/> under a
/// document→viewport transform, then draws adorners (checkerboard, selection, handles, snap guides)
/// that never reach export. Pointer gestures preview through <see cref="EditorViewModel.SetPreview"/>
/// and commit exactly one history entry on release; Escape cancels.
/// </summary>
public sealed class CanvasView : FrameworkElement
{
    private const double HandleSize = 8;
    private static readonly Pen SelectionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1.5));
    private static readonly Pen AnnSelectionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen ExportPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(160, 90, 90, 90)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 0, 170)), 1));
    private static readonly Pen DraftPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Brush HandleFill = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush CropShade = Frozen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)));
    private static readonly Brush Backdrop = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)));
    private static readonly Brush Checker = CreateChecker();

    private EditorViewModel? _vm;
    private ViewportTransform _view = ViewportTransform.Identity;
    private bool _fitPending = true;

    // gesture state
    private enum Drag { None, Pan, Move, Resize, AnnResize, Marquee, Draw, Freehand, Crop }
    private Drag _drag;
    private Point _downView;
    private PointD _downDoc;
    private PointD _curDoc;
    private ResizeHandle _handle;
    private PixelRect _resizeStart;
    private Guid _resizeTarget;
    private RectD _annStart;
    private Guid? _drawLayer;
    private readonly List<PointD> _freehand = [];
    private SnapLine[] _guides = [];
    private (double X, double Y) _panStart;
    private bool _spaceDown;

    public CanvasView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        AllowDrop = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        System.Windows.Automation.AutomationProperties.SetName(this, "Canvas. Arrow keys nudge the selection, Delete removes it.");
    }

    public EditorViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null) _vm.CanvasInvalidated -= OnInvalidated;
            _vm = value;
            if (_vm is not null) _vm.CanvasInvalidated += OnInvalidated;
            _fitPending = true;
            InvalidateVisual();
        }
    }

    public ToolKind Tool { get; set; } = ToolKind.Select;

    /// <summary>Current style for newly drawn annotations (provided by the tool panel).</summary>
    public Func<ToolKind, ToolStyle>? StyleProvider { get; set; }

    public bool SnapEnabled { get; set; } = true;

    /// <summary>Raised when a text/callout annotation needs its text edited.</summary>
    public event Action<TextAnnotation, bool>? EditTextRequested;

    /// <summary>Raised when a cut-out strip was chosen (document rectangle, vertical strip = true removes columns).</summary>
    public event Action<RectD>? CutOutRequested;

    public event Action? ViewChanged;

    public double Zoom => _view.Zoom;

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static Brush CreateChecker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var dark = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
        g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    private void OnInvalidated() => InvalidateVisual();

    // ================================================================== viewport

    public void FitToView()
    {
        if (_vm is null || ActualWidth <= 0) { _fitPending = true; return; }
        var d = _vm.Displayed;
        var area = d.HasVisibleContent ? d.ExportArea.ToRectD().Union(DocumentBounds.ContentBounds(d)) : d.ExportArea.ToRectD();
        _view = ViewportTransform.Fit(area, ActualWidth, ActualHeight);
        _fitPending = false;
        AfterViewChange();
    }

    public void ZoomTo(double zoom)
    {
        _view = _view.ZoomAbout(new PointD(ActualWidth / 2, ActualHeight / 2), zoom);
        AfterViewChange();
    }

    public void ZoomBy(double factor) => ZoomTo(_view.Zoom * factor);

    /// <summary>Converts a point in this element's coordinates to document pixels.</summary>
    public PointD ToDocumentPoint(Point viewPoint) => ToDoc(viewPoint);

    private void AfterViewChange()
    {
        UpdatePlacementHint();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void UpdatePlacementHint()
    {
        if (_vm is null) return;
        var c = _view.ToDocument(new PointD(ActualWidth / 2, ActualHeight / 2));
        _vm.FreePlacementHint = new PixelPoint((int)Math.Round(c.X), (int)Math.Round(c.Y));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fitPending) FitToView(); else UpdatePlacementHint();
    }

    private PointD ToDoc(Point p) => _view.ToDocument(new PointD(p.X, p.Y));
    private Point ToView(PointD p) { var v = _view.ToViewport(p); return new Point(v.X, v.Y); }
    private Rect ToView(RectD r) { var v = _view.ToViewport(r); return new Rect(v.X, v.Y, Math.Max(0, v.Width), Math.Max(0, v.Height)); }

    // ================================================================== rendering

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_vm is null) return;
        var doc = _vm.Displayed;
        if (_fitPending && doc.HasVisibleContent && ActualWidth > 0) { FitToView(); return; }

        var export = ToView(doc.ExportArea.ToRectD());
        if (doc.Background.A < 255) dc.DrawRectangle(Checker, null, export);

        dc.PushTransform(new MatrixTransform(_view.Zoom, 0, 0, _view.Zoom, _view.PanX, _view.PanY));
        try { _vm.Services.Renderer.Draw(dc, doc); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException) { }
        dc.Pop();

        dc.DrawRectangle(null, ExportPen, export);

        // Empty state
        if (!doc.HasVisibleContent)
        {
            DrawCenteredText(dc, "Drop images here, paste a screenshot (Ctrl+V), or use Capture.", 15);
            return;
        }

        // Selection adorners
        foreach (var id in _vm.SelectedImages)
        {
            if (doc.FindImage(id) is not { } l) continue;
            var r = ToView(l.Bounds.ToRectD());
            dc.DrawRectangle(null, SelectionPen, r);
            if (_vm.SelectedImages.Count == 1 && _vm.SelectedAnnotations.Count == 0 && Tool == ToolKind.Select) DrawHandles(dc, r);
        }
        foreach (var id in _vm.SelectedAnnotations)
        {
            if (doc.FindAnnotation(id) is not { } a) continue;
            var r = ToView(AnnotationDocBounds(doc, a));
            dc.DrawRectangle(null, AnnSelectionPen, r);
            if (_vm.SelectedAnnotations.Count == 1 && _vm.SelectedImages.Count == 0 && Tool == ToolKind.Select) DrawHandles(dc, r);
        }

        foreach (var g in _guides)
        {
            if (g.Vertical) { var x = ToView(new PointD(g.Position, 0)).X; dc.DrawLine(GuidePen, new Point(x, 0), new Point(x, ActualHeight)); }
            else { var y = ToView(new PointD(0, g.Position)).Y; dc.DrawLine(GuidePen, new Point(0, y), new Point(ActualWidth, y)); }
        }

        DrawDraft(dc, doc);
    }

    private void DrawCenteredText(DrawingContext dc, string text, double size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, SystemColors.GrayTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
    }

    private static void DrawHandles(DrawingContext dc, Rect r)
    {
        foreach (var h in Enum.GetValues<ResizeHandle>())
        {
            var p = HandlePoint(r, h);
            dc.DrawRectangle(HandleFill, SelectionPen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
        }
    }

    private static Point HandlePoint(Rect r, ResizeHandle h) => h switch
    {
        ResizeHandle.TopLeft => r.TopLeft,
        ResizeHandle.Top => new Point(r.X + r.Width / 2, r.Y),
        ResizeHandle.TopRight => r.TopRight,
        ResizeHandle.Right => new Point(r.Right, r.Y + r.Height / 2),
        ResizeHandle.BottomRight => r.BottomRight,
        ResizeHandle.Bottom => new Point(r.X + r.Width / 2, r.Bottom),
        ResizeHandle.BottomLeft => r.BottomLeft,
        _ => new Point(r.X, r.Y + r.Height / 2),
    };

    private void DrawDraft(DrawingContext dc, DocumentState doc)
    {
        switch (_drag)
        {
            case Drag.Marquee:
            case Drag.Draw:
                {
                    var r = ToView(RectD.FromPoints(_downDoc, _curDoc));
                    if (Tool is ToolKind.Arrow or ToolKind.Line && _drag == Drag.Draw)
                        dc.DrawLine(DraftPen, ToView(_downDoc), ToView(_curDoc));
                    else if (Tool == ToolKind.Ellipse && _drag == Drag.Draw)
                        dc.DrawEllipse(null, DraftPen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                    else dc.DrawRectangle(null, DraftPen, r);
                    if (_drag == Drag.Draw) DrawSizeLabel(dc, r, RectD.FromPoints(_downDoc, _curDoc));
                    break;
                }
            case Drag.Crop:
                {
                    var r = ToView(RectD.FromPoints(_downDoc, _curDoc));
                    var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
                    if (_vm?.SelectedLayer is { } l) shade.Children.Add(new RectangleGeometry(ToView(l.Bounds.ToRectD())));
                    shade.Children.Add(new RectangleGeometry(r));
                    dc.DrawGeometry(CropShade, null, shade);
                    dc.DrawRectangle(null, DraftPen, r);
                    DrawSizeLabel(dc, r, RectD.FromPoints(_downDoc, _curDoc));
                    break;
                }
            case Drag.Freehand when _freehand.Count > 1:
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(ToView(_freehand[0]), false, false);
                        for (int i = 1; i < _freehand.Count; i++) c.LineTo(ToView(_freehand[i]), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, DraftPen, g);
                    break;
                }
        }
    }

    private void DrawSizeLabel(DrawingContext dc, Rect view, RectD docRect)
    {
        var ft = new FormattedText($"{Math.Round(docRect.Width)} × {Math.Round(docRect.Height)}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var at = new Point(view.X, Math.Max(0, view.Y - ft.Height - 6));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)), null, new Rect(at.X, at.Y, ft.Width + 8, ft.Height + 4), 3, 3);
        dc.DrawText(ft, new Point(at.X + 4, at.Y + 2));
    }

    // ================================================================== hit testing

    private static RectD AnnotationDocBounds(DocumentState doc, Annotation a)
    {
        var b = a.Bounds.IsEmpty ? a.Extent() : a.Bounds;
        if (a is LineAnnotation || a is FreehandAnnotation) b = a.Extent();
        return b;
    }

    private Annotation? HitAnnotation(DocumentState doc, PointD p)
    {
        double tol = 4 / _view.Zoom;
        for (int i = doc.Annotations.Length - 1; i >= 0; i--)
        {
            var a = doc.Annotations[i];
            if (AnnotationDocBounds(doc, a).Inflate(tol).Contains(p)) return a;
        }
        return null;
    }

    private ResizeHandle? HitHandle(Rect viewRect, Point p)
    {
        foreach (var h in Enum.GetValues<ResizeHandle>())
        {
            var hp = HandlePoint(viewRect, h);
            if (Math.Abs(hp.X - p.X) <= HandleSize && Math.Abs(hp.Y - p.Y) <= HandleSize) return h;
        }
        return null;
    }

    // ================================================================== pointer input

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            _view = _view.ZoomAbout(new PointD(p.X, p.Y), _view.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            _view = _view.PanBy(e.Delta / 2.0, 0);
        else
            _view = _view.PanBy(0, e.Delta / 2.0);
        AfterViewChange();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (_vm is null) return;
        var p = e.GetPosition(this);
        _downView = p;
        _downDoc = _curDoc = ToDoc(p);

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown))
        {
            _drag = Drag.Pan;
            _panStart = (_view.PanX, _view.PanY);
            Cursor = Cursors.SizeAll;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        var doc = _vm.Document;
        if (e.ClickCount == 2 && Tool == ToolKind.Select)
        {
            if (HitAnnotation(doc, _downDoc) is TextAnnotation t) { EditTextRequested?.Invoke(t, false); e.Handled = true; return; }
        }

        switch (Tool)
        {
            case ToolKind.Select: BeginSelect(doc, p); break;
            case ToolKind.Crop:
                if (_vm.SelectedLayer is null)
                {
                    var hit = DocumentOps.HitTestImage(doc, _downDoc);
                    if (hit is null) { _vm.Status = "Click an image to crop it."; return; }
                    _vm.Select([hit.Id]);
                }
                _drag = Drag.Crop;
                break;
            case ToolKind.Text or ToolKind.Callout or ToolKind.Step or ToolKind.Stamp:
                PlaceClickAnnotation(doc);
                return;
            case ToolKind.Freehand:
                _freehand.Clear();
                _freehand.Add(_downDoc);
                _drag = Drag.Freehand;
                break;
            default:
                // Only image effects (blur/pixelate) target an image; annotations are canvas objects.
                _drawLayer = Tool is ToolKind.Blur or ToolKind.Pixelate ? DocumentOps.HitTestImage(doc, _downDoc)?.Id : null;
                _drag = Drag.Draw;
                break;
        }
        CaptureMouse();
        e.Handled = true;
    }

    private void BeginSelect(DocumentState doc, Point p)
    {
        var vm = _vm!;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // Handles of the single selected item take priority.
        if (vm.SelectedImages.Count == 1 && vm.SelectedAnnotations.Count == 0 && vm.SelectedLayer is { } sel &&
            HitHandle(ToView(sel.Bounds.ToRectD()), p) is { } h)
        {
            _drag = Drag.Resize; _handle = h; _resizeStart = sel.Bounds; _resizeTarget = sel.Id;
            return;
        }
        if (vm.SelectedAnnotations.Count == 1 && vm.SelectedImages.Count == 0 && vm.PrimaryAnnotation is { } sa &&
            HitHandle(ToView(AnnotationDocBounds(doc, sa)), p) is { } ah)
        {
            _drag = Drag.AnnResize; _handle = ah; _annStart = AnnotationDocBounds(doc, sa); _resizeTarget = sa.Id;
            return;
        }

        var ann = HitAnnotation(doc, _downDoc);
        var img = ann is null ? DocumentOps.HitTestImage(doc, _downDoc) : null;
        if (ann is not null)
        {
            if (ctrl) vm.Select(vm.SelectedImages, Toggle(vm.SelectedAnnotations, ann.Id));
            else if (!vm.SelectedAnnotations.Contains(ann.Id)) vm.Select([], [ann.Id]);
            _drag = Drag.Move;
        }
        else if (img is not null)
        {
            if (ctrl) vm.ToggleImage(img.Id);
            else if (!vm.SelectedImages.Contains(img.Id)) vm.Select([img.Id]);
            _drag = Drag.Move;
        }
        else
        {
            if (!ctrl) vm.ClearSelection();
            _drag = Drag.Marquee;
        }
    }

    private static HashSet<Guid> Toggle(IReadOnlySet<Guid> set, Guid id)
    {
        var s = set.ToHashSet();
        if (!s.Add(id)) s.Remove(id);
        return s;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_vm is null || _drag == Drag.None) { UpdateHoverCursor(e.GetPosition(this)); return; }
        var p = e.GetPosition(this);
        _curDoc = ToDoc(p);
        var baseDoc = _vm.Document;
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (_drag)
        {
            case Drag.Pan:
                _view = _view with { PanX = _panStart.X + (p.X - _downView.X), PanY = _panStart.Y + (p.Y - _downView.Y) };
                AfterViewChange();
                return;
            case Drag.Move:
                {
                    double dx = _curDoc.X - _downDoc.X, dy = _curDoc.Y - _downDoc.Y;
                    if (Math.Abs(p.X - _downView.X) < 2 && Math.Abs(p.Y - _downView.Y) < 2) return;
                    (dx, dy) = Snap(baseDoc, dx, dy, alt);
                    int ix = (int)Math.Round(dx), iy = (int)Math.Round(dy);
                    TryPreview(d => DocumentOps.Move(d, _vm.SelectedImages, _vm.SelectedAnnotations, ix, iy));
                    return;
                }
            case Drag.Resize:
                {
                    double dx = _curDoc.X - _downDoc.X, dy = _curDoc.Y - _downDoc.Y;
                    var nb = ResizeGeometry.Resize(_resizeStart, _handle, dx, dy, keepAspect: !shift);
                    TryPreview(d => DocumentOps.SetBounds(d, _resizeTarget, nb));
                    return;
                }
            case Drag.AnnResize:
                {
                    var nr = ResizeRectD(_annStart, _handle, _curDoc.X - _downDoc.X, _curDoc.Y - _downDoc.Y, shift);
                    TryPreview(d => ResizeAnnotation(d, _resizeTarget, _annStart, nr));
                    return;
                }
            case Drag.Freehand:
                if (_freehand.Count < FreehandAnnotation.MaxPoints && PointD.Distance(_freehand[^1], _curDoc) * _view.Zoom >= 1.5) _freehand.Add(_curDoc);
                break;
            case Drag.Draw when shift && Tool is ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Highlight or ToolKind.Redaction:
                {
                    // Shift constrains to a square.
                    double s = Math.Max(Math.Abs(_curDoc.X - _downDoc.X), Math.Abs(_curDoc.Y - _downDoc.Y));
                    _curDoc = new PointD(_downDoc.X + Math.Sign(_curDoc.X - _downDoc.X) * s, _downDoc.Y + Math.Sign(_curDoc.Y - _downDoc.Y) * s);
                    break;
                }
        }
        InvalidateVisual();
    }

    private void UpdateHoverCursor(Point p)
    {
        if (_spaceDown) { Cursor = Cursors.Hand; return; }
        if (_vm is null) return;
        Cursor = Tool switch
        {
            ToolKind.Select => HoverHandleCursor(p) ?? Cursors.Arrow,
            ToolKind.Text or ToolKind.Callout => Cursors.IBeam,
            _ => Cursors.Cross,
        };
    }

    private Cursor? HoverHandleCursor(Point p)
    {
        Rect? r = null;
        if (_vm!.SelectedImages.Count == 1 && _vm.SelectedAnnotations.Count == 0 && _vm.SelectedLayer is { } l) r = ToView(l.Bounds.ToRectD());
        else if (_vm.SelectedAnnotations.Count == 1 && _vm.SelectedImages.Count == 0 && _vm.PrimaryAnnotation is { } a) r = ToView(AnnotationDocBounds(_vm.Document, a));
        if (r is null || HitHandle(r.Value, p) is not { } h) return null;
        return h switch
        {
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            _ => Cursors.SizeWE,
        };
    }

    private (double, double) Snap(DocumentState doc, double dx, double dy, bool alt)
    {
        _guides = [];
        if (!SnapEnabled || alt || _vm!.SelectedImages.Count == 0) return (dx, dy);
        RectD moving = default; bool any = false;
        foreach (var id in _vm.SelectedImages)
            if (doc.FindImage(id) is { } l) { moving = any ? moving.Union(l.Bounds.ToRectD()) : l.Bounds.ToRectD(); any = true; }
        if (!any) return (dx, dy);
        var targets = doc.Images.Where(i => i.Visible && !_vm.SelectedImages.Contains(i.Id))
            .Select(i => new SnapEngine.Candidate(i.Id.ToString(), i.Bounds.ToRectD())).ToList();
        if (!doc.AutoCanvas) targets.Add(new SnapEngine.Candidate("canvas", doc.ExportArea.ToRectD()));
        var r = SnapEngine.SnapMove(moving, dx, dy, targets, _view.Zoom);
        _guides = r.Guides;
        return (r.Dx, r.Dy);
    }

    private void TryPreview(Func<DocumentState, DocumentState> f)
    {
        try { _vm!.SetPreview(f(_vm.Document)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or LayoutLimitException or OverflowException) { }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_vm is null || _drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        _guides = [];
        _curDoc = ToDoc(e.GetPosition(this));
        if (drag == Drag.Pan) { Cursor = _spaceDown ? Cursors.Hand : null; return; }

        var preview = _vm.PreviewState;
        _vm.SetPreview(null);
        switch (drag)
        {
            case Drag.Move:
                if (preview is not null) _vm.Commit("Move", _ => preview);
                break;
            case Drag.Resize:
                if (preview is not null) _vm.Commit("Resize", _ => preview);
                break;
            case Drag.AnnResize:
                if (preview is not null) _vm.Commit("Resize annotation", _ => preview);
                break;
            case Drag.Marquee:
                FinishMarquee();
                break;
            case Drag.Crop:
                FinishCrop();
                break;
            case Drag.Draw:
                FinishDraw();
                break;
            case Drag.Freehand:
                FinishFreehand();
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is Drag.None) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed) CancelGesture();
    }

    /// <summary>Escape: abandon the gesture without history.</summary>
    public bool CancelGesture()
    {
        if (_drag == Drag.None) return false;
        _drag = Drag.None;
        _guides = [];
        _freehand.Clear();
        if (IsMouseCaptured) ReleaseMouseCapture();
        _vm?.SetPreview(null);
        InvalidateVisual();
        return true;
    }

    public void SetSpace(bool down)
    {
        _spaceDown = down;
        if (_drag == Drag.None) Cursor = down ? Cursors.Hand : null;
    }

    // ================================================================== finishing gestures

    private void FinishMarquee()
    {
        var r = RectD.FromPoints(_downDoc, _curDoc);
        if (r.Width * _view.Zoom < 3 && r.Height * _view.Zoom < 3) return;
        var doc = _vm!.Document;
        var imgs = doc.Images.Where(i => i.Visible && Intersects(i.Bounds.ToRectD(), r)).Select(i => i.Id);
        var anns = doc.Annotations.Where(a => Intersects(AnnotationDocBounds(doc, a), r)).Select(a => a.Id);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _vm.Select(ctrl ? _vm.SelectedImages.Concat(imgs) : imgs, ctrl ? _vm.SelectedAnnotations.Concat(anns) : anns);
    }

    private static bool Intersects(RectD a, RectD b) => !a.Intersect(b).IsEmpty;

    private void FinishCrop()
    {
        var vm = _vm!;
        if (vm.SelectedLayer is not { } l) return;
        var r = RectD.FromPoints(_downDoc, _curDoc).Intersect(l.Bounds.ToRectD());
        if (r.IsEmpty || r.Width * _view.Zoom < 3 || r.Height * _view.Zoom < 3) { vm.Status = "Drag across the image to choose the area to keep."; return; }
        var src = ImageTransform.DocumentRectToSource(l, r).ToPixelRectRounded().Intersect(l.SourceCrop);
        if (src.IsEmpty) return;
        vm.Commit("Crop", d => DocumentOps.Crop(d, l.Id, src));
    }

    private ToolStyle ToolStyleFor(ToolKind k) => StyleProvider?.Invoke(k) ?? new ToolStyle();

    private void FinishDraw()
    {
        var vm = _vm!;
        var doc = vm.Document;
        var drRect = RectD.FromPoints(_downDoc, _curDoc);
        bool tiny = drRect.Width * _view.Zoom < 3 && drRect.Height * _view.Zoom < 3;
        var st = ToolStyleFor(Tool);

        if (Tool is ToolKind.Blur or ToolKind.Pixelate)
        {
            var layer = _drawLayer is { } lid ? doc.FindImage(lid) : DocumentOps.HitTestImage(doc, drRect.Center);
            if (layer is null || tiny) { vm.Status = "Drag over an image to blur or pixelate part of it."; return; }
            var src = ImageTransform.DocumentRectToSource(layer, drRect.Intersect(layer.Bounds.ToRectD())).ToPixelRectOutward();
            var kind = Tool == ToolKind.Blur ? ImageEffectKind.Blur : ImageEffectKind.Pixelate;
            int strength = kind == ImageEffectKind.Blur ? Math.Clamp(st.EffectStrength, ImageEffect.MinBlur, ImageEffect.MaxBlur) : Math.Clamp(st.EffectStrength, ImageEffect.MinBlock, ImageEffect.MaxBlock);
            vm.AddEffect(layer.Id, new ImageEffect(Guid.NewGuid(), kind, src, strength));
            return;
        }
        if (Tool == ToolKind.CutOut)
        {
            if (tiny) { vm.Status = "Drag across the strip to remove."; return; }
            CutOutRequested?.Invoke(drRect);
            return;
        }
        if (Tool == ToolKind.Magnifier)
        {
            if (tiny) { vm.Status = "Drag over the detail you want to magnify."; return; }
            double z = Math.Clamp(st.Zoom, 1.25, 8);
            var dest = new RectD(drRect.Right + 16, drRect.Y, drRect.Width * z, drRect.Height * z);
            vm.AddAnnotation(new MagnifierAnnotation { SourceRegion = drRect, Bounds = dest, Color = st.Color, StrokeWidth = Math.Max(1, st.StrokeWidth), Circular = st.Symbol != "square" });
            return;
        }
        if (tiny) return;

        // Annotations are canvas objects in document pixels; redactions cover exactly what was dragged.
        double stroke = st.StrokeWidth;
        var a0 = _downDoc; var a1 = _curDoc;
        var b = RectD.FromPoints(a0, a1);
        Annotation? ann = Tool switch
        {
            ToolKind.Rectangle => new RectangleAnnotation { Bounds = b, Color = st.Color, Fill = st.Fill, StrokeWidth = stroke },
            ToolKind.Ellipse => new EllipseAnnotation { Bounds = b, Color = st.Color, Fill = st.Fill, StrokeWidth = stroke },
            ToolKind.Arrow => new ArrowAnnotation { Start = a0, End = a1, Bounds = b, Color = st.Color, StrokeWidth = stroke, Dashed = st.Dashed },
            ToolKind.Line => new LineAnnotation { Start = a0, End = a1, Bounds = b, Color = st.Color, StrokeWidth = stroke, Dashed = st.Dashed },
            ToolKind.Highlight => new HighlightAnnotation { Bounds = b, Color = st.Color },
            ToolKind.Redaction => new RedactionAnnotation { Bounds = b, Color = st.Color with { A = 255 } },
            _ => null,
        };
        if (ann is null) return;
        vm.AddAnnotation(ann);
    }

    private void FinishFreehand()
    {
        var vm = _vm!;
        if (_freehand.Count < 2) { _freehand.Clear(); return; }
        var pts = _freehand.ToList();
        _freehand.Clear();
        var simplified = CaptureMask.Simplify(pts, 0.75, FreehandAnnotation.MaxPoints);
        var st = ToolStyleFor(ToolKind.Freehand);
        vm.AddAnnotation(new FreehandAnnotation
        {
            Points = simplified, Bounds = FreehandAnnotation.BoundsOf(simplified), Color = st.Color,
            StrokeWidth = st.StrokeWidth,
        });
    }

    private void PlaceClickAnnotation(DocumentState doc)
    {
        var vm = _vm!;
        var st = ToolStyleFor(Tool);
        const double scale = 1;
        var p = _downDoc;
        switch (Tool)
        {
            case ToolKind.Step:
                {
                    double size = Math.Max(16, st.FontSize * 1.4) * scale;
                    vm.AddAnnotation(new StepAnnotation
                    {
                        Bounds = new RectD(p.X - size / 2, p.Y - size / 2, size, size), Number = DocumentOps.NextStepNumber(doc),
                        Color = st.Color, StrokeWidth = 0,
                    });
                    break;
                }
            case ToolKind.Stamp:
                {
                    double size = 64 * scale;
                    vm.AddAnnotation(new StampAnnotation
                    {
                        Bounds = new RectD(p.X - size / 2, p.Y - size / 2, size, size), Symbol = st.Symbol ?? StampSymbols.Check,
                        Color = st.Color, StrokeWidth = 0,
                    });
                    break;
                }
            case ToolKind.Text:
            case ToolKind.Callout:
                {
                    double fs = st.FontSize * scale;
                    var w = Math.Max(120, fs * 10);
                    var box = new RectD(p.X, p.Y, w, fs * 1.6 + 2 * AnnotationRenderer.TextPadding);
                    TextAnnotation t = Tool == ToolKind.Callout
                        ? new CalloutAnnotation
                        {
                            Bounds = box with { Y = p.Y - box.Height - 40 * scale, X = p.X + 30 * scale }, Tail = p, Text = "",
                            FontFamily = st.FontFamily, FontSize = fs, Bold = st.Bold, Color = st.Color,
                            Fill = st.Fill ?? new Rgba32(255, 255, 255, 255), StrokeWidth = Math.Max(1, st.StrokeWidth * scale),
                        }
                        : new TextAnnotation
                        {
                            Bounds = box, Text = "", FontFamily = st.FontFamily, FontSize = fs, Bold = st.Bold,
                            Color = st.Color, Fill = st.Fill, StrokeWidth = 0,
                        };
                    EditTextRequested?.Invoke(t, true);
                    break;
                }
        }
    }

    // ================================================================== annotation resize helpers

    private static RectD ResizeRectD(RectD start, ResizeHandle h, double dx, double dy, bool keepAspect)
    {
        double l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        if (h is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft) l = Math.Min(l + dx, r - 1);
        if (h is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight) r = Math.Max(r + dx, l + 1);
        if (h is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight) t = Math.Min(t + dy, b - 1);
        if (h is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight) b = Math.Max(b + dy, t + 1);
        if (keepAspect && start.Width > 0 && start.Height > 0 && h is ResizeHandle.TopLeft or ResizeHandle.TopRight or ResizeHandle.BottomLeft or ResizeHandle.BottomRight)
        {
            double aspect = start.Width / start.Height, w = r - l, ht = b - t;
            if (w / start.Width >= ht / start.Height) ht = w / aspect; else w = ht * aspect;
            if (h is ResizeHandle.TopLeft or ResizeHandle.BottomLeft) l = r - w; else r = l + w;
            if (h is ResizeHandle.TopLeft or ResizeHandle.TopRight) t = b - ht; else b = t + ht;
        }
        return RectD.FromEdges(l, t, r, b);
    }

    /// <summary>Affinely maps an annotation's geometry so its document bounds go from <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal static DocumentState ResizeAnnotation(DocumentState doc, Guid id, RectD from, RectD to)
    {
        doc = AnnotationCanvas.Normalize(doc);
        if (doc.FindAnnotation(id) is not { } a) return doc;
        double sx = from.Width > 1e-6 ? to.Width / from.Width : 1, sy = from.Height > 1e-6 ? to.Height / from.Height : 1;
        var moved = a.MapGeometry(p => new PointD(to.X + (p.X - from.X) * sx, to.Y + (p.Y - from.Y) * sy));
        if (a is MagnifierAnnotation m && moved is MagnifierAnnotation mm) moved = mm with { SourceRegion = m.SourceRegion };
        return DocumentOps.UpdateAnnotation(doc, moved);
    }
}
