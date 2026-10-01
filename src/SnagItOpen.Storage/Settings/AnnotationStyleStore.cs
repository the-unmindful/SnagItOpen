using System.Text.Json;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Storage.Settings;

/// <summary>A saved, named annotation look (no geometry that matters).</summary>
public sealed record QuickStyle(string Name, Annotation Style);

public sealed record GalleryEntry(string Id, string Name, Annotation Style, bool BuiltIn = false, bool Hidden = false);

public sealed record AnnotationStyleFile(int Version, Dictionary<string, Annotation> Tools, QuickStyle[] Quick, Rgba32[] Recent,
    Dictionary<string, GalleryEntry[]>? Gallery = null);

/// <summary>
/// Per-tool prototypes (the look new annotations get), named quick styles and recent colours.
/// Invalid or unknown entries are dropped on load; a damaged file falls back to defaults with a backup.
/// </summary>
public sealed class AnnotationStyleStore
{
    public const int MaxQuick = 40, MaxRecent = 12, MaxGalleryPerTool = 40;
    private readonly JsonFileStore<AnnotationStyleFile> _file;
    private Dictionary<string, Annotation> _tools = new(StringComparer.Ordinal);
    private List<QuickStyle> _quick = [];
    private List<Rgba32> _recent = [];
    private Dictionary<string, GalleryEntry[]> _gallery = new(StringComparer.Ordinal);

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
        _gallery = r.Value.Gallery ?? new(StringComparer.Ordinal);
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

    public IReadOnlyList<GalleryEntry> GalleryFor(string kind, bool includeHidden = false)
    {
        if (!_gallery.TryGetValue(kind, out var entries)) _gallery[kind] = entries = BuiltInStyles.For(kind).ToArray();
        return includeHidden ? entries : entries.Where(e => !e.Hidden).ToArray();
    }

    public GalleryEntry SaveGallery(string kind, string name, Annotation style)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("Style name must be 1–40 characters.");
        if (style.Kind != kind || !AnnotationStyle.IsValidPrototype(style)) throw new ArgumentException("That style is not valid for this tool.");
        var entries = GalleryFor(kind, true).ToList();
        if (entries.Count >= MaxGalleryPerTool) throw new InvalidOperationException("This tool already has 40 styles. Delete a saved style first.");
        var entry = new GalleryEntry(Guid.NewGuid().ToString("N"), name, Strip(style)); entries.Add(entry); _gallery[kind] = entries.ToArray(); Persist(); return entry;
    }

    public bool RenameGallery(string kind, string id, string name)
    {
        name = (name ?? "").Trim(); if (name.Length is 0 or > 40) throw new ArgumentException("Style name must be 1–40 characters.");
        return UpdateGallery(kind, id, e => e.BuiltIn ? e : e with { Name = name });
    }
    public bool HideGallery(string kind, string id, bool hidden = true) => UpdateGallery(kind, id, e => e with { Hidden = hidden });
    public bool DeleteGallery(string kind, string id)
    {
        var entries = GalleryFor(kind, true); var target = entries.FirstOrDefault(e => e.Id == id); if (target is null) return false;
        if (target.BuiltIn) return HideGallery(kind, id);
        _gallery[kind] = entries.Where(e => e.Id != id).ToArray(); Persist(); return true;
    }
    public GalleryEntry? DuplicateGallery(string kind, string id)
    {
        var target = GalleryFor(kind, true).FirstOrDefault(e => e.Id == id); if (target is null) return null;
        string name = target.Name.Length > 35 ? target.Name[..35] + " copy" : target.Name + " copy";
        return SaveGallery(kind, name, target.Style);
    }
    public bool MoveGallery(string kind, string id, int index)
    {
        var entries = GalleryFor(kind, true).ToList(); var target = entries.FirstOrDefault(e => e.Id == id); if (target is null) return false;
        int old = entries.IndexOf(target); index = Math.Clamp(index, 0, entries.Count - 1); if (index == old) return false;
        entries.RemoveAt(old); entries.Insert(index, target); _gallery[kind] = entries.ToArray(); Persist(); return true;
    }
    public bool MoveGalleryBy(string kind, string id, int direction)
    {
        var entries = GalleryFor(kind, true).ToList(); return MoveGallery(kind, id, entries.FindIndex(e => e.Id == id) + direction);
    }
    public void RestoreBuiltIns(string kind)
    {
        _gallery[kind] = GalleryFor(kind, true).Select(e => e.BuiltIn ? e with { Hidden = false } : e).ToArray(); Persist();
    }
    private bool UpdateGallery(string kind, string id, Func<GalleryEntry, GalleryEntry> update)
    {
        var entries = GalleryFor(kind, true).ToArray(); int index = Array.FindIndex(entries, e => e.Id == id); if (index < 0) return false;
        var next = update(entries[index]); if (next == entries[index]) return false; entries[index] = next; _gallery[kind] = entries; Persist(); return true;
    }

    private static Annotation Strip(Annotation a) => a with { Id = Guid.Empty, ImageLayerId = null, Locked = false, Hidden = false, Name = null, Rotation = 0 };

    private void Persist()
    {
        try { _file.Save(new AnnotationStyleFile(2, _tools, _quick.ToArray(), _recent.ToArray(), _gallery)); }
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
        var gallery = new Dictionary<string, GalleryEntry[]>(StringComparer.Ordinal);
        foreach (string kind in BuiltInStyles.Kinds)
        {
            var builtIns = BuiltInStyles.For(kind).ToDictionary(e => e.Id, StringComparer.Ordinal);
            var source = f.Gallery?.GetValueOrDefault(kind) ?? [];
            var entries = new List<GalleryEntry>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in source)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id)) continue;
                if (builtIns.TryGetValue(entry.Id, out var builtin)) entries.Add(builtin with { Hidden = entry.Hidden });
                else if (!entry.BuiltIn && entry.Style is { } style && style.Kind == kind && AnnotationStyle.IsValidPrototype(style)
                    && !string.IsNullOrWhiteSpace(entry.Name) && entry.Name.Length <= 40) entries.Add(entry with { Style = Strip(style) });
            }
            foreach (var entry in builtIns.Values) if (ids.Add(entry.Id)) entries.Add(entry);
            if (f.Gallery is null)
                foreach (var q in quick.Where(q => q.Style.Kind == kind)) entries.Add(new GalleryEntry("migrated:" + kind + ":" + q.Name, q.Name, Strip(q.Style)));
            // Reserve room for every built-in and retain user ordering within that bound.
            while (entries.Count > MaxGalleryPerTool)
            {
                int index = entries.FindLastIndex(e => !e.BuiltIn); if (index < 0) break; entries.RemoveAt(index);
            }
            gallery[kind] = entries.ToArray();
        }
        return new AnnotationStyleFile(2, tools, quick, (f.Recent ?? []).Distinct().Take(MaxRecent).ToArray(), gallery);
    }
}
