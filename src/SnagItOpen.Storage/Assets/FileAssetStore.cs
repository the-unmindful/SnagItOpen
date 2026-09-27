using System.Security.Cryptography;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;

namespace SnagItOpen.Storage.Assets;

/// <summary>
/// Content-addressed PNG store. Bytes are hashed while written to a temp file, then renamed to
/// &lt;sha256&gt;.png. Existing files are never overwritten with different data.
/// </summary>
public sealed class FileAssetStore : IAssetStore
{
    private readonly object _gate = new();

    public FileAssetStore(string root)
    {
        RootDirectory = Path.GetFullPath(root);
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public string PathFor(string assetId)
    {
        if (!DocumentValidator.IsHash(assetId)) throw new ArgumentException("Invalid asset id.", nameof(assetId));
        return Path.Combine(RootDirectory, assetId + ".png");
    }

    public bool Contains(string assetId) => DocumentValidator.IsHash(assetId) && File.Exists(PathFor(assetId));

    public async Task<ImageAsset> PutAsync(Stream normalizedPng, int width, int height, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedPng);
        if (!Limits.IsAcceptableImageSize(width, height))
            throw new ArgumentOutOfRangeException(nameof(width), $"Asset size {width}×{height} is outside limits.");
        ct.ThrowIfCancellationRequested();
        var tmp = Path.Combine(RootDirectory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string hash;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buf = new byte[81920];
                int n;
                bool first = true;
                while ((n = await normalizedPng.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    if (first)
                    {
                        first = false;
                        if (n >= 24 && PngInfo.TryReadSize(buf.AsSpan(0, n), out var pw, out var ph) && (pw != width || ph != height))
                            throw new InvalidDataException($"PNG header {pw}×{ph} does not match declared {width}×{height}.");
                    }
                    sha.AppendData(buf, 0, n);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                if (first) throw new InvalidDataException("Empty image data.");
                await fs.FlushAsync(ct).ConfigureAwait(false);
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            ct.ThrowIfCancellationRequested();
            var dest = PathFor(hash);
            lock (_gate)
            {
                if (File.Exists(dest))
                {
                    // Keep the existing file; verify it is the same content (hash collision/corruption guard).
                    if (!VerifyHash(dest, hash))
                        File.Move(tmp, dest, overwrite: true); // existing file is corrupt: repair with verified content
                }
                else
                {
                    File.Move(tmp, dest);
                }
            }
            return ImageAsset.Create(hash, width, height);
        }
        finally
        {
            AtomicFile.TryDelete(tmp);
        }
    }

    public Task<Stream> OpenReadAsync(string assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!DocumentValidator.IsHash(assetId)) throw new AssetNotFoundException(assetId);
        var p = PathFor(assetId);
        try
        {
            Stream s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult(s);
        }
        catch (FileNotFoundException) { throw new AssetNotFoundException(assetId); }
        catch (DirectoryNotFoundException) { throw new AssetNotFoundException(assetId); }
    }

    public static bool VerifyHash(string path, string expected)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(fs)) == expected;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Asset ids present on disk.</summary>
    public IEnumerable<string> Enumerate() =>
        Directory.EnumerateFiles(RootDirectory, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && DocumentValidator.IsHash(n))!;

    /// <summary>Deletes an asset file. Callers must have verified it is unreferenced.</summary>
    public bool Delete(string assetId)
    {
        if (!Contains(assetId)) return false;
        try { File.Delete(PathFor(assetId)); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Removes leftover temp files from interrupted writes.</summary>
    public int CleanupTemporaryFiles()
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(RootDirectory, ".*.tmp"))
        {
            try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return n;
    }
}
