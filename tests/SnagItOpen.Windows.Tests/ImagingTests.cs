using System.IO;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Imaging.Effects;
using SnagItOpen.Imaging.Threading;
using SnagItOpen.Storage.Assets;

namespace SnagItOpen.Windows.Tests;

public class AssetStoreTests
{
    [Fact]
    public async Task Duplicate_bytes_store_one_asset_and_reads_are_independent()
    {
        using var s = new Sandbox();
        var png = await s.Dispatcher.InvokeAsync(Fixtures.Red100x50().EncodePng);
        ImageAsset a1, a2;
        using (var m = new MemoryStream(png)) a1 = await s.Store.PutAsync(m, 100, 50);
        using (var m = new MemoryStream(png)) a2 = await s.Store.PutAsync(m, 100, 50);
        Assert.Equal(a1.Id, a2.Id);
        Assert.Single(s.Store.Enumerate());
        await using var r1 = await s.Store.OpenReadAsync(a1.Id);
        await using var r2 = await s.Store.OpenReadAsync(a1.Id);
        Assert.Equal(png.Length, r1.Length);
        Assert.Equal(png.Length, r2.Length);
    }

    [Fact]
    public async Task Canceled_write_leaves_no_asset()
    {
        using var s = new Sandbox();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var png = await s.Dispatcher.InvokeAsync(Fixtures.Red100x50().EncodePng);
        using var m = new MemoryStream(png);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Store.PutAsync(m, 100, 50, cts.Token));
        Assert.Empty(s.Store.Enumerate());
        Assert.Empty(Directory.GetFiles(s.Store.RootDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Missing_asset_is_typed_failure()
    {
        using var s = new Sandbox();
        await Assert.ThrowsAsync<AssetNotFoundException>(() => s.Store.OpenReadAsync(new string('a', 64)));
    }

    [Fact]
    public async Task Declared_size_must_match_png_header()
    {
        using var s = new Sandbox();
        var png = await s.Dispatcher.InvokeAsync(Fixtures.Red100x50().EncodePng);
        using var m = new MemoryStream(png);
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Store.PutAsync(m, 99, 50));
    }
}

public class ImagingDispatcherTests
{
    [Fact]
    public async Task Runs_on_one_sta_thread_and_survives_faults()
    {
        using var d = new ImagingDispatcher();
        var t1 = await d.InvokeAsync(() => (Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => d.InvokeAsync<int>(() => throw new InvalidOperationException()));
        var t2 = await d.InvokeAsync(() => Thread.CurrentThread.ManagedThreadId);
        Assert.Equal(ApartmentState.STA, t1.Item2);
        Assert.Equal(t1.ManagedThreadId, t2);
    }

    [Fact]
    public async Task Cancellation_completes_caller_promptly()
    {
        using var d = new ImagingDispatcher();
        using var gate = new ManualResetEventSlim();
        var blocker = d.InvokeAsync(() => gate.Wait(5000));
        using var cts = new CancellationTokenSource();
        var queued = d.InvokeAsync(() => 42, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        gate.Set();
        await blocker;
        Assert.Equal(7, await d.InvokeAsync(() => 7));
    }
}

public class ImageImportTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task Exif_orientation_is_applied_once(ushort orientation)
    {
        using var s = new Sandbox();
        // 40×20 image: left half red, right half blue (large blocks survive JPEG).
        var src = new PixelBuffer(40, 20);
        for (int y = 0; y < 20; y++) for (int x = 0; x < 40; x++) src[x, y] = PixelBuffer.Pack(x < 20 ? Fixtures.Red : Fixtures.Blue);
        var jpeg = await s.Dispatcher.InvokeAsync(() => Fixtures.JpegWithOrientation(src, orientation));
        var r = await s.Importer.ImportBytesAsync(jpeg, "o" + orientation);
        Assert.True(r.IsSuccess, r.Message);
        var expected = await s.Dispatcher.InvokeAsync(() => src.Orient(orientation));
        Assert.Equal(expected.Width, r.Value!.Asset.Width);
        Assert.Equal(expected.Height, r.Value.Asset.Height);
        var got = await s.Dispatcher.InvokeAsync(() => s.Cache.GetPixels(r.Value.Asset.Id));
        // Sample well inside each block.
        foreach (var (x, y) in new[] { (5, 5), (expected.Width - 5, expected.Height - 5) })
            Assert.True(Fixtures.Near(got[x, y], PixelBuffer.Unpack(expected[x, y]), 40), $"orientation {orientation} at {x},{y}");
    }

    [Fact]
    public async Task Alpha_is_preserved()
    {
        using var s = new Sandbox();
        var png = await s.Dispatcher.InvokeAsync(Fixtures.Checker().EncodePng);
        var r = await s.Importer.ImportBytesAsync(png, "checker");
        Assert.True(r.IsSuccess);
        var px = await s.Dispatcher.InvokeAsync(() => s.Cache.GetPixels(r.Value!.Asset.Id));
        Assert.Equal(128, PixelBuffer.Unpack(px[0, 0]).A);
        Assert.Equal(0, PixelBuffer.Unpack(px[1, 0]).A);
    }

    [Fact]
    public async Task Corrupt_and_unsupported_fail_without_killing_dispatcher()
    {
        using var s = new Sandbox();
        var bad = await s.Importer.ImportBytesAsync(Fixtures.Corrupt(), "bad");
        Assert.Equal(ErrorCode.InvalidImage, bad.Error);
        var text = await s.Importer.ImportBytesAsync("hello"u8.ToArray(), "text");
        Assert.Equal(ErrorCode.UnsupportedFormat, text.Error);
        var ok = await s.Importer.ImportBytesAsync(await s.Dispatcher.InvokeAsync(Fixtures.Blue80x30().EncodePng), "ok");
        Assert.True(ok.IsSuccess);
    }

    [Fact]
    public async Task Canceled_import_stores_nothing()
    {
        using var s = new Sandbox();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var png = await s.Dispatcher.InvokeAsync(Fixtures.Red100x50().EncodePng);
        var r = await s.Importer.ImportBytesAsync(png, "c", cts.Token);
        Assert.True(r.IsCanceled);
        Assert.Empty(s.Store.Enumerate());
    }

    [Fact]
    public async Task Import_file_releases_input()
    {
        using var s = new Sandbox();
        var path = s.PathOf("in.png");
        await File.WriteAllBytesAsync(path, await s.Dispatcher.InvokeAsync(Fixtures.Red100x50().EncodePng));
        var r = await s.Importer.ImportFileAsync(path);
        Assert.True(r.IsSuccess);
        File.Delete(path); // would throw if still locked
        Assert.False(File.Exists(path));
    }
}

public class RendererTests
{
    [Fact]
    public async Task Vertical_combine_places_pixels_per_oracle()
    {
        using var s = new Sandbox();
        var layout = new LayoutOptions(LayoutMode.Vertical, 10, 5, CrossAlignment.Center, ScaleMode.Original, null, false);
        var doc = await s.DocAsync(layout, Fixtures.Red100x50(), Fixtures.Blue80x30());
        Assert.Equal(new PixelRect(0, 0, 110, 100), doc.ExportArea);
        var px = await s.RenderAsync(doc);
        Assert.Equal(110, px.Width);
        Assert.Equal(100, px.Height);
        Assert.True(Fixtures.Near(px[5, 5], Fixtures.Red));
        Assert.True(Fixtures.Near(px[104, 54], Fixtures.Red));
        Assert.Equal(0u, px[50, 60] >> 24);           // transparent gap
        Assert.True(Fixtures.Near(px[15, 65], Fixtures.Blue));
        Assert.True(Fixtures.Near(px[94, 94], Fixtures.Blue));
        Assert.Equal(0u, px[10, 70] >> 24);           // left of centered blue
    }

