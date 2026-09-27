using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core;

/// <summary>Typed failure codes from the specification.</summary>
public enum ErrorCode
{
    None, UnsupportedFormat, InvalidImage, ImageTooLarge, InvalidProject, UnsupportedSchema,
    AccessDenied, DiskFull, ClipboardBusy, HotkeyUnavailable, TargetGone, CaptureUnavailable, NotFound, Io,
}

/// <summary>Result with explicit success / canceled / failure alternatives.</summary>
public readonly record struct OpResult<T>(T? Value, ErrorCode Error, string? Message, bool IsCanceled)
{
    public bool IsSuccess => !IsCanceled && Error == ErrorCode.None;
    public static OpResult<T> Ok(T value) => new(value, ErrorCode.None, null, false);
    public static OpResult<T> Fail(ErrorCode e, string message) => new(default, e, message, false);
    public static OpResult<T> Canceled() => new(default, ErrorCode.None, "Canceled", true);
}

/// <summary>Maps IO exceptions to typed error codes.</summary>
public static class ErrorMapping
{
    private const int ErrorDiskFull = unchecked((int)0x80070070), ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static ErrorCode FromException(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ErrorCode.AccessDenied,
        IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => ErrorCode.DiskFull,
        FileNotFoundException or DirectoryNotFoundException => ErrorCode.NotFound,
        IOException => ErrorCode.Io,
        _ => ErrorCode.Io,
    };

    public static string Describe(ErrorCode code) => code switch
    {
        ErrorCode.AccessDenied => "Access was denied.",
        ErrorCode.DiskFull => "The disk is full.",
        ErrorCode.NotFound => "The file was not found.",
        ErrorCode.ClipboardBusy => "Clipboard is busy. Try Copy again.",
        _ => code.ToString(),
    };
}

/// <summary>Content-addressed normalized PNG store.</summary>
public interface IAssetStore
{
    /// <summary>Stores normalized PNG bytes with verified dimensions; returns the asset (deduplicated by SHA-256).</summary>
    Task<ImageAsset> PutAsync(Stream normalizedPng, int width, int height, CancellationToken ct = default);
    /// <summary>Fresh readable stream per call. Throws <see cref="AssetNotFoundException"/> if absent.</summary>
    Task<Stream> OpenReadAsync(string assetId, CancellationToken ct = default);
    bool Contains(string assetId);
    string RootDirectory { get; }
}

public sealed class AssetNotFoundException(string assetId) : Exception($"Image asset {assetId} is missing.")
{
    public string AssetId { get; } = assetId;
}

public sealed record ImportResult(ImageAsset Asset, string Name, IReadOnlyList<string> Diagnostics);

public interface IImageImporter
{
    Task<OpResult<ImportResult>> ImportAsync(Stream input, string name, CancellationToken ct = default);
    Task<OpResult<ImportResult>> ImportFileAsync(string path, CancellationToken ct = default);
}

public enum ExportFormat { Png, Jpeg }

public sealed record ExportOptions(ExportFormat Format = ExportFormat.Png, int JpegQuality = 90, Rgba32? FlattenBackground = null)
{
    public static ExportFormat FormatFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? ExportFormat.Jpeg : ExportFormat.Png;
}

public sealed record ExportResult(string Path, int Width, int Height);

public interface IExportService
{
    Task<OpResult<ExportResult>> ExportAsync(DocumentState doc, string path, ExportOptions options, CancellationToken ct = default);
}

public interface IProjectStore
{
    Task<OpResult<string>> SaveAsync(DocumentState doc, string path, byte[]? previewPng, CancellationToken ct = default);
    /// <summary>Loads and validates; imports referenced assets into the asset store before returning.</summary>
    Task<OpResult<DocumentState>> LoadAsync(string path, CancellationToken ct = default);
}
