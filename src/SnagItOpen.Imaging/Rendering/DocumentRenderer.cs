using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Effects;
using static SnagItOpen.Imaging.Rendering.WpfConvert;

namespace SnagItOpen.Imaging.Rendering;

public sealed record RenderSettings
{
    public static readonly RenderSettings Default = new();
    /// <summary>Paint the document background over the export area.</summary>
    public bool DrawBackground { get; init; } = true;
    /// <summary>Annotations to skip (e.g. the one being edited in place).</summary>
    public IReadOnlySet<Guid>? HiddenAnnotations { get; init; }
}

/// <summary>
/// The single scene renderer shared by preview and export. Drawing order:
/// background → per image (shadow, processed pixels clipped to edge shape, border, linked annotations,
/// linked redactions) → document annotations → document redactions → magnifiers (sampling a scene
/// without magnifiers) → all redactions again so covered pixels always stay covered.
/// Editor adorners never pass through here.
/// </summary>
public sealed class DocumentRenderer
{
    private readonly BitmapAssetCache _cache;
    private readonly ConditionalWeakTable<DocumentState, Dictionary<PixelRect, BitmapSource>> _magnifierBase = new();

    public DocumentRenderer(BitmapAssetCache cache) => _cache = cache;

    public BitmapAssetCache Cache => _cache;

    /// <summary>Draws the document in document-pixel coordinates.</summary>
    public void Draw(DrawingContext dc, DocumentState doc, RenderSettings? settings = null)
    {
        var s = settings ?? RenderSettings.Default;
        if (s.DrawBackground && doc.Background.A > 0)
            dc.DrawRectangle(Brush(doc.Background), null, doc.ExportArea.ToRect());
        DrawScene(dc, doc, s, includeMagnifiers: true);
    }