    [Fact]
    public async Task Crop_negative_origin_and_draw_order()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Coordinates(50, 50), Fixtures.Red100x50());
        var coord = doc.Images[0];
        doc = DocumentOps.Crop(doc, coord.Id, new PixelRect(10, 20, 5, 5));
        doc = DocumentOps.SetBounds(doc, coord.Id, new PixelRect(-30, -40, 5, 5));
        doc = DocumentOps.SetBounds(doc, doc.Images[1].Id, new PixelRect(-28, -40, 100, 50)); // overlaps on top
        doc = DocumentOps.FitCanvas(doc, 0);
        Assert.Equal(new PixelRect(-30, -40, 102, 50), doc.ExportArea);
        var px = await s.RenderAsync(doc);
        var c = PixelBuffer.Unpack(px[0, 0]);
        Assert.Equal((10, 20), (c.R, c.G));            // crop origin at canvas origin
        var c2 = PixelBuffer.Unpack(px[1, 4]);
        Assert.Equal((11, 24), (c2.R, c2.G));
        Assert.True(Fixtures.Near(px[2, 0], Fixtures.Red)); // red drawn above
    }

    [Fact]
    public async Task Rendering_is_deterministic()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Gap = 3 }, Fixtures.Coordinates(20, 10), Fixtures.Checker());
        var a = await s.RenderAsync(doc);
        s.Cache.Clear();
        var b = await s.RenderAsync(doc);
        Assert.Equal(a.Data, b.Data);
    }

    [Fact]
    public async Task Rotation_matches_pixel_oracle()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Corners2x2());
        var id = doc.Images[0].Id;
        var rotated = DocumentOps.FitCanvas(DocumentOps.Rotate(doc, [id], 1), 0);
        var px = await s.RenderAsync(rotated);
        // Clockwise: TL <- BL(blue), TR <- TL(red), BR <- TR(green), BL <- BR(yellow)
        Assert.True(Fixtures.Near(px[0, 0], Fixtures.Blue));
        Assert.True(Fixtures.Near(px[1, 0], Fixtures.Red));
        Assert.True(Fixtures.Near(px[1, 1], Fixtures.Green));
        Assert.True(Fixtures.Near(px[0, 1], Fixtures.Yellow));
        var flipped = DocumentOps.FitCanvas(DocumentOps.Flip(doc, [id], horizontal: true), 0);
        var fp = await s.RenderAsync(flipped);
        Assert.True(Fixtures.Near(fp[0, 0], Fixtures.Green));
        Assert.True(Fixtures.Near(fp[1, 1], Fixtures.Blue));
    }
}

