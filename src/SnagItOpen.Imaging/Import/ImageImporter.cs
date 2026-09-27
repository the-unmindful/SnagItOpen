using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Imaging.Threading;

namespace SnagItOpen.Imaging.Import;

/// <summary>
/// Decodes PNG/JPEG/BMP/GIF/TIFF, checks dimensions before full decode, applies EXIF orientation once,
/// converts embedded color profiles to sRGB when possible, and stores a normalized PNG asset.
/// </summary>
public sealed class ImageImporter : IImageImporter
{
    public const long MaxInputBytes = 512L * 1024 * 1024;
    private readonly IAssetStore _store;
    private readonly ImagingDispatcher _dispatcher;

    public ImageImporter(IAssetStore store, ImagingDispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
    }

    public async Task<OpResult<ImportResult>> ImportFileAsync(string path, CancellationToken ct = default)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return OpResult<ImportResult>.Fail(ErrorCode.NotFound, $"{Path.GetFileName(path)}: file not found.");
            if (info.Length > MaxInputBytes) return OpResult<ImportResult>.Fail(ErrorCode.ImageTooLarge, $"{info.Name}: file is too large.");
            // Read fully so the input file is released immediately.
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OpResult<ImportResult>.Canceled(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var code = ErrorMapping.FromException(ex);
            return OpResult<ImportResult>.Fail(code, $"{Path.GetFileName(path)}: {ErrorMapping.Describe(code)}");
        }
        return await ImportBytesAsync(bytes, Path.GetFileNameWithoutExtension(path), ct).ConfigureAwait(false);
    }

    public async Task<OpResult<ImportResult>> ImportAsync(Stream input, string name, CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > MaxInputBytes) return OpResult<ImportResult>.Fail(ErrorCode.ImageTooLarge, $"{name}: input is too large.");
                ms.Write(buf, 0, n);
            }
            return await ImportBytesAsync(ms.ToArray(), name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OpResult<ImportResult>.Canceled(); }
    }

    public async Task<OpResult<ImportResult>> ImportBytesAsync(byte[] bytes, string name, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (DetectFormat(bytes) is null)
                return OpResult<ImportResult>.Fail(ErrorCode.UnsupportedFormat, $"{name}: not a supported image (PNG, JPEG, BMP, GIF, TIFF).");
            var decoded = await _dispatcher.InvokeAsync(() => Decode(bytes, name, ct), ct).ConfigureAwait(false);
            if (!decoded.IsSuccess) return OpResult<ImportResult>.Fail(decoded.Error, decoded.Message!);
            var (png, w, h, diags) = decoded.Value;
            ct.ThrowIfCancellationRequested();
            using var ps = new MemoryStream(png, writable: false);
            var asset = await _store.PutAsync(ps, w, h, ct).ConfigureAwait(false);
            return OpResult<ImportResult>.Ok(new ImportResult(asset, name, diags));
        }
        catch (OperationCanceledException) { return OpResult<ImportResult>.Canceled(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var code = ErrorMapping.FromException(ex);
            return OpResult<ImportResult>.Fail(code, $"{name}: could not store image ({ErrorMapping.Describe(code)}).");
        }
    }

    /// <summary>Imports an already-decoded buffer (e.g. a capture) as a normalized asset.</summary>
    public async Task<ImageAsset> ImportPixelsAsync(PixelBuffer pixels, CancellationToken ct = default)
    {
        var png = await _dispatcher.InvokeAsync(pixels.EncodePng, ct).ConfigureAwait(false);
        using var ms = new MemoryStream(png, writable: false);
        return await _store.PutAsync(ms, pixels.Width, pixels.Height, ct).ConfigureAwait(false);
    }

    public static string? DetectFormat(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpeg";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "bmp";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "gif";
        if (b.Length >= 4 && ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A) || (b[0] == 0x4D && b[1] == 0x4D && b[3] == 0x2A))) return "tiff";
        return null;
    }

    private static OpResult<(byte[] Png, int W, int H, IReadOnlyList<string> Diags)> Decode(byte[] bytes, string name, CancellationToken ct)
    {
        var diags = new List<string>();
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            // Header-only pass: DelayCreation + None cache reads dimensions without decoding pixels.
            var probe = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (probe.Frames.Count == 0) return Fail(ErrorCode.InvalidImage, $"{name}: image has no frames.");
            var f0 = probe.Frames[0];
            if (!Limits.IsAcceptableImageSize(f0.PixelWidth, f0.PixelHeight))
                return Fail(ErrorCode.ImageTooLarge, $"{name}: {f0.PixelWidth}×{f0.PixelHeight} exceeds the limit ({Limits.MaxDimension}px per side, {Limits.MaxAssetPixels / 1_000_000} MP).");
            int orientation = ReadOrientation(f0);
            if (probe.Frames.Count > 1) diags.Add("Only the first frame was imported.");
            ct.ThrowIfCancellationRequested();

            ms.Position = 0;
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = dec.Frames[0];
            frame = ConvertProfile(frame, dec.Frames[0].ColorContexts, diags);
            var px = PixelBuffer.FromBitmap(frame);
            if (orientation is >= 2 and <= 8) px = px.Orient(orientation);
            ct.ThrowIfCancellationRequested();
            return OpResult<(byte[], int, int, IReadOnlyList<string>)>.Ok((px.EncodePng(), px.Width, px.Height, diags));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or OverflowException or IOException or System.Runtime.InteropServices.COMException)
        {
            return Fail(ErrorCode.InvalidImage, $"{name}: the image could not be decoded ({ex.GetType().Name}).");
        }
        catch (OutOfMemoryException)
        {
            return Fail(ErrorCode.ImageTooLarge, $"{name}: not enough memory to decode.");
        }
    }

    private static BitmapSource ConvertProfile(BitmapSource frame, System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>? contexts, List<string> diags)
    {
        if (contexts is null || contexts.Count == 0) return frame;
        try
        {
            var converted = new ColorConvertedBitmap(frame, contexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
            converted.Freeze();
            return converted;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            diags.Add("Embedded color profile could not be applied; assumed sRGB.");
            return frame;
        }
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata md) return 1;
            foreach (var q in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                if (md.ContainsQuery(q) && md.GetQuery(q) is { } v)
                    return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or FormatException or InvalidCastException or System.Runtime.InteropServices.COMException) { }
        return 1;
    }

    private static OpResult<(byte[], int, int, IReadOnlyList<string>)> Fail(ErrorCode c, string m) =>
        OpResult<(byte[], int, int, IReadOnlyList<string>)>.Fail(c, m);
}