    /// <summary>Renders <paramref name="area"/> (document pixels) at <paramref name="scale"/> into a frozen Pbgra32 bitmap.</summary>
    public BitmapSource RenderToBitmap(DocumentState doc, PixelRect area, double scale = 1, RenderSettings? settings = null)
    {
        if (area.IsEmpty) throw new ArgumentOutOfRangeException(nameof(area));
        int w = Math.Max(1, (int)Math.Round(area.Width * scale, MidpointRounding.AwayFromZero));
        int h = Math.Max(1, (int)Math.Round(area.Height * scale, MidpointRounding.AwayFromZero));
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            if (scale != 1) dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-area.X, -area.Y));
            Draw(dc, doc, settings);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Flattened export-area pixels at 1:1 (straight alpha).</summary>
    public PixelBuffer RenderToPixels(DocumentState doc, RenderSettings? settings = null) =>
        PixelBuffer.FromBitmap(RenderToBitmap(doc, doc.ExportArea, 1, settings));

    /// <summary>Small preview whose longest side is at most <paramref name="maxSide"/>.</summary>
    public BitmapSource RenderThumbnail(DocumentState doc, int maxSide)
    {
        var a = doc.ExportArea;
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(a.Width, a.Height));
        return RenderToBitmap(doc, a, scale);
    }

    private BitmapSource? ResolveAsset(string id)
    {
        try { return _cache.Get(id); }
        catch (AssetNotFoundException) { return null; }
    }

    private void DrawScene(DrawingContext dc, DocumentState doc, RenderSettings s, bool includeMagnifiers)
    {
        var hidden = s.HiddenAnnotations;
        // Hidden items are skipped, except redactions: they always cover, so hiding can never expose pixels.
        bool Show(Annotation a) => (hidden is null || !hidden.Contains(a.Id)) && (!a.Hidden || a is RedactionAnnotation);
        // Annotations are canvas objects: convert any legacy image-linked ones to document space.
        doc = AnnotationCanvas.Normalize(doc);

        // 1. Images in drawing order.
        foreach (var layer in doc.Images)
            if (layer.Visible) DrawLayer(dc, layer);

        // 2. Annotations in their own order, above all images, never clipped to an image.
        foreach (var a in doc.Annotations)
            if (Show(a) && a is not RedactionAnnotation and not MagnifierAnnotation)
                AnnotationRenderer.Draw(dc, a, ResolveAsset);

        // 3. Redactions last so they always cover everything beneath them.
        foreach (var a in doc.Annotations)
            if (Show(a) && a is RedactionAnnotation r) DrawRedaction(dc, doc, r);

        if (!includeMagnifiers) return;
        bool any = false;
        foreach (var a in doc.Annotations)
            if (a is MagnifierAnnotation m && Show(m)) { DrawMagnifier(dc, doc, m); any = true; }
        if (!any) return;
        foreach (var a in doc.Annotations)
            if (a is RedactionAnnotation r && Show(r)) DrawRedaction(dc, doc, r);
    }

    private void DrawLayer(DrawingContext dc, ImageLayer layer)
    {
        var edge = layer.Edge is { IsEmpty: false } e ? e : null;
        var shape = EdgeEffectRenderer.Shape(layer.Bounds, edge);
        if (edge is { ShadowSize: > 0 }) EdgeEffectRenderer.DrawShadow(dc, shape, edge);

        BitmapSource? bmp;
        try { bmp = _cache.GetProcessed(layer); }
        catch (AssetNotFoundException) { bmp = null; }

        dc.PushClip(shape);
        if (bmp is null)
        {
            var r = layer.Bounds.ToRect();
            dc.DrawRectangle(Brush(new Rgba32(220, 220, 220, 255)), null, r);
            var pen = Pen(new Rgba32(200, 40, 40, 255), 2);
            dc.DrawLine(pen, r.TopLeft, r.BottomRight);
            dc.DrawLine(pen, r.TopRight, r.BottomLeft);
        }
        else
        {
            var crop = layer.SourceCrop.Intersect(new PixelRect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            if (!crop.IsEmpty)
            {
                BitmapSource src = bmp;
                if (crop.X != 0 || crop.Y != 0 || crop.Width != bmp.PixelWidth || crop.Height != bmp.PixelHeight)
                {
                    var cb = new CroppedBitmap(bmp, new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
                    cb.Freeze();
                    src = cb;
                }
                dc.PushTransform(new MatrixTransform(ImageTransform.SourceToDocument(layer).ToMatrix()));
                dc.DrawImage(src, new Rect(crop.X, crop.Y, crop.Width, crop.Height));
                dc.Pop();
            }
        }
        dc.Pop();

        if (edge is { BorderWidth: > 0 }) EdgeEffectRenderer.DrawBorder(dc, shape, edge);
    }

    /// <summary>Document-pixel rectangle a redaction covers (outward-rounded; never clipped to an image).</summary>
    public static PixelRect RedactionRect(DocumentState doc, RedactionAnnotation r)
    {
        var b = AnnotationCanvas.ToDocument(doc, r).Bounds;
        return b.IsEmpty ? default : b.ToPixelRectOutward();
    }

    private static void DrawRedaction(DrawingContext dc, DocumentState doc, RedactionAnnotation r)
    {
        var rect = RedactionRect(doc, r);
        if (rect.IsEmpty) return;
        dc.DrawRectangle(Brush(r.Color with { A = 255 }), null, rect.ToRect());
    }

    private void DrawMagnifier(DrawingContext dc, DocumentState doc, MagnifierAnnotation m)
    {
        var src = m.SourceRegion;
        var dest = m.Bounds.ToRect();
        if (src.IsEmpty || dest.Width <= 0 || dest.Height <= 0) return;
        var area = src.ToPixelRectOutward();
        if (!Limits.IsAcceptableExportSize(area.Width, area.Height)) return;
        var baseBmp = MagnifierBase(doc, area);
        Geometry clip = m.Circular ? new EllipseGeometry(dest) : new RectangleGeometry(dest);
        clip.Freeze();
        double sx = dest.Width / src.Width, sy = dest.Height / src.Height;
        dc.PushClip(clip);
        dc.DrawRectangle(Brushes.White, null, dest);
        dc.DrawImage(baseBmp, new Rect(dest.X - (src.X - area.X) * sx, dest.Y - (src.Y - area.Y) * sy, area.Width * sx, area.Height * sy));
        dc.Pop();
        if (m.StrokeWidth > 0) dc.DrawGeometry(null, Pen(m.Color, m.StrokeWidth), clip);
    }

    /// <summary>Scene without magnifiers (so lenses never recurse), including redactions.</summary>
    private BitmapSource MagnifierBase(DocumentState doc, PixelRect area)
    {
        var map = _magnifierBase.GetOrCreateValue(doc);
        lock (map)
        {
            if (map.TryGetValue(area, out var cached)) return cached;
        }
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-area.X, -area.Y));
            if (doc.Background.A > 0) dc.DrawRectangle(Brush(doc.Background), null, area.ToRect());
            DrawScene(dc, doc, RenderSettings.Default, includeMagnifiers: false);
        }
        var rtb = new RenderTargetBitmap(area.Width, area.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        lock (map) map[area] = rtb;
        return rtb;
    }
}