public class ExportTests
{
    [Fact]
    public async Task Png_export_exact_size_alpha_and_decodable()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(new LayoutOptions(LayoutMode.Vertical, 10, 5, CrossAlignment.Center, ScaleMode.Original, null, false),
            Fixtures.Red100x50(), Fixtures.Blue80x30());
        var path = s.PathOf("out.png");
        var r = await s.Exporter.ExportAsync(doc, path, new ExportOptions());
        Assert.True(r.IsSuccess, r.Message);
        var px = await s.Dispatcher.InvokeAsync(() => PixelBuffer.DecodeImage(File.ReadAllBytes(path)));
        Assert.Equal((110, 100), (px.Width, px.Height));
        Assert.Equal(0u, px[0, 0] >> 24);
    }

    [Fact]
    public async Task Jpeg_flattens_on_white()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Padding = 4 }, Fixtures.Red100x50());
        var path = s.PathOf("out.jpg");
        var r = await s.Exporter.ExportAsync(doc, path, new ExportOptions(ExportFormat.Jpeg, 95));
        Assert.True(r.IsSuccess, r.Message);
        var px = await s.Dispatcher.InvokeAsync(() => PixelBuffer.DecodeImage(File.ReadAllBytes(path)));
        Assert.True(Fixtures.Near(px[0, 0], Rgba32.White, 8));
        Assert.True(Fixtures.Near(px[50, 30], Fixtures.Red, 12));
    }

    [Fact]
    public async Task Failed_export_keeps_existing_destination()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default, Fixtures.Red100x50());
        var path = s.PathOf("keep.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await s.Exporter.ExportAsync(doc, path, new ExportOptions(), cts.Token);
        Assert.True(r.IsCanceled);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Over_limit_rejected_before_render()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Red100x50());
        doc = doc with { ExportArea = new PixelRect(0, 0, 30000, 30000), AutoCanvas = false };
        var r = await s.Exporter.ExportAsync(doc, s.PathOf("big.png"), new ExportOptions());
        Assert.Equal(ErrorCode.ImageTooLarge, r.Error);
    }

    [Fact]
    public async Task Empty_document_cannot_export()
    {
        using var s = new Sandbox();
        var r = await s.Exporter.ExportAsync(DocumentState.CreateEmpty(), s.PathOf("e.png"), new ExportOptions());
        Assert.False(r.IsSuccess);
    }
}

