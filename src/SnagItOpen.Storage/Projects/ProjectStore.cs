using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Storage.Assets;

namespace SnagItOpen.Storage.Projects;

/// <summary>Top-level manifest.json of an editable .sio project.</summary>
public sealed record ProjectManifest
{
    public const string FormatName = "snagitopen-project";
    public string Format { get; init; } = FormatName;
    public int SchemaVersion { get; init; } = DocumentState.CurrentSchemaVersion;
    public string? App { get; init; } = "SnagItOpen";
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
    public DocumentState Document { get; init; } = DocumentState.CreateEmpty();
}

/// <summary>
/// .sio ZIP: manifest.json + assets/&lt;sha256&gt;.png + optional preview.png. Saves atomically; loads
/// by streaming only allowed entries under strict size budgets and validating before returning.
/// </summary>
public sealed class ProjectStore : IProjectStore
{
    public const string Extension = ".sio";
    public const int MaxEntries = Limits.MaxAssets + 2; // 100 assets + manifest + preview
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxTotalBytes = 1024L * 1024 * 1024;
    public const long MaxManifestBytes = 16L * 1024 * 1024;
    private const string ManifestName = "manifest.json", PreviewName = "preview.png", AssetPrefix = "assets/";

    private readonly IAssetStore _assets;

    public ProjectStore(IAssetStore assets) => _assets = assets;

    public async Task<OpResult<string>> SaveAsync(DocumentState doc, string path, byte[]? previewPng, CancellationToken ct = default)
    {
        try
        {
            DocumentValidator.EnsureValid(doc);
            var manifest = new ProjectManifest { Document = doc, SchemaVersion = doc.SchemaVersion };
            await AtomicFile.WriteAsync(path, async (stream, c) =>
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                var me = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                await using (var ms = me.Open())
                    await JsonSerializer.SerializeAsync(ms, manifest, Json.Options, c).ConfigureAwait(false);
                foreach (var asset in doc.Assets)
                {
                    c.ThrowIfCancellationRequested();
                    // PNG data is already compressed.
                    var e = zip.CreateEntry(AssetPrefix + asset.Id + ".png", CompressionLevel.NoCompression);
                    await using var src = await _assets.OpenReadAsync(asset.Id, c).ConfigureAwait(false);
                    await using var dst = e.Open();
                    await src.CopyToAsync(dst, c).ConfigureAwait(false);
                }
                if (previewPng is { Length: > 0 })
                {
                    var pe = zip.CreateEntry(PreviewName, CompressionLevel.NoCompression);
                    await using var ps = pe.Open();
                    await ps.WriteAsync(previewPng, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
            return OpResult<string>.Ok(Path.GetFullPath(path));
        }
        catch (OperationCanceledException) { return OpResult<string>.Canceled(); }
        catch (InvalidDocumentException ex) { return OpResult<string>.Fail(ErrorCode.InvalidProject, ex.Message); }
        catch (AssetNotFoundException ex) { return OpResult<string>.Fail(ErrorCode.NotFound, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var code = ErrorMapping.FromException(ex);
            return OpResult<string>.Fail(code, $"Could not save project: {ErrorMapping.Describe(code)} ({ex.Message})");
        }
    }

    public async Task<OpResult<DocumentState>> LoadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) return Invalid($"Project has too many entries ({zip.Entries.Count}).");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? manifestEntry = null;
            var assetEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                var name = e.FullName;
                if (!names.Add(name)) return Invalid($"Duplicate entry '{name}'.");
                if (name == ManifestName) manifestEntry = e;
                else if (name == PreviewName) { }
                else if (name.StartsWith(AssetPrefix, StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal)
                         && DocumentValidator.IsHash(name[AssetPrefix.Length..^4]))
                    assetEntries[name[AssetPrefix.Length..^4]] = e;
                else return Invalid($"Unexpected entry '{name}'.");
                if (e.Length > MaxEntryBytes) return Invalid($"Entry '{name}' is too large.");
            }
            if (manifestEntry is null) return Invalid("manifest.json is missing.");
            if (manifestEntry.Length > MaxManifestBytes) return Invalid("Manifest is too large.");

            var budget = new ByteBudget(MaxTotalBytes);
            byte[] manifestBytes;
            await using (var ms = manifestEntry.Open())
                manifestBytes = await ReadLimitedAsync(ms, MaxManifestBytes, budget, ct).ConfigureAwait(false);
            // Tolerate a UTF-8 byte order mark (e.g. manifests edited in Notepad).
            if (manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF && manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF)
                manifestBytes = manifestBytes[3..];

            // Check format and schema before binding to the model so newer files fail clearly.
            using (var jd = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 64 }))
            {
                var root = jd.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Invalid("Manifest is not an object.");
                if (!root.TryGetProperty("format", out var f) || f.GetString() != ProjectManifest.FormatName)
                    return Invalid("Not a SnagItOpen project.");
                int schema = root.TryGetProperty("schemaVersion", out var sv) && sv.TryGetInt32(out var v) ? v : 0;
                if (schema > DocumentState.CurrentSchemaVersion)
                    return OpResult<DocumentState>.Fail(ErrorCode.UnsupportedSchema,
                        $"This project was saved by a newer version (schema {schema}). This version supports schema {DocumentState.CurrentSchemaVersion}.");
                if (schema < 1) return Invalid("Missing schema version.");
            }

