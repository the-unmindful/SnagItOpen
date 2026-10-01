using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;

namespace SnagItOpen.App.Editor;

/// <summary>
/// Cached first-party previews drawn by the same renderer as canvas/export (F-STY3): rendered at the monitor DPI,
/// samples sized like a real annotation at 100 % and then fitted, so stroke weights, padding and corner radii keep
/// their real proportions instead of looking cramped.
/// </summary>
public sealed class StyleThumbnailRenderer
{
    public const int TileWidth = 60, TileHeight = 40;
    private readonly Dictionary<(Annotation Style, EffectiveTheme Theme, Color A, Color B, double Scale), BitmapSource> _cache = [];
    public int RenderCount { get; private set; }
    public BitmapSource Render(Annotation style, EffectiveTheme theme, double scale = 1)
    {
        scale = Math.Clamp(scale, 1, 4);
        Color a = (Application.Current?.TryFindResource("Checker.A") as SolidColorBrush)?.Color ?? (theme == EffectiveTheme.Dark ? Color.FromRgb(45, 45, 45) : Colors.White);
        Color b = (Application.Current?.TryFindResource("Checker.B") as SolidColorBrush)?.Color ?? (theme == EffectiveTheme.Dark ? Color.FromRgb(60, 60, 60) : Color.FromRgb(220, 220, 220));
        var key = (style, theme, a, b, scale); if (_cache.TryGetValue(key, out var cached)) return cached;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            var first = new SolidColorBrush(a); first.Freeze(); var second = new SolidColorBrush(b); second.Freeze();
            dc.DrawRectangle(first, null, new Rect(0, 0, TileWidth, TileHeight));
            for (int y = 0; y < TileHeight; y += 5) for (int x = 0; x < TileWidth; x += 5) if ((x / 5 + y / 5) % 2 != 0) dc.DrawRectangle(second, null, new Rect(x, y, 5, 5));
            var sample = Sample(style); var extent = sample.Extent();
            // Never enlarge (a 2 px arrow must not look like 6 px); shrink large samples to fit with a 5 px margin.
            double factor = Math.Min(1.0, Math.Min((TileWidth - 10) / Math.Max(1, extent.Width), (TileHeight - 10) / Math.Max(1, extent.Height)));
            dc.PushTransform(new TranslateTransform((TileWidth - extent.Width * factor) / 2 - extent.X * factor, (TileHeight - extent.Height * factor) / 2 - extent.Y * factor));
            dc.PushTransform(new ScaleTransform(factor, factor));
            AnnotationRenderer.Draw(dc, sample); dc.Pop(); dc.Pop(); dc.Pop();
        }
        int pw = (int)Math.Ceiling(TileWidth * scale), ph = (int)Math.Ceiling(TileHeight * scale);
        var bitmap = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); RenderCount++;
        if (_cache.Count >= 512) _cache.Clear(); _cache[key] = bitmap; return bitmap;
    }

    /// <summary>A representative sample at real (100 %) size; the tile shrinks it if needed.</summary>
    public static Annotation Sample(Annotation style)
    {
        var sample = style with { Id = Guid.Empty, Bounds = new RectD(0, 0, 70, 44), Rotation = 0, Hidden = false, Locked = false };
        return sample switch
        {
            LineAnnotation line => line with { Start = new PointD(0, 40), End = new PointD(70, 4), Control = line.Control is null ? null : new PointD(22, -10), Bounds = new RectD(0, 4, 70, 36) },
            CalloutAnnotation callout => Fitted(callout with { Text = "Text", FontSize = Math.Min(callout.FontSize, 22), TailWidth = Math.Min(callout.TailWidth, 14) }) is CalloutAnnotation c
                ? c with { Tail = new PointD(c.Bounds.X + c.Bounds.Width * 0.3, c.Bounds.Bottom + 14) } : callout,
            TextAnnotation text => Fitted(text with { Text = "Text", FontSize = Math.Min(text.FontSize, 26) }),
            StepAnnotation step => step with { Number = 1, Bounds = new RectD(0, 0, 32, 32), Tail = step.Tail is null ? null : new PointD(46, 40) },
            FreehandAnnotation pen => pen with { Points = [new PointD(0, 30), new PointD(14, 8), new PointD(28, 30), new PointD(42, 8), new PointD(56, 30)] },
            StampAnnotation stamp => stamp with { AssetId = null, Bounds = new RectD(0, 0, 36, 36) },
            _ => sample,
        };
    }

    private static TextAnnotation Fitted(TextAnnotation t) => AnnotationRenderer.Fit(t with { Sizing = TextSizing.AutoWidth, Bounds = new RectD(0, 0, 10, 10) });
}