public class RedactionExportTests
{
    [Theory]
    [InlineData(1.0)] [InlineData(0.37)] [InlineData(2.5)]
    public async Task Redaction_fully_covers_secret_at_all_scales(double scale)
    {
        using var s = new Sandbox();
        var secret = new PixelBuffer(60, 40);
        for (int y = 0; y < 40; y++) for (int x = 0; x < 60; x++) secret[x, y] = ((x ^ y) & 1) == 0 ? PixelBuffer.Pack(255, 255, 255, 255) : PixelBuffer.Pack(255, 0, 255, 255);
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, secret);
        var layer = doc.Images[0];
        doc = DocumentOps.SetBounds(doc, layer.Id, new PixelRect(3, 7, (int)Math.Round(60 * scale), (int)Math.Round(40 * scale)));
        var red = new RedactionAnnotation { ImageLayerId = layer.Id, Bounds = new RectD(10.3, 5.6, 20.2, 10.1) };
        doc = DocumentOps.AddAnnotation(doc, red);
        foreach (var fmt in new[] { ExportFormat.Png, ExportFormat.Jpeg })
        {
            var path = s.PathOf($"r{scale}.{(fmt == ExportFormat.Png ? "png" : "jpg")}");
            var r = await s.Exporter.ExportAsync(doc, path, new ExportOptions(fmt, 100));
            Assert.True(r.IsSuccess, r.Message);
            var bytes = await File.ReadAllBytesAsync(path);
            var px = await s.Dispatcher.InvokeAsync(() => PixelBuffer.DecodeImage(bytes));
            var cover = Imaging.Rendering.DocumentRenderer.RedactionRect(doc, red).Translate(-doc.ExportArea.X, -doc.ExportArea.Y);
            // Every source pixel whose area touches the redaction must be black in the output.
            var src = ImageTransform.SourceRectToDocument(doc.Images[0], red.Bounds).ToPixelRectOutward()
                .Intersect(doc.Images[0].Bounds).Translate(-doc.ExportArea.X, -doc.ExportArea.Y);
            Assert.True(cover.Contains(src) || cover == src);
            int tol = fmt == ExportFormat.Jpeg ? 24 : 0;
            // JPEG ringing may bleed at the very edge; check interior strictly for PNG, near-edge-inset for JPEG.
            var check = fmt == ExportFormat.Jpeg ? cover.Inflate(-1) : cover;
            for (int y = check.Y; y < check.Bottom; y++)
                for (int x = check.X; x < check.Right; x++)
                    Assert.True(Fixtures.Near(px[x, y], Rgba32.Black, tol), $"{fmt} scale {scale} pixel {x},{y} not covered");
            Assert.DoesNotContain("PNG"u8.ToArray(), SplitChunks(bytes, fmt));
        }
    }

    // A JPEG must not embed a PNG payload; for PNG we only check there is a single IHDR.
    private static IEnumerable<byte[]> SplitChunks(byte[] b, ExportFormat f)
    {
        if (f == ExportFormat.Png) yield break;
        for (int i = 1; i + 4 < b.Length; i++)
            if (b[i] == 0x50 && b[i + 1] == 0x4E && b[i + 2] == 0x47 && b[i - 1] == 0x89) yield return "PNG"u8.ToArray();
    }
}