            ProjectManifest? manifest;
            try { manifest = JsonSerializer.Deserialize<ProjectManifest>(manifestBytes, Json.Options); }
            catch (JsonException ex) when (ex.Message.Contains("discriminator", StringComparison.OrdinalIgnoreCase))
            { return OpResult<DocumentState>.Fail(ErrorCode.UnsupportedSchema, "Project contains an unsupported annotation type."); }
            catch (JsonException ex) { return Invalid($"Manifest is malformed: {ex.Message}"); }
            catch (NotSupportedException ex) { return Invalid($"Manifest is malformed: {ex.Message}"); }
            if (manifest?.Document is null) return Invalid("Manifest has no document.");

            var doc = Migrate(manifest.Document);
            var errors = DocumentValidator.Validate(doc);
            if (errors.Count > 0) return Invalid(string.Join(" ", errors.Take(5)));

            // Verify and stage every referenced asset.
            foreach (var asset in doc.Assets)
            {
                ct.ThrowIfCancellationRequested();
                if (!assetEntries.TryGetValue(asset.Id, out var entry))
                {
                    if (_assets.Contains(asset.Id)) continue; // tolerated: already known locally
                    return Invalid($"Image asset {asset.Id[..8]} is missing from the project.");
                }
                byte[] data;
                await using (var s = entry.Open())
                    data = await ReadLimitedAsync(s, MaxEntryBytes, budget, ct).ConfigureAwait(false);
                if (Convert.ToHexStringLower(SHA256.HashData(data)) != asset.Id)
                    return Invalid($"Image asset {asset.Id[..8]} is corrupt (hash mismatch).");
                if (!PngInfo.TryReadSize(data, out var w, out var h) || w != asset.Width || h != asset.Height)
                    return Invalid($"Image asset {asset.Id[..8]} dimensions do not match the manifest.");
                if (!_assets.Contains(asset.Id))
                {
                    using var ms2 = new MemoryStream(data, writable: false);
                    await _assets.PutAsync(ms2, w, h, ct).ConfigureAwait(false);
                }
            }
            return OpResult<DocumentState>.Ok(doc);
        }
        catch (OperationCanceledException) { return OpResult<DocumentState>.Canceled(); }
        catch (InvalidDataException ex) { return Invalid($"Project file is damaged: {ex.Message}"); }
        catch (BudgetExceededException) { return Invalid("Project expands beyond the allowed size."); }
        catch (JsonException ex) { return Invalid($"Manifest is malformed: {ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var code = ErrorMapping.FromException(ex);
            return OpResult<DocumentState>.Fail(code, $"Could not open project: {ErrorMapping.Describe(code)}");
        }
    }

    /// <summary>Reads the embedded preview, if present (for thumbnails).</summary>
    public static byte[]? TryReadPreview(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var e = zip.GetEntry(PreviewName);
            if (e is null || e.Length > 32L * 1024 * 1024) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Upgrades older documents: normalizes null collections and converts image-linked annotations to
    /// canvas (document) coordinates at their current position.
    /// </summary>
    public static DocumentState Migrate(DocumentState d)
    {
        var n = d with
        {
            // Older schemas load into the current model; saving writes the current version.
            SchemaVersion = Math.Max(d.SchemaVersion, 1) <= DocumentState.CurrentSchemaVersion ? DocumentState.CurrentSchemaVersion : d.SchemaVersion,
            Assets = d.Assets ?? [],
            Images = (d.Images ?? []).Select(i => i is null ? i! : i with { Effects = i.Effects ?? [] }).ToArray(),
            LayoutOrder = d.LayoutOrder ?? [],
            Annotations = d.Annotations ?? [],
            Layout = d.Layout ?? LayoutOptions.Default,
        };
        if (n.Images.Any(i => i is null) || n.Annotations.Any(a => a is null)) return n; // validator reports it
        return Core.Documents.Annotations.AnnotationCanvas.Normalize(n);
    }

    private static OpResult<DocumentState> Invalid(string msg) => OpResult<DocumentState>.Fail(ErrorCode.InvalidProject, msg);

    private static async Task<byte[]> ReadLimitedAsync(Stream s, long limit, ByteBudget budget, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > limit) throw new BudgetExceededException();
            budget.Consume(n);
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private sealed class ByteBudget(long max)
    {
        private long _used;
        public void Consume(long n) { _used += n; if (_used > max) throw new BudgetExceededException(); }
    }

    private sealed class BudgetExceededException : Exception;
}
