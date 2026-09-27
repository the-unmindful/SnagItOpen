using System.Text.Json;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Storage.Settings;

/// <summary>A saved, named annotation look (no geometry that matters).</summary>
public sealed record QuickStyle(string Name, Annotation Style);

public sealed record AnnotationStyleFile(int Version, Dictionary<string, Annotation> Tools, QuickStyle[] Quick, Rgba32[] Recent);

/// <summary>
/// Per-tool prototypes (the look new annotations get), named quick styles and recent colours.
/// Invalid or unknown entries are dropped on load; a damaged file falls back to defaults with a backup.
/// </summary>
public sealed class AnnotationStyleStore
{
    public const int MaxQuick = 40, MaxRecent = 12;
    private readonly JsonFileStore<AnnotationStyleFile> _file;
    private Dictionary<string, Annotation> _tools = new(StringComparer.Ordinal);
    private List<QuickStyle> _quick = [];
    private List<Rgba32> _recent = [];

    public AnnotationStyleStore(string path) =>
        _file = new JsonFileStore<AnnotationStyleFile>(path, () => new AnnotationStyleFile(1, new(), [], []), Sanitize);

    public string? LastWarning { get; private set; }
    public IReadOnlyList<QuickStyle> QuickStyles => _quick;
    public IReadOnlyList<Rgba32> RecentColors => _recent;
    public event Action? Changed;

    public void Load()
    {
        var r = _file.Load();
        LastWarning = r.Warning;
        _tools = new Dictionary<string, Annotation>(r.Value.Tools, StringComparer.Ordinal);
        _quick = r.Value.Quick.ToList();
        _recent = r.Value.Recent.ToList();
    }

    /// <summary>Prototype for a tool kind (defaults when never customized).</summary>
    public Annotation Prototype(string kind) =>
        _tools.TryGetValue(kind, out var a) ? a : AnnotationStyle.DefaultPrototype(kind) ?? new RectangleAnnotation();

    public void SetPrototype(string kind, Annotation a)
    {
        if (a.Kind != kind || !AnnotationStyle.IsValidPrototype(a)) return;
        _tools[kind] = Strip(a);
        Persist();
    }

    public void SaveQuick(string name, Annotation style)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("Style name must be 1–40 characters.");
        if (!AnnotationStyle.IsValidPrototype(style)) throw new ArgumentException("That style is not valid.");
        _quick.RemoveAll(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
        _quick.Add(new QuickStyle(name, Strip(style)));
        if (_quick.Count > MaxQuick) _quick.RemoveAt(0);
        Persist();
    }

    public bool DeleteQuick(string name)
    {
        int n = _quick.RemoveAll(q => q.Name == name);
        if (n > 0) Persist();
        return n > 0;
    }

    public void UseColor(Rgba32 c)
    {
        _recent.Remove(c);
        _recent.Insert(0, c);
        if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        Persist();
    }

    private static Annotation Strip(Annotation a) => a with { Id = Guid.Empty, ImageLayerId = null, Locked = false, Rotation = 0 };

    private void Persist()
    {
        try { _file.Save(new AnnotationStyleFile(1, _tools, _quick.ToArray(), _recent.ToArray())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastWarning = $"Styles could not be saved: {ex.Message}"; }
        Changed?.Invoke();
    }

    private static AnnotationStyleFile Sanitize(AnnotationStyleFile f)
    {
        var tools = new Dictionary<string, Annotation>(StringComparer.Ordinal);
        foreach (var (k, v) in f.Tools ?? new())
            if (v is not null && v.Kind == k && AnnotationStyle.IsValidPrototype(v)) tools[k] = v;
        var quick = (f.Quick ?? []).Where(q => q?.Style is not null && !string.IsNullOrWhiteSpace(q.Name) && q.Name.Length <= 40
                && AnnotationStyle.IsValidPrototype(q.Style))
            .GroupBy(q => q.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(MaxQuick).ToArray();
        return new AnnotationStyleFile(1, tools, quick, (f.Recent ?? []).Distinct().Take(MaxRecent).ToArray());
    }
}
