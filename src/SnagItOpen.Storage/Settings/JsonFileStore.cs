using System.Text.Json;

namespace SnagItOpen.Storage.Settings;

/// <summary>Outcome of loading a JSON settings-like file.</summary>
public sealed record LoadOutcome<T>(T Value, string? Warning);

/// <summary>
/// Loads/saves a validated JSON document. A corrupt or invalid file never throws: it is copied to a
/// timestamped .bak next to the original and defaults are returned with a readable warning.
/// </summary>
public sealed class JsonFileStore<T> where T : class
{
    private const long MaxBytes = 4L * 1024 * 1024;
    private readonly Func<T> _defaults;
    private readonly Func<T, T> _sanitize;

    public JsonFileStore(string path, Func<T> defaults, Func<T, T>? sanitize = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _defaults = defaults;
        _sanitize = sanitize ?? (v => v);
    }

    public string Path { get; }

    public LoadOutcome<T> Load()
    {
        if (!File.Exists(Path)) return new(_defaults(), null);
        try
        {
            var info = new FileInfo(Path);
            if (info.Length > MaxBytes) return Fallback("File is too large.");
            var bytes = File.ReadAllBytes(Path);
            var v = JsonSerializer.Deserialize<T>(bytes, Json.Options);
            if (v is null) return Fallback("File is empty.");
            return new(_sanitize(v), null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Fallback(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(_defaults(), $"{System.IO.Path.GetFileName(Path)} could not be read ({ex.Message}); defaults are in use.");
        }
    }

    public void Save(T value) => AtomicFile.WriteJson(Path, value, Json.Options);

    public Task SaveAsync(T value, CancellationToken ct = default) => AtomicFile.WriteJsonAsync(Path, value, Json.Options, ct);

    private LoadOutcome<T> Fallback(string reason)
    {
        string? backup = null;
        try
        {
            backup = Path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            File.Copy(Path, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { backup = null; }
        var name = System.IO.Path.GetFileName(Path);
        var msg = backup is null
            ? $"{name} was invalid ({reason}); defaults are in use."
            : $"{name} was invalid ({reason}); defaults are in use. A backup was kept at {backup}.";
        return new(_defaults(), msg);
    }
}