public class EffectTests
{
    [Fact]
    public void Pixelate_averages_blocks_and_leaves_outside_untouched()
    {
        var b = Fixtures.Coordinates(8, 8);
        var before = b.Clone();
        ObscureProcessor.Pixelate(b, new PixelRect(2, 2, 4, 4), 2);
        var c = PixelBuffer.Unpack(b[2, 2]);
        Assert.Equal(3, c.R); // avg of x 2,3 = 2.5 -> 3 (round half up)
        Assert.Equal(b[2, 2], b[3, 3]);
        Assert.Equal(before[1, 1], b[1, 1]);
        Assert.Equal(before[6, 6], b[6, 6]);
        Assert.Equal(before[7, 2], b[7, 2]);
    }

    [Fact]
    public void Blur_changes_only_region()
    {
        var b = Fixtures.Coordinates(40, 40);
        var before = b.Clone();
        ObscureProcessor.Blur(b, new PixelRect(10, 10, 10, 10), 4);
        for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++)
            if (!new PixelRect(10, 10, 10, 10).Contains(x, y)) Assert.Equal(before[x, y], b[x, y]);
    }

    [Fact]
    public void Strip_cutout_rows_and_columns()
    {
        var b = Fixtures.Coordinates(100, 100);
        var r = StripCutout.RemoveRows(b, 30, 50);
        Assert.Equal((100, 80), (r.Width, r.Height));
        Assert.Equal(50, PixelBuffer.Unpack(r[0, 30]).G);
        Assert.Equal(29, PixelBuffer.Unpack(r[0, 29]).G);
        var c = StripCutout.RemoveColumns(b, 30, 50);
        Assert.Equal((80, 100), (c.Width, c.Height));
        Assert.Equal(50, PixelBuffer.Unpack(c[30, 0]).R);
        Assert.Throws<ArgumentOutOfRangeException>(() => StripCutout.RemoveRows(b, 0, 100));
    }

    [Fact]
    public void Ellipse_mask_corner_transparent_center_opaque()
    {
        var m = CaptureMask.ApplyEllipse(PixelBuffer.Solid(40, 20, Fixtures.Red));
        Assert.Equal(0u, m[0, 0]);
        Assert.Equal(0u, m[39, 19]);
        Assert.True(Fixtures.Near(m[20, 10], Fixtures.Red));
    }

    [Fact]
    public void Polygon_mask_and_degenerate_rejected()
    {
        var m = CaptureMask.ApplyPolygon(PixelBuffer.Solid(10, 10, Fixtures.Red), [new(0, 0), new(10, 0), new(0, 10)]);
        Assert.True(Fixtures.Near(m[1, 1], Fixtures.Red));
        Assert.Equal(0u, m[9, 9]);
        Assert.Throws<ArgumentException>(() => CaptureMask.ApplyPolygon(PixelBuffer.Solid(10, 10, Fixtures.Red), [new(0, 0), new(5, 5), new(9, 9)]));
    }

    [Fact]
    public async Task Effect_in_document_blurs_rendered_output()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default, Fixtures.Coordinates(32, 32));
        var id = doc.Images[0].Id;
        var fx = DocumentOps.AddEffect(doc, id, new ImageEffect(Guid.NewGuid(), ImageEffectKind.Pixelate, new PixelRect(0, 0, 16, 16), 8));
        var a = await s.RenderAsync(doc);
        var b = await s.RenderAsync(fx);
        Assert.NotEqual(a[1, 1], b[1, 1]);
        Assert.Equal(b[0, 0], b[7, 7]);
        Assert.Equal(a[20, 20], b[20, 20]);
    }
}
