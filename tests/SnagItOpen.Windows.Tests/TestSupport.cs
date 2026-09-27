using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Imaging.Export;
using SnagItOpen.Imaging.Import;
using SnagItOpen.Imaging.Rendering;
using SnagItOpen.Imaging.Threading;
using SnagItOpen.Storage.Assets;

namespace SnagItOpen.Windows.Tests;

/// <summary>Per-test sandbox: temp folder, asset store, STA dispatcher, importer, renderer, exporter.</summary>
public sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "sio-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Store = new FileAssetStore(Path.Combine(Root, "assets"));
        Dispatcher = new ImagingDispatcher("test imaging");
        Importer = new ImageImporter(Store, Dispatcher);
        Cache = new BitmapAssetCache(Store);
        Renderer = new DocumentRenderer(Cache);
        Exporter = new ExportService(Renderer, Dispatcher);
    }

    public string Root { get; }
    public FileAssetStore Store { get; }
    public ImagingDispatcher Dispatcher { get; }
    public ImageImporter Importer { get; }
    public BitmapAssetCache Cache { get; }
    public DocumentRenderer Renderer { get; }
    public ExportService Exporter { get; }

    public string PathOf(string name) => Path.Combine(Root, name);

    public async Task<ImageAsset> AddAsync(PixelBuffer px) => await Importer.ImportPixelsAsync(px);

    /// <summary>Builds a document from pixel buffers using the given layout.</summary>
    public async Task<DocumentState> DocAsync(LayoutOptions layout, params PixelBuffer[] images)
    {
        var items = new List<(ImageAsset, ImageLayer)>();
        foreach (var px in images)
        {
            var a = await AddAsync(px);
            items.Add((a, ImageLayer.ForAsset(a)));
        }
        return DocumentOps.AddImages(DocumentState.CreateEmpty() with { Layout = layout }, items);
    }

    public Task<PixelBuffer> RenderAsync(DocumentState doc) => Dispatcher.InvokeAsync(() => Renderer.RenderToPixels(doc));

    public void Dispose()
    {
        Dispatcher.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Generated fixtures with documented dimensions.</summary>
public static class Fixtures
{
    public static readonly Rgba32 Red = new(255, 0, 0, 255);
    public static readonly Rgba32 Blue = new(0, 0, 255, 255);
    public static readonly Rgba32 Green = new(0, 255, 0, 255);
    public static readonly Rgba32 Yellow = new(255, 255, 0, 255);

    /// <summary>Red 100×50.</summary>
    public static PixelBuffer Red100x50() => PixelBuffer.Solid(100, 50, Red);
    /// <summary>Blue 80×30.</summary>
    public static PixelBuffer Blue80x30() => PixelBuffer.Solid(80, 30, Blue);

    /// <summary>2×2 with distinct corners: TL red, TR green, BL blue, BR yellow.</summary>
    public static PixelBuffer Corners2x2()
    {
        var b = new PixelBuffer(2, 2);
        b[0, 0] = PixelBuffer.Pack(Red); b[1, 0] = PixelBuffer.Pack(Green);
        b[0, 1] = PixelBuffer.Pack(Blue); b[1, 1] = PixelBuffer.Pack(Yellow);
        return b;
    }

    /// <summary>Every pixel encodes its coordinate: R = x, G = y (0..255), opaque.</summary>
    public static PixelBuffer Coordinates(int w, int h)
    {
        var b = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) b[x, y] = PixelBuffer.Pack((byte)x, (byte)y, 128, 255);
        return b;
    }

    /// <summary>Transparent/opaque checker 8×8 with 50% alpha squares.</summary>
    public static PixelBuffer Checker()
    {
        var b = new PixelBuffer(8, 8);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            b[x, y] = ((x + y) & 1) == 0 ? PixelBuffer.Pack(10, 200, 30, 128) : 0u;
        return b;
    }

    /// <summary>Encodes a JPEG with the given EXIF orientation tag.</summary>
    public static byte[] JpegWithOrientation(PixelBuffer px, ushort orientation)
    {
        var md = new BitmapMetadata("jpg");
        md.SetQuery("/app1/ifd/{ushort=274}", orientation);
        var bmp = new FormatConvertedBitmap(px.ToBitmap(), PixelFormats.Bgr24, null, 0);
        var enc = new JpegBitmapEncoder { QualityLevel = 100 };
        enc.Frames.Add(BitmapFrame.Create(bmp, null, md, null));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] Corrupt() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];

    public static bool Near(uint a, Rgba32 b, int tol = 0)
    {
        var c = PixelBuffer.Unpack(a);
        return Math.Abs(c.R - b.R) <= tol && Math.Abs(c.G - b.G) <= tol && Math.Abs(c.B - b.B) <= tol && Math.Abs(c.A - b.A) <= tol;
    }
}
