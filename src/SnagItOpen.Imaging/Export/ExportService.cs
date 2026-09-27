using System.IO;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;
using SnagItOpen.Imaging.Threading;

namespace SnagItOpen.Imaging.Export;

/// <summary>Conservative memory preflight for flattened output.</summary>
public static class ExportBudget
{
    /// <summary>Returns an error message when the export would exceed limits, otherwise null.</summary>
    public static string? Check(DocumentState doc)
    {
        var a = doc.ExportArea;
        if (!Limits.IsAcceptableExportSize(a.Width, a.Height))
            return $"Output {a.Width}×{a.Height} exceeds the limit ({Limits.MaxDimension}px per side, {Limits.MaxExportPixels / 1_000_000} MP). Crop the canvas or scale the document down.";
        long output = (long)a.Width * a.Height * 4;
        long sources = doc.Images.Where(i => i.Visible).Select(i => i.AssetId).Distinct()
            .Select(id => doc.FindAsset(id)).Where(x => x is not null).Sum(x => (long)x!.Width * x.Height * 4);
        long estimate = output * 3 + sources + Limits.ExportReserveBytes;
        if (estimate > Limits.WorkingSetBytes)
            return $"Output {a.Width}×{a.Height} needs about {estimate / (1024 * 1024)} MiB, above the {Limits.WorkingSetBytes / (1024 * 1024)} MiB working budget.";
        return null;
    }
}

/// <summary>
/// Renders an immutable document snapshot on the imaging dispatcher at 96 DPI to exact pixel
/// dimensions, encodes PNG/JPEG, writes a temporary sibling and replaces the destination only on success.
/// </summary>
public sealed class ExportService : IExportService
{
    private readonly DocumentRenderer _renderer;
    private readonly ImagingDispatcher _dispatcher;

    public ExportService(DocumentRenderer renderer, ImagingDispatcher dispatcher)
    {
        _renderer = renderer;
        _dispatcher = dispatcher;
    }

    public static Rgba32 JpegBackground(DocumentState doc, ExportOptions o) =>
        o.FlattenBackground ?? (doc.Background.A == 255 ? doc.Background : Rgba32.White);

    public async Task<OpResult<ExportResult>> ExportAsync(DocumentState doc, string path, ExportOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!doc.HasVisibleContent) return OpResult<ExportResult>.Fail(ErrorCode.InvalidImage, "There is nothing to export.");
        if (ExportBudget.Check(doc) is { } msg) return OpResult<ExportResult>.Fail(ErrorCode.ImageTooLarge, msg);
        byte[] bytes;
        try
        {
            bytes = await _dispatcher.InvokeAsync(() => Encode(doc, options, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OpResult<ExportResult>.Canceled(); }
        catch (OutOfMemoryException) { return OpResult<ExportResult>.Fail(ErrorCode.ImageTooLarge, "Not enough memory to render this output. Try a smaller canvas."); }

        try
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(path);
            await WriteAtomicAsync(full, bytes, ct).ConfigureAwait(false);
            return OpResult<ExportResult>.Ok(new ExportResult(full, doc.ExportArea.Width, doc.ExportArea.Height));
        }
        catch (OperationCanceledException) { return OpResult<ExportResult>.Canceled(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var code = ErrorMapping.FromException(ex);
            return OpResult<ExportResult>.Fail(code, $"Could not write {Path.GetFileName(path)}: {ErrorMapping.Describe(code)}");
        }
    }

    /// <summary>Encodes the flattened document. Must run on a dispatcher thread.</summary>
    public byte[] Encode(DocumentState doc, ExportOptions o, CancellationToken ct = default)
    {
        var px = _renderer.RenderToPixels(doc);
        ct.ThrowIfCancellationRequested();
        if (o.Format == ExportFormat.Jpeg) return px.EncodeJpeg(o.JpegQuality, JpegBackground(doc, o));
        if (o.FlattenBackground is { } bg) px = px.FlattenOnto(bg with { A = 255 });
        return px.EncodePng();
    }

    /// <summary>Flattened pixels for clipboard/pin/OCR, rendered on the imaging thread.</summary>
    public Task<PixelBuffer> RenderAsync(DocumentState doc, CancellationToken ct = default)
    {
        if (ExportBudget.Check(doc) is { } msg) return Task.FromException<PixelBuffer>(new InvalidOperationException(msg));
        return _dispatcher.InvokeAsync(() => _renderer.RenderToPixels(doc), ct);
    }

    public Task<byte[]> EncodeAsync(DocumentState doc, ExportOptions o, CancellationToken ct = default) =>
        _dispatcher.InvokeAsync(() => Encode(doc, o, ct), ct);

    private static async Task WriteAtomicAsync(string full, byte[] bytes, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
