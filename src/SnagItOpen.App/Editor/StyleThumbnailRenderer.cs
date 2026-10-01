using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;

namespace SnagItOpen.App.Editor;

/// <summary>Cached first-party previews drawn by the same renderer as canvas/export.</summary>
public sealed class StyleThumbnailRenderer
{
    private readonly Dictionary<(Annotation Style, EffectiveTheme Theme, Color A, Color B), BitmapSource> _cache = [];
    public int RenderCount { get; private set; }
    public BitmapSource Render(Annotation style, EffectiveTheme theme)
    {
        Color a = (Application.Current?.TryFindResource("Checker.A") as SolidColorBrush)?.Color ?? (theme == EffectiveTheme.Dark ? Color.FromRgb(45, 45, 45) : Colors.White);
        Color b = (Application.Current?.TryFindResource("Checker.B") as SolidColorBrush)?.Color ?? (theme == EffectiveTheme.Dark ? Color.FromRgb(60, 60, 60) : Color.FromRgb(220, 220, 220));
        var key = (style, theme, a, b); if (_cache.TryGetValue(key, out var cached)) return cached;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var first = new SolidColorBrush(a); first.Freeze(); var second = new SolidColorBrush(b); second.Freeze();
            dc.DrawRectangle(first, null, new Rect(0, 0, 56, 40));
            for (int y = 0; y < 40; y += 4) for (int x = 0; x < 56; x += 4) if ((x / 4 + y / 4) % 2 != 0) dc.DrawRectangle(second, null, new Rect(x, y, 4, 4));
            var sample = Sample(style); var extent = sample.Extent(); double factor = Math.Min(44 / Math.Max(1, extent.Width), 30 / Math.Max(1, extent.Height));
            dc.PushTransform(new TranslateTransform((56 - extent.Width * factor) / 2 - extent.X * factor, (40 - extent.Height * factor) / 2 - extent.Y * factor)); dc.PushTransform(new ScaleTransform(factor, factor));
            AnnotationRenderer.Draw(dc, sample); dc.Pop(); dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(56, 40, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); RenderCount++;
        if (_cache.Count >= 512) _cache.Clear(); _cache[key] = bitmap; return bitmap;
    }
    public static Annotation Sample(Annotation style)
    {
        var sample = style with { Id = Guid.Empty, Bounds = new RectD(0, 0, 40, 24), Rotation = 0, Hidden = false, Locked = false };
        return sample switch
        {
            LineAnnotation line => line with { Start = new PointD(0, 24), End = new PointD(40, 0), Control = line.Control is null ? null : new PointD(15, -16), Bounds = new RectD(0, 0, 40, 24) },
            CalloutAnnotation callout => callout with { Text = "Aa", FontSize = 20, Bounds = new RectD(0, 0, 48, 30), Tail = new PointD(44, 44), TailWidth = 12 },
            TextAnnotation text => text with { Text = "Aa", FontSize = 24, Bounds = new RectD(0, 0, 52, 40) },
            StepAnnotation step => step with { Number = 1, Bounds = new RectD(0, 0, 32, 32), Tail = step.Tail is null ? null : new PointD(45, 40) },
            FreehandAnnotation pen => pen with { Points = [new PointD(0, 20), new PointD(10, 5), new PointD(20, 20), new PointD(30, 5), new PointD(40, 20)] },
            StampAnnotation stamp => stamp with { AssetId = null, Bounds = new RectD(0, 0, 32, 32) },
            _ => sample,
        };
    }
}
