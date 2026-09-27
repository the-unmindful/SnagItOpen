using System.Text.Json;

namespace SnagItOpen.Storage;

/// <summary>Write-to-temp-then-replace helpers. The destination is only replaced after a complete write.</summary>
public static class AtomicFile
{
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await write(fs, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    public static Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct = default) =>
        WriteAsync(path, (s, c) => s.WriteAsync(bytes, c).AsTask(), ct);

    public static Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken ct = default) =>
        WriteAsync(path, (s, c) => JsonSerializer.SerializeAsync(s, value, options, c), ct);

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions options) =>
        WriteJsonAsync(path, value, options).GetAwaiter().GetResult();

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
