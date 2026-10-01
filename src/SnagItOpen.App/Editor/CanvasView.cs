using System.Globalization;
using System.IO;
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
using SnagItOpen.App.Infrastructure;

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
    private Pen SelectionPen = null!, AnnSelectionPen = null!, ExportPen = null!, GuidePen = null!, DraftPen = null!;
    private Pen LockedCanvasPen = null!, HoverPen = null!, FocusPen = null!, LockPen = null!;
    private Brush HandleFill = null!, SpecialFill = null!, CropShade = null!, Backdrop = null!, Checker = null!;
    private Brush OutsideShade = null!, LabelBack = null!, LabelText = null!, GuideText = null!, SheetShadow = null!;
    private Geometry _lockGeometry = Geometry.Empty;
    private readonly DrawingVisual _documentLayer = new();
    private readonly DrawingVisual _adornerLayer = new();
    private ThemeService? _theme;
    private bool _documentDirty = true;
    private DocumentState? _renderedDocument;
    private ViewportTransform _renderedView;
    private Size _renderedSize;
    private Guid? _renderedEditing;
    private OutsideCanvasMode _renderedOutside;
    private bool _keyboardFocus, _pointerFocusing;
    private static readonly Lazy<Cursor> RotateCursor = new(LoadRotateCursor);

    private EditorViewModel? _vm;
    private ViewportTransform _view = ViewportTransform.Identity;
    private bool _fitPending = true;

    // gesture state
    private enum Drag { None, Pan, Move, Resize, AnnHandle, Marquee, Draw, Freehand, Crop, CanvasEdge, CanvasMove }
    private PixelRect _canvasStart;

    /// <summary>How content outside a locked canvas is shown in the editor (never exported).</summary>
    public OutsideCanvasMode Outside
    {
        get => _outside;
        set { if (_outside == value) return; _outside = value; InvalidateVisual(); }
    }
    private OutsideCanvasMode _outside = OutsideCanvasMode.Dim;
    private Drag _drag;
    private Point _downView;
    private PointD _downDoc;
    private PointD _curDoc;
    private ResizeHandle _handle;
    private PixelRect _resizeStart;
    private Guid _resizeTarget;
    private AnnotationHandle _annHandle;
    private Annotation? _annStart;
    private Guid? _hover;
    private double? _angleLabel;
    private Guid? _drawLayer;
    private readonly List<PointD> _freehand = [];
    private SnapLine[] _guides = [];
    private SpacingDistance[] _distances = [];
    private EqualSpacing[] _spacing = [];
    private (double X, double Y) _panStart;
    private bool _spaceDown;

    public CanvasView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        AllowDrop = true;
        AddVisualChild(_documentLayer);
        AddVisualChild(_adornerLayer);
        RenderOptions.SetBitmapScalingMode(_documentLayer, BitmapScalingMode.HighQuality);
        RebuildThemeResources();
        Loaded += (_, _) =>
        {
            _theme = ThemeService.Current;
            if (_theme is not null) _theme.ThemeChanged += OnThemeChanged;
            RebuildThemeResources();
            _documentDirty = true;
            InvalidateVisual();
        };
        Unloaded += (_, _) => { if (_theme is not null) _theme.ThemeChanged -= OnThemeChanged; _theme = null; };
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        System.Windows.Automation.AutomationProperties.SetName(this, "Canvas. Arrow keys nudge the selection, Delete removes it.");
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _documentLayer, 1 => _adornerLayer, _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>Number of shared scene render calls. Hover and selection must leave this unchanged.</summary>
    public int DocumentRenderCount { get; private set; }

    /// <summary>Requests a hover/selection/draft redraw without discarding the document visual.</summary>
    public void RefreshAdorners() => InvalidateVisual();

    /// <summary>The shell shows an actionable InfoBar when all editable content is hidden.</summary>
    public bool IsAllContentHidden => _vm is { IsEmpty: false } && !_vm.Displayed.Images.Any(i => i.Visible)
        && !_vm.Displayed.Annotations.Any(a => !a.Hidden || a is RedactionAnnotation);

    public EditorViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null) _vm.CanvasInvalidated -= OnInvalidated;
            _vm = value;
            if (_vm is not null) _vm.CanvasInvalidated += OnInvalidated;
            _fitPending = true;
            _documentDirty = true;
            InvalidateVisual();
        }
    }

    public ToolKind Tool { get => _tool; set { if (_tool == value) return; _tool = value; RefreshAdorners(); } }
    private ToolKind _tool = ToolKind.Select;

    /// <summary>Current style for newly drawn annotations (provided by the tool panel).</summary>
    public Func<ToolKind, ToolStyle>? StyleProvider { get; set; }

    /// <summary>Per-tool annotation prototype (look of newly drawn items), edited in the properties panel.</summary>
    public Func<ToolKind, Annotation>? PrototypeProvider { get; set; }

    private T Proto<T>(ToolKind k) where T : Annotation, new() =>
        PrototypeProvider?.Invoke(k) is T p ? AnnotationStyle.Instantiate(p) : new T();

    public bool SnapEnabled { get; set; } = true;

    /// <summary>Raised when a text/callout annotation needs its text edited.</summary>
    public event Action<TextAnnotation, bool>? EditTextRequested;

    /// <summary>Raised when a cut-out strip was chosen (document rectangle, vertical strip = true removes columns).</summary>
    public event Action<RectD>? CutOutRequested;

    public event Action? ViewChanged;

    public double Zoom => _view.Zoom;

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private Brush Token(string key, Brush fallback, double opacity = 1)
    {
        var brush = (TryFindResource(key) as Brush ?? fallback).CloneCurrentValue();
        brush.Opacity *= opacity;
        return Frozen(brush);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RebuildThemeResources();
        _documentDirty = true;
        InvalidateVisual();
    }

    private void RebuildThemeResources()
    {
        Brush accent = Token("Accent.Select", SystemColors.HighlightBrush);
        SelectionPen = Frozen(new Pen(accent, 1.5));
        AnnSelectionPen = Frozen(new Pen(accent, 1) { DashStyle = DashStyles.Dash });
        ExportPen = Frozen(new Pen(accent, 1) { DashStyle = DashStyles.Dash });
        LockedCanvasPen = Frozen(new Pen(accent, 1.5) { DashStyle = DashStyles.Dot });
        DraftPen = Frozen(new Pen(accent, 1.5) { DashStyle = DashStyles.Dash });
        HoverPen = Frozen(new Pen(Token("Accent.Select", SystemColors.HighlightBrush, 0.65), 1));
        GuidePen = Frozen(new Pen(Token("Accent.Guide", SystemColors.HighlightBrush), 1));
        FocusPen = Frozen(new Pen(Token("Accent.Select", SystemColors.HighlightBrush), 1.5)); // PRD 5.2: Accent.Select is the on-canvas focus colour
        HandleFill = Token("Handle.Fill", SystemColors.HighlightTextBrush);
        SpecialFill = Token("Handle.Special", SystemColors.HighlightBrush);
        Backdrop = Token("Bg.Canvas", SystemColors.ControlBrush);
        CropShade = Token("Overlay.Dim", SystemColors.WindowTextBrush, 0.65);
        OutsideShade = Token("Bg.Canvas", SystemColors.ControlBrush, 0.65);
        LabelBack = Token("Bg.Surface", SystemColors.WindowBrush, 0.9);
        LabelText = Token("Text.Primary", SystemColors.WindowTextBrush);
        GuideText = Token("Text.OnAccent", SystemColors.HighlightTextBrush);
        LockPen = Frozen(new Pen(LabelText, 1.5));
        SheetShadow = Token("Overlay.Dim", SystemColors.WindowTextBrush, 0.2);
        Checker = CreateChecker();
        _lockGeometry = TryFindResource("Icon.Lock") is Geometry g ? Frozen(g.CloneCurrentValue()) : Geometry.Empty;
    }

    private Brush CreateChecker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Token("Checker.A", SystemColors.WindowBrush), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var dark = Token("Checker.B", SystemColors.ControlBrush);
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

    /// <summary>Fits the current selected image/annotation bounds and centres them in the viewport.</summary>
    public void ZoomToSelection()
    {
        if (_vm is null || !TrySelectionBounds(_vm.Displayed, out var area)) return;
        _view = ViewportTransform.Fit(area, ActualWidth, ActualHeight, maxZoom: ViewportTransform.MaxZoom);
        _fitPending = false;
        AfterViewChange();
    }

    /// <summary>Fits the export sheet and visible content horizontally, retaining a 24 DIP margin.</summary>
    public void FitWidth()
    {
        if (_vm is null || ActualWidth <= 48 || ActualHeight <= 0) return;
        var doc = _vm.Displayed;
        var area = doc.ExportArea.ToRectD().Union(DocumentBounds.ContentBounds(doc));
        double zoom = Math.Clamp((ActualWidth - 48) / Math.Max(1, area.Width), ViewportTransform.MinZoom, ViewportTransform.MaxZoom);
        _view = new(zoom, (ActualWidth - area.Width * zoom) / 2 - area.X * zoom,
            (ActualHeight - area.Height * zoom) / 2 - area.Y * zoom);
        _fitPending = false;
        AfterViewChange();
    }

    /// <summary>Converts a point in this element's coordinates to document pixels.</summary>
    public PointD ToDocumentPoint(Point viewPoint) => ToDoc(viewPoint);

    /// <summary>Converts a document point to this element's coordinates.</summary>
    public Point ToViewPoint(PointD docPoint) => ToView(docPoint);

    /// <summary>Annotation hidden from the canvas while it is edited in place.</summary>
    public Guid? EditingAnnotation
    {
        get => _editing;
        set { _editing = value; InvalidateVisual(); }
    }
    private Guid? _editing;

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
        // A transparent hit surface keeps the entire viewport interactive; the two child visuals retain their drawings.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_fitPending && _vm?.Displayed.HasVisibleContent == true && ActualWidth > 0) FitToView();
        var doc = _vm?.Displayed;
        if (_documentDirty || !ReferenceEquals(doc, _renderedDocument) || _view != _renderedView
            || RenderSize != _renderedSize || EditingAnnotation != _renderedEditing || Outside != _renderedOutside)
        {
            using var documentContext = _documentLayer.RenderOpen();
            DrawDocument(documentContext, doc);
            _renderedDocument = doc;
            _renderedView = _view;
            _renderedSize = RenderSize;
            _renderedEditing = EditingAnnotation;
            _renderedOutside = Outside;
            _documentDirty = false;
        }
        using var adorners = _adornerLayer.RenderOpen();
        if (doc is not null) DrawAdorners(adorners, doc);
        if (_keyboardFocus && IsKeyboardFocused && ActualWidth > 2 && ActualHeight > 2)
            adorners.DrawRectangle(null, FocusPen, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));
    }

    private void DrawDocument(DrawingContext dc, DocumentState? doc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        // An empty document has a placeholder 1x1 export area fitted to the viewport: drawing its sheet frames the whole canvas.
        if (doc is null || _vm is null || _vm.IsEmpty) return;
        var export = ToView(doc.ExportArea.ToRectD());
        dc.DrawRoundedRectangle(SheetShadow, null, new Rect(export.X - 1, export.Y + 2, export.Width + 2, export.Height + 2), 2, 2);
        if (doc.Background.A < 255) dc.DrawRectangle(Checker, null, export);

        bool locked = !doc.AutoCanvas;
        bool hideOutside = locked && Outside == OutsideCanvasMode.Hide;
        if (hideOutside) dc.PushClip(new RectangleGeometry(export));
        dc.PushTransform(new MatrixTransform(_view.Zoom, 0, 0, _view.Zoom, _view.PanX, _view.PanY));
        try
        {
            DocumentRenderCount++;
            _vm.Services.Renderer.Draw(dc, doc, EditingAnnotation is { } eid ? new RenderSettings { HiddenAnnotations = new HashSet<Guid> { eid } } : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException) { }
        dc.Pop();
        if (hideOutside) dc.Pop();

        if (locked && Outside == OutsideCanvasMode.Dim)
        {
            // Content outside a locked canvas is not exported: shade it.
            var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
            shade.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            shade.Children.Add(new RectangleGeometry(export));
            dc.DrawGeometry(OutsideShade, null, shade);
        }
    }

    private void DrawAdorners(DrawingContext dc, DocumentState doc)
    {
        if (_vm is null) return;
        var export = ToView(doc.ExportArea.ToRectD());
        bool locked = !doc.AutoCanvas;
        dc.DrawRectangle(null, locked ? LockedCanvasPen : ExportPen, export);
        if (locked && Tool == ToolKind.Select && _drag is Drag.None or Drag.CanvasEdge) DrawHandles(dc, export, HandleFill);

        // Selection adorners
        foreach (var id in _vm.SelectedImages)
        {
            if (doc.FindImage(id) is not { } l) continue;
            var r = ToView(l.Bounds.ToRectD());
            dc.DrawRectangle(null, SelectionPen, r);
            if (_vm.SelectedImages.Count == 1 && _vm.SelectedAnnotations.Count == 0 && Tool == ToolKind.Select) DrawHandles(dc, r);
        }

        foreach (var distance in _distances) DrawSpacingMarker(dc, distance, label: true);
        foreach (var equal in _spacing)
        {
            DrawSpacingMarker(dc, equal.Moving, label: false);
            DrawSpacingMarker(dc, equal.Reference, label: true);
        }
        if (_hover is { } hid && !_vm.SelectedAnnotations.Contains(hid) && _drag == Drag.None && doc.FindAnnotation(hid) is { } ha)
            DrawOutline(dc, ha, HoverPen);
        foreach (var id in _vm.SelectedAnnotations)
        {
            if (doc.FindAnnotation(id) is not { } a) continue;
            DrawOutline(dc, a, AnnSelectionPen);
            if (_vm.SelectedAnnotations.Count == 1 && _vm.SelectedImages.Count == 0 && Tool == ToolKind.Select) DrawAnnotationHandles(dc, a);
            if (a.Locked) DrawLockBadge(dc, ToView(AnnotationDocBounds(doc, a)));
        }

        foreach (var g in _guides)
        {
            if (g.Vertical) { var x = ToView(new PointD(g.Position, 0)).X; dc.DrawLine(GuidePen, new Point(x, 0), new Point(x, ActualHeight)); }
            else { var y = ToView(new PointD(0, g.Position)).Y; dc.DrawLine(GuidePen, new Point(0, y), new Point(ActualWidth, y)); }
        }

        DrawDraft(dc, doc);
        if (_angleLabel is { } ang && _drag == Drag.AnnHandle)
            DrawSizeLabelText(dc, $"{ang:0.#}°", ToView(_curDoc));
    }

    private void DrawSizeLabelText(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, LabelText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var p = new Point(at.X + 14, at.Y + 14);
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(p.X, p.Y, ft.Width + 8, ft.Height + 4), 3, 3);
        dc.DrawText(ft, new Point(p.X + 4, p.Y + 2));
    }

    private void DrawHandles(DrawingContext dc, Rect r) => DrawHandles(dc, r, HandleFill);

    private void DrawHandles(DrawingContext dc, Rect r, Brush fill)
    {
        foreach (var h in Enum.GetValues<ResizeHandle>())
        {
            var p = HandlePoint(r, h);
            dc.DrawRectangle(fill, SelectionPen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
        }
    }

    /// <summary>True when <paramref name="p"/> is within <paramref name="tol"/> DIPs of the rectangle's outline.</summary>
    private static bool OnRectEdge(Rect r, Point p, double tol)
    {
        var outer = r; outer.Inflate(tol, tol);
        if (!outer.Contains(p)) return false;
        var inner = r; inner.Inflate(-tol, -tol);
        return inner.IsEmpty || !inner.Contains(p);
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
            new Typeface("Segoe UI"), 11, LabelText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var at = new Point(view.X, Math.Max(0, view.Y - ft.Height - 6));
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(at.X, at.Y, ft.Width + 8, ft.Height + 4), 3, 3);
        dc.DrawText(ft, new Point(at.X + 4, at.Y + 2));
    }

    // ================================================================== hit testing

    private static RectD AnnotationDocBounds(DocumentState doc, Annotation a) =>
        a is LineAnnotation or FreehandAnnotation ? a.Extent() : a.RotatedBox;

    /// <summary>Topmost annotation whose painted shape is under <paramref name="p"/> (unfilled interiors are see-through).</summary>
    private Annotation? HitAnnotation(DocumentState doc, PointD p) =>
        AnnotationGeometry.HitStack(doc.Annotations, p, 6 / _view.Zoom).FirstOrDefault();

    /// <summary>Rotate-handle offset in document pixels (constant 24 DIPs on screen).</summary>
    private double RotateDistance => 24 / _view.Zoom;

    private IReadOnlyList<AnnotationHandle> HandlesOf(Annotation a) => AnnotationGeometry.Handles(a, RotateDistance);

    private AnnotationHandle? HitAnnotationHandle(Annotation a, Point viewPoint) =>
        AnnotationGeometry.HitHandle(HandlesOf(a), ToDoc(viewPoint), HandleSize / _view.Zoom);

    private void DrawOutline(DrawingContext dc, Annotation a, Pen pen)
    {
        if (a is LineAnnotation l)
        {
            var pts = AnnotationGeometry.Sample(l);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(ToView(pts[0]), false, false);
                for (int i = 1; i < pts.Length; i++) c.LineTo(ToView(pts[i]), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            return;
        }
        var corners = AnnotationGeometry.Outline(a);
        if (corners is null) { dc.DrawRectangle(null, pen, ToView(a.Extent())); return; }
        var poly = new StreamGeometry();
        using (var c = poly.Open())
        {
            c.BeginFigure(ToView(corners[0]), false, true);
            for (int i = 1; i < corners.Length; i++) c.LineTo(ToView(corners[i]), true, false);
        }
        poly.Freeze();
        dc.DrawGeometry(null, pen, poly);
    }

    private void DrawAnnotationHandles(DrawingContext dc, Annotation a)
    {
        var hs = HandlesOf(a);
        // Connector from box top to the rotate handle.
        foreach (var h in hs)
        {
            var p = ToView(h.Position);
            switch (h.Kind)
            {
                case HandleKind.Resize:
                    dc.DrawRectangle(HandleFill, SelectionPen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
                    break;
                case HandleKind.Rotate:
                    dc.DrawEllipse(HandleFill, SelectionPen, p, 5, 5);
                    DrawRotateGlyph(dc, p);
                    break;
                case HandleKind.Start or HandleKind.End:
                    dc.DrawEllipse(HandleFill, SelectionPen, p, HandleSize / 2 + 1, HandleSize / 2 + 1);
                    break;
                case HandleKind.Bend:
                    dc.DrawGeometry(SpecialFill, SelectionPen, Polygon([new(p.X, p.Y - 5), new(p.X + 5, p.Y), new(p.X, p.Y + 5), new(p.X - 5, p.Y)]));
                    break;
                case HandleKind.Tail:
                    dc.DrawGeometry(SpecialFill, SelectionPen, Polygon([new(p.X, p.Y - 5), new(p.X + 5, p.Y + 4), new(p.X - 5, p.Y + 4)]));
                    break;
            }
        }
    }

    private static StreamGeometry Polygon(Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(points[0], true, true);
            c.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        return Frozen(geometry);
    }

    private void DrawRotateGlyph(DrawingContext dc, Point p)
    {
        var arc = new StreamGeometry();
        using (var c = arc.Open())
        {
            c.BeginFigure(new Point(p.X - 2, p.Y + 1), false, false);
            c.ArcTo(new Point(p.X + 2, p.Y - 1), new Size(2.5, 2.5), 0, true, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(p.X + 2, p.Y + 1), true, false);
            c.LineTo(new Point(p.X, p.Y), true, false);
        }
        dc.DrawGeometry(null, SelectionPen, Frozen(arc));
    }

    private void DrawLockBadge(DrawingContext dc, Rect r)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double x = Math.Round((r.Right + 3) * dpi.DpiScaleX) / dpi.DpiScaleX;
        double y = Math.Round((r.Y - 18) * dpi.DpiScaleY) / dpi.DpiScaleY;
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(x, y, 20, 20), 3, 3);
        dc.PushTransform(new TranslateTransform(x + 2, y + 2));
        dc.DrawGeometry(null, LockPen, _lockGeometry);
        dc.Pop();
    }

    private void DrawSpacingMarker(DrawingContext dc, SpacingDistance gap, bool label)
    {
        var a = ToView(gap.Start); var b = ToView(gap.End);
        dc.DrawLine(GuidePen, a, b);
        var cross = gap.Horizontal ? new Vector(0, 4) : new Vector(4, 0);
        dc.DrawLine(GuidePen, a - cross, a + cross);
        dc.DrawLine(GuidePen, b - cross, b + cross);
        if (!label) return;
        var ft = new FormattedText($"{gap.Pixels:0.#} px", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, GuideText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var at = new Point((a.X + b.X - ft.Width) / 2 - 4, (a.Y + b.Y - ft.Height) / 2 - 2);
        dc.DrawRoundedRectangle(GuidePen.Brush, null, new Rect(at.X, at.Y, ft.Width + 8, ft.Height + 4), 3, 3);
        dc.DrawText(ft, new Point(at.X + 4, at.Y + 2));
    }

    private static Cursor LoadRotateCursor()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/SnagItOpen;component/Assets/Cursors/rotate.cur"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Cursor(stream, true);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException) { }
        return Cursors.Cross;
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
        _keyboardFocus = false;
        _pointerFocusing = true;
        try { Focus(); } finally { _pointerFocusing = false; }
        RefreshAdorners();
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
        if (_drag == Drag.None) { e.Handled = true; return; }
        CaptureMouse();
        e.Handled = true;
    }

    // Ctrl/Shift+click on an already-selected item removes it, but only if the press did not become a drag.
    private (Guid Id, bool IsImage)? _deferToggle;
    private Guid[] _dupAnnIds = [], _dupImgIds = [];

    private void BeginSelect(DocumentState doc, Point p)
    {
        var vm = _vm!;
        _deferToggle = null;
        // Ctrl or Shift adds to / removes from the selection (Ctrl+drag also duplicates).
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Handles of the single selected item take priority.
        if (vm.SelectedImages.Count == 1 && vm.SelectedAnnotations.Count == 0 && vm.SelectedLayer is { } sel &&
            HitHandle(ToView(sel.Bounds.ToRectD()), p) is { } h)
        {
            _drag = Drag.Resize; _handle = h; _resizeStart = sel.Bounds; _resizeTarget = sel.Id;
            return;
        }
        if (vm.SelectedAnnotations.Count == 1 && vm.SelectedImages.Count == 0 && vm.PrimaryAnnotation is { } sa &&
            HitAnnotationHandle(sa, p) is { } ah)
        {
            _drag = Drag.AnnHandle; _annHandle = ah; _annStart = sa; _resizeTarget = sa.Id;
            return;
        }
        // Locked canvas: its handles resize it; grabbing the dotted edge moves it.
        if (!doc.AutoCanvas && !ctrl)
        {
            var er = ToView(doc.ExportArea.ToRectD());
            if (HitHandle(er, p) is { } ch)
            {
                _drag = Drag.CanvasEdge; _handle = ch; _canvasStart = doc.ExportArea;
                return;
            }
            if (OnRectEdge(er, p, 4) && HitAnnotation(doc, _downDoc) is null)
            {
                _drag = Drag.CanvasMove; _canvasStart = doc.ExportArea;
                return;
            }
        }

        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        double tol = 6 / _view.Zoom;
        var stack = AnnotationGeometry.HitStack(doc.Annotations, _downDoc, tol);
        Annotation? ann;
        if (alt && stack.Count > 0)
        {
            // Alt+click cycles down through everything under the pointer.
            int cur = -1;
            for (int i = 0; i < stack.Count; i++) if (vm.SelectedAnnotations.Contains(stack[i].Id)) { cur = i; break; }
            ann = stack[(cur + 1) % stack.Count];
            vm.Select([], [ann.Id]);
            _drag = ann.Locked ? Drag.None : Drag.Move;
            if (ann.Locked) Cursor = Cursors.No;
            return;
        }
        // A selected item can be dragged from anywhere inside its box, even a see-through interior.
        ann = !ctrl ? doc.Annotations.LastOrDefault(a => vm.SelectedAnnotations.Contains(a.Id) && !a.Hidden
                          && AnnotationGeometry.InsideBox(a, _downDoc, tol)) : null;
        ann ??= stack.FirstOrDefault();
        var img = ann is null ? DocumentOps.HitTestImage(doc, _downDoc) : null;
        if (ann is not null)
        {
            if (ctrl)
            {
                if (vm.SelectedAnnotations.Contains(ann.Id)) _deferToggle = (ann.Id, false);
                else vm.Select(vm.SelectedImages, vm.SelectedAnnotations.Append(ann.Id));
            }
            else if (!vm.SelectedAnnotations.Contains(ann.Id)) vm.Select([], [ann.Id]);
            _drag = ann.Locked ? Drag.None : Drag.Move;
            if (ann.Locked) Cursor = Cursors.No;
        }
        else if (img is not null)
        {
            if (ctrl)
            {
                if (vm.SelectedImages.Contains(img.Id)) _deferToggle = (img.Id, true);
                else vm.Select(vm.SelectedImages.Append(img.Id), vm.SelectedAnnotations);
            }
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
        if (_vm is null || _drag == Drag.None)
        {
            var hp = e.GetPosition(this);
            UpdateHoverCursor(hp);
            if (_vm is not null)
            {
                UpdateHover(hp);
            }
            return;
        }
        var p = e.GetPosition(this);
        _curDoc = ToDoc(p);
        var baseDoc = _vm.Document;
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (_drag)
        {
            case Drag.CanvasEdge:
                {
                    var nb = ResizeGeometry.Resize(_canvasStart, _handle, _curDoc.X - _downDoc.X, _curDoc.Y - _downDoc.Y, keepAspect: shift);
                    if (Limits.IsAcceptableExportSize(nb.Width, nb.Height)) TryPreview(d => DocumentOps.SetExportArea(d, nb));
                    return;
                }
            case Drag.CanvasMove:
                {
                    var nb = _canvasStart.Translate((int)Math.Round(_curDoc.X - _downDoc.X), (int)Math.Round(_curDoc.Y - _downDoc.Y));
                    TryPreview(d => DocumentOps.SetExportArea(d, nb));
                    return;
                }
            case Drag.Pan:
                _view = _view with { PanX = _panStart.X + (p.X - _downView.X), PanY = _panStart.Y + (p.Y - _downView.Y) };
                AfterViewChange();
                return;
            case Drag.Move:
                {
                    double dx = _curDoc.X - _downDoc.X, dy = _curDoc.Y - _downDoc.Y;
                    if (Math.Abs(p.X - _downView.X) < 2 && Math.Abs(p.Y - _downView.Y) < 2) return;
                    _deferToggle = null; // it is a drag now, not a click
                    (dx, dy) = Snap(baseDoc, dx, dy, alt);
                    int ix = (int)Math.Round(dx), iy = (int)Math.Round(dy);
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        // Ctrl+drag: leave the originals and drag copies (release Ctrl mid-drag to move instead).
                        Cursor = Cursors.Cross;
                        TryPreview(d =>
                        {
                            var (d1, imgs) = _vm.SelectedImages.Count > 0 ? DocumentOps.Duplicate(d, _vm.SelectedImages, ix, iy) : (d, []);
                            var unlocked = _vm.SelectedAnnotations.Where(id => d.FindAnnotation(id) is { Locked: false }).ToHashSet();
                            var (d2, anns) = unlocked.Count > 0 ? DocumentOps.DuplicateAnnotations(d1, unlocked, ix, iy) : (d1, []);
                            _dupImgIds = imgs; _dupAnnIds = anns;
                            return d2;
                        });
                        _vm.Status = "Release to drop copies (Ctrl+drag duplicates).";
                        return;
                    }
                    _dupImgIds = []; _dupAnnIds = [];
                    Cursor = null;
                    var movable = _vm.SelectedAnnotations.Where(id => baseDoc.FindAnnotation(id) is { Locked: false }).ToHashSet();
                    TryPreview(d => DocumentOps.Move(d, _vm.SelectedImages, movable, ix, iy));
                    return;
                }
            case Drag.Resize:
                {
                    double dx = _curDoc.X - _downDoc.X, dy = _curDoc.Y - _downDoc.Y;
                    var nb = ResizeGeometry.Resize(_resizeStart, _handle, dx, dy, keepAspect: !shift);
                    TryPreview(d => DocumentOps.SetBounds(d, _resizeTarget, nb));
                    return;
                }
            case Drag.AnnHandle:
                {
                    if (_annStart is not { } start) return;
                    var changed = AnnotationGeometry.Drag(start, _annHandle, _downDoc, _curDoc, shift);
                    if (changed is TextAnnotation ta && _annHandle.Kind == HandleKind.Resize && ta is not CalloutAnnotation { Shape: CalloutShape.Ellipse })
                    {
                        // Dragging a top/bottom edge sets the height (Fixed); other handles set the width and the height follows.
                        bool heightOnly = _annHandle.Corner is ResizeHandle.Top or ResizeHandle.Bottom;
                        var sizing = heightOnly ? TextSizing.Fixed : ta.Sizing == TextSizing.Fixed ? TextSizing.Fixed : TextSizing.AutoHeight;
                        changed = AnnotationRenderer.Fit(ta with { Sizing = sizing });
                    }
                    _angleLabel = _annHandle.Kind == HandleKind.Rotate
                        ? (changed is LineAnnotation ln ? Rotation2D.Normalize(Rotation2D.AngleOf(ln.Start, ln.End)) : changed.Rotation)
                        : null;
                    TryPreview(d => DocumentOps.UpdateAnnotation(d, changed));
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
        var vm = _vm!;
        if (vm.SelectedImages.Count == 1 && vm.SelectedAnnotations.Count == 0 && vm.SelectedLayer is { } l && HitHandle(ToView(l.Bounds.ToRectD()), p) is { } imageHandle)
            return ResizeCursor(imageHandle);
        if (vm.SelectedAnnotations.Count == 1 && vm.SelectedImages.Count == 0 && vm.PrimaryAnnotation is { } a && HitAnnotationHandle(a, p) is { } h)
            return h.Kind switch
            {
                HandleKind.Rotate => RotateCursor.Value,
                HandleKind.Start or HandleKind.End or HandleKind.Bend => Cursors.Cross,
                HandleKind.Tail => Cursors.Hand,
                _ => ResizeCursor(h.Corner),
            };
        if (HitAnnotation(vm.Document, ToDoc(p)) is { Locked: true }) return Cursors.No;
        if (!vm.Document.AutoCanvas)
        {
            var canvas = ToView(vm.Document.ExportArea.ToRectD());
            if (HitHandle(canvas, p) is { } canvasHandle) return ResizeCursor(canvasHandle);
            if (OnRectEdge(canvas, p, 4)) return Cursors.SizeAll;
        }
        return null;
    }

    private static Cursor ResizeCursor(ResizeHandle h) => h switch
        {
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            _ => Cursors.SizeWE,
        };

    private (double, double) Snap(DocumentState doc, double dx, double dy, bool alt)
    {
        _guides = [];
        _distances = [];
        _spacing = [];
        if (!SnapEnabled || alt || !TrySelectionBounds(doc, out var moving, movableOnly: true)) return (dx, dy);
        var targets = doc.Images.Where(i => i.Visible && !_vm!.SelectedImages.Contains(i.Id))
            .Select(i => new SnapEngine.Candidate(i.Id.ToString(), i.Bounds.ToRectD())).ToList();
        targets.AddRange(doc.Annotations.Where(a => (!a.Hidden || a is RedactionAnnotation) && !_vm!.SelectedAnnotations.Contains(a.Id))
            .Select(a => new SnapEngine.Candidate(a.Id.ToString(), AnnotationDocBounds(doc, a))));
        var neighbours = targets.ToArray();
        targets.Add(new SnapEngine.Candidate("canvas", doc.ExportArea.ToRectD()));
        var r = SnapEngine.SnapMove(moving, dx, dy, targets, _view.Zoom);
        var spacing = SpacingGuides.Snap(moving.Offset(dx, dy), neighbours, SnapEngine.DefaultToleranceDips / _view.Zoom);
        bool useX = spacing.Equal.Any(e => e.Moving.Horizontal) &&
            (!r.Guides.Any(g => g.Vertical) || Math.Abs(spacing.Dx) <= Math.Abs(r.Dx - dx));
        bool useY = spacing.Equal.Any(e => !e.Moving.Horizontal) &&
            (!r.Guides.Any(g => !g.Vertical) || Math.Abs(spacing.Dy) <= Math.Abs(r.Dy - dy));
        double x = useX ? dx + spacing.Dx : r.Dx, y = useY ? dy + spacing.Dy : r.Dy;
        _guides = r.Guides.Where(g => g.Vertical ? !useX : !useY).ToArray();
        var final = moving.Offset(Math.Round(x), Math.Round(y));
        _distances = SpacingGuides.Nearest(final, neighbours);
        _spacing = SpacingGuides.Detect(final, neighbours);
        return (x, y);
    }

    private bool TrySelectionBounds(DocumentState doc, out RectD bounds, bool movableOnly = false)
    {
        bounds = default;
        if (_vm is null) return false;
        bool any = false;
        foreach (var id in _vm.SelectedImages)
            if (doc.FindImage(id) is { } image)
            {
                bounds = any ? bounds.Union(image.Bounds.ToRectD()) : image.Bounds.ToRectD(); any = true;
            }
        foreach (var id in _vm.SelectedAnnotations)
            if (doc.FindAnnotation(id) is { } annotation && (!movableOnly || !annotation.Locked))
            {
                var r = AnnotationDocBounds(doc, annotation);
                bounds = any ? bounds.Union(r) : r; any = true;
            }
        return any;
    }

    private void UpdateHover(Point p)
    {
        if (_vm is null) return;
        var doc = _vm.Displayed;
        var ann = Tool == ToolKind.Select ? HitAnnotation(doc, ToDoc(p)) : null;
        Guid? hover = ann?.Id ?? (Tool == ToolKind.Select ? DocumentOps.HitTestImage(doc, ToDoc(p))?.Id : null);
        _hover = hover;
        _distances = [];
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _vm.SelectedImages.Count + _vm.SelectedAnnotations.Count == 1
            && hover is { } id && !_vm.SelectedImages.Contains(id) && !_vm.SelectedAnnotations.Contains(id)
            && TrySelectionBounds(doc, out var selected))
        {
            RectD? target = ann is not null ? AnnotationDocBounds(doc, ann) : doc.FindImage(id)?.Bounds.ToRectD();
            if (target is { } rect) _distances = SpacingGuides.Between(selected, rect, id.ToString());
        }
        RefreshAdorners();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag != Drag.None) return;
        _hover = null;
        _distances = [];
        RefreshAdorners();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // Programmatic focus (startup, after dialogs) must not light the ring; only focus that arrives by keyboard.
        _keyboardFocus = !_pointerFocusing && InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        RefreshAdorners();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        _keyboardFocus = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) UpdateHover(Mouse.GetPosition(this));
        RefreshAdorners();
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if ((e.Key is Key.LeftCtrl or Key.RightCtrl) && _drag == Drag.None) { _distances = []; RefreshAdorners(); }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _keyboardFocus = false;
        _spaceDown = false;
        _distances = [];
        CancelGesture();
        RefreshAdorners();
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
        _angleLabel = null;
        ReleaseMouseCapture();
        _guides = [];
        _distances = [];
        _spacing = [];
        _curDoc = ToDoc(e.GetPosition(this));
        if (drag == Drag.Pan) { Cursor = _spaceDown ? Cursors.Hand : null; RefreshAdorners(); return; }

        var preview = _vm.PreviewState;
        _vm.SetPreview(null);
        switch (drag)
        {
            case Drag.Move:
                if (_deferToggle is { } dt)
                {
                    // Ctrl/Shift+click (no drag) on a selected item: remove it from the selection.
                    if (dt.IsImage) _vm.Select(_vm.SelectedImages.Where(i => i != dt.Id), _vm.SelectedAnnotations);
                    else _vm.Select(_vm.SelectedImages, _vm.SelectedAnnotations.Where(i => i != dt.Id));
                }
                else if (preview is not null && (_dupAnnIds.Length > 0 || _dupImgIds.Length > 0))
                {
                    var (ai, ii) = (_dupAnnIds, _dupImgIds);
                    int n = ai.Length + ii.Length;
                    if (_vm.Commit(n == 1 ? "Duplicate" : $"Duplicate {n} items", _ => preview))
                    {
                        _vm.Select(ii, ai);
                        _vm.Status = n == 1 ? "Duplicated." : $"Duplicated {n} items.";
                    }
                }
                else if (preview is not null) _vm.Commit("Move", _ => preview);
                _deferToggle = null; _dupAnnIds = []; _dupImgIds = [];
                Cursor = null;
                break;
            case Drag.Resize:
                if (preview is not null) _vm.Commit("Resize", _ => preview);
                break;
            case Drag.AnnHandle:
                if (preview is not null) _vm.Commit("Resize annotation", _ => preview);
                break;
            case Drag.CanvasEdge or Drag.CanvasMove:
                if (preview is not null) _vm.Commit(drag == Drag.CanvasMove ? "Move canvas" : "Resize canvas", _ => preview);
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
        _distances = [];
        _spacing = [];
        _angleLabel = null;
        _deferToggle = null;
        _dupAnnIds = []; _dupImgIds = [];
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
        var anns = doc.Annotations.Where(a => !a.Hidden && Intersects(AnnotationDocBounds(doc, a), r)).Select(a => a.Id);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
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
            vm.AddAnnotation(Proto<MagnifierAnnotation>(Tool) with { SourceRegion = drRect, Bounds = dest });
            return;
        }
        if (tiny) return;

        // Annotations are canvas objects in document pixels; redactions cover exactly what was dragged.
        var a0 = _downDoc; var a1 = _curDoc;
        var b = RectD.FromPoints(a0, a1);
        // New items take the tool's prototype look (edited in the properties panel).
        Annotation? ann = Tool switch
        {
            ToolKind.Rectangle => Proto<RectangleAnnotation>(Tool) with { Bounds = b },
            ToolKind.Ellipse => Proto<EllipseAnnotation>(Tool) with { Bounds = b },
            ToolKind.Arrow => PlaceLine(Proto<ArrowAnnotation>(Tool), a0, a1),
            ToolKind.Line => PlaceLine(Proto<LineAnnotation>(Tool), a0, a1),
            ToolKind.Highlight => Proto<HighlightAnnotation>(Tool) with { Bounds = b },
            ToolKind.Redaction => Proto<RedactionAnnotation>(Tool) is var r ? r with { Bounds = b, Color = r.Color with { A = 255 } } : null,
            _ => null,
        };
        if (ann is null) return;
        vm.AddAnnotation(ann);
    }

    private static LineAnnotation PlaceLine(LineAnnotation prototype, PointD start, PointD end) => prototype with
    {
        Start = start, End = end, Bounds = RectD.FromPoints(start, end),
        Control = prototype.Control is null ? null : new PointD((start.X + end.X) / 2 - (end.Y - start.Y) * 0.3,
            (start.Y + end.Y) / 2 + (end.X - start.X) * 0.3),
    };

    private void FinishFreehand()
    {
        var vm = _vm!;
        if (_freehand.Count < 2) { _freehand.Clear(); return; }
        var pts = _freehand.ToList();
        _freehand.Clear();
        var simplified = CaptureMask.Simplify(pts, 0.75, FreehandAnnotation.MaxPoints);
        vm.AddAnnotation(Proto<FreehandAnnotation>(ToolKind.Freehand) with { Points = simplified, Bounds = FreehandAnnotation.BoundsOf(simplified) });
    }

    private void PlaceClickAnnotation(DocumentState doc)
    {
        var vm = _vm!;
        var p = _downDoc;
        switch (Tool)
        {
            case ToolKind.Step:
                {
                    var proto = Proto<StepAnnotation>(Tool);
                    double size = proto.Bounds.Width > 4 ? proto.Bounds.Width : 32;
                    var step = proto with { Bounds = new RectD(p.X - size / 2, p.Y - size / 2, size, size), Number = DocumentOps.NextStepNumber(doc) };
                    if (proto.Tail is not null) step = step with { Tail = new PointD(p.X + size * 1.5, p.Y + size) };
                    vm.AddAnnotation(step);
                    break;
                }
            case ToolKind.Stamp:
                {
                    var proto = Proto<StampAnnotation>(Tool);
                    double size = proto.Bounds.Width > 4 ? proto.Bounds.Width : 64;
                    vm.AddAnnotation(proto with { Bounds = new RectD(p.X - size / 2, p.Y - size / 2, size, size), Symbol = proto.Symbol ?? StampSymbols.Check, AssetId = null });
                    break;
                }
            case ToolKind.Text:
            case ToolKind.Callout:
                {
                    var proto = Tool == ToolKind.Callout ? Proto<CalloutAnnotation>(Tool) : Proto<TextAnnotation>(Tool);
                    // Click-placed text grows with what is typed and centres in its box (TXT1/TXT2).
                    proto = proto with { Sizing = TextSizing.AutoWidth, VerticalAlign = TextVAlign.Middle };
                    double fs = proto.FontSize;
                    var w = Math.Max(120, fs * 10);
                    var box = new RectD(p.X, p.Y, w, fs * 1.6 + 2 * Math.Max(0, proto.Padding));
                    TextAnnotation t = proto is CalloutAnnotation co
                        ? co with { Bounds = box with { Y = p.Y - box.Height - 40, X = p.X + 30 }, Tail = p, Text = "" }
                        : proto with { Bounds = box, Text = "" };
                    EditTextRequested?.Invoke(t, true);
                    break;
                }
        }
    }

}
