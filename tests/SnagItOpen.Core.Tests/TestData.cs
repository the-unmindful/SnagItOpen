using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

/// <summary>Helpers for building small documents without any imaging dependency.</summary>
internal static class TestData
{
    private static int _counter;

    /// <summary>Deterministic fake SHA-256 hex id.</summary>
    public static string Hash(int n) => n.ToString("x").PadLeft(64, '0');

    public static ImageAsset Asset(int w, int h) => ImageAsset.Create(Hash(Interlocked.Increment(ref _counter)), w, h);

    public static ImageLayer Layer(ImageAsset a, int x = 0, int y = 0) => ImageLayer.ForAsset(a, x, y);

    /// <summary>Document with the given images added in order (triggers layout/reflow).</summary>
    public static DocumentState Doc(LayoutOptions options, params (int W, int H)[] sizes)
    {
        var d = DocumentState.CreateEmpty() with { Layout = options };
        var items = sizes.Select(s => { var a = Asset(s.W, s.H); return (a, Layer(a)); }).ToList();
        return Editing.DocumentOps.AddImages(d, items);
    }

    /// <summary>Free-mode document with explicit bounds, no auto layout.</summary>
    public static DocumentState FreeDoc(params PixelRect[] bounds)
    {
        var assets = new List<ImageAsset>();
        var layers = new List<ImageLayer>();
        foreach (var b in bounds)
        {
            var a = Asset(b.Width, b.Height);
            assets.Add(a);
            layers.Add(Layer(a, b.X, b.Y) with { Bounds = b });
        }
        return DocumentState.CreateEmpty() with
        {
            Layout = LayoutOptions.Default with { Mode = LayoutMode.Free },
            Assets = assets.ToArray(),
            Images = layers.ToArray(),
            LayoutOrder = layers.Select(l => l.Id).ToArray(),
        };
    }

    public static LayoutOptions Vertical(int gap = 10, int pad = 5, CrossAlignment align = CrossAlignment.Center) =>
        new(LayoutMode.Vertical, gap, pad, align, ScaleMode.Original, null, false);
}
