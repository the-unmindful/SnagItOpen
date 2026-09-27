using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.Core;

namespace SnagItOpen.Windows.Clipboard;

/// <summary>What the clipboard holds that the editor can use.</summary>
public sealed record ClipboardContent(byte[]? ImageBytes, IReadOnlyList<string> ImageFiles, bool HasOtherData)
{
    public bool HasImage => ImageBytes is { Length: > 0 } || ImageFiles.Count > 0;
}

/// <summary>Raw clipboard access, replaceable by a fake in tests. Methods may throw when the clipboard is busy.</summary>
public interface IClipboardAdapter
{
    void SetImage(BitmapSource opaqueBitmap, byte[] png);
    ClipboardContent Read();
}

/// <summary>Bounded retry for clipboard contention: at most N attempts within a total time budget.</summary>
public static class ClipboardRetry
{
    public const int DefaultAttempts = 5;
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(1);

    public static bool IsBusy(Exception ex) => ex is ExternalException;

    public static async Task<OpResult<T>> RunAsync<T>(Func<T> op, CancellationToken ct = default,
        int maxAttempts = DefaultAttempts, TimeSpan? budget = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var total = budget ?? DefaultBudget;
        var wait = TimeSpan.FromTicks(Math.Max(1, total.Ticks / Math.Max(1, maxAttempts)));
        for (int attempt = 1; ; attempt++)
        {
            if (ct.IsCancellationRequested) return OpResult<T>.Canceled();
            try
            {
                return OpResult<T>.Ok(op());
            }
            catch (Exception ex) when (IsBusy(ex))
            {
                if (attempt >= maxAttempts) return OpResult<T>.Fail(ErrorCode.ClipboardBusy, ErrorMapping.Describe(ErrorCode.ClipboardBusy));
            }
            try { await delay(wait, ct); }
            catch (OperationCanceledException) { return OpResult<T>.Canceled(); }
        }
    }
}

/// <summary>
/// Copy/paste of images. Must be called on an STA (UI) thread. Copy publishes both a bitmap (flattened,
/// for apps without alpha support) and "PNG" data with transparency; receiving apps choose.
/// </summary>
public sealed class ClipboardImageService
{
    private readonly IClipboardAdapter _adapter;

    public ClipboardImageService(IClipboardAdapter? adapter = null) => _adapter = adapter ?? new WpfClipboardAdapter();

    public Task<OpResult<bool>> CopyImageAsync(BitmapSource opaqueBitmap, byte[] png, CancellationToken ct = default) =>
        ClipboardRetry.RunAsync(() => { _adapter.SetImage(opaqueBitmap, png); return true; }, ct);

    public Task<OpResult<ClipboardContent>> ReadAsync(CancellationToken ct = default) =>
        ClipboardRetry.RunAsync(_adapter.Read, ct);
}

/// <summary>Real clipboard via WPF.</summary>
public sealed class WpfClipboardAdapter : IClipboardAdapter
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public void SetImage(BitmapSource opaqueBitmap, byte[] png)
    {
        var data = new DataObject();
        data.SetImage(opaqueBitmap);
        data.SetData("PNG", new MemoryStream(png), autoConvert: false);
        System.Windows.Clipboard.SetDataObject(data, copy: true);
    }

    public ClipboardContent Read()
    {
        var dataObj = System.Windows.Clipboard.GetDataObject();
        if (dataObj is null) return new ClipboardContent(null, [], false);

        // Prefer image data over file lists.
        if (dataObj.GetDataPresent("PNG") && dataObj.GetData("PNG") is MemoryStream pngStream)
            return new ClipboardContent(pngStream.ToArray(), [], false);
        if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } bmp)
            return new ClipboardContent(EncodeDib(bmp), [], false);
        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            StringCollection files = System.Windows.Clipboard.GetFileDropList();
            var images = files.Cast<string>()
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
            return new ClipboardContent(null, images, images.Count < files.Count);
        }
        return new ClipboardContent(null, [], dataObj.GetFormats().Length > 0);
    }

    /// <summary>DIB clipboard images often carry alpha 0 everywhere; treat those as opaque.</summary>
    private static byte[] EncodeDib(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
