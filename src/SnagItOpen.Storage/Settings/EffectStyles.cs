namespace SnagItOpen.Storage.Settings;

/// <summary>Image-effect defaults are tool styles, never annotations or project objects.</summary>
public sealed record EffectGalleryEntry(string Id, string Name, ToolStyle Style, bool BuiltIn = false, bool Hidden = false);

public static class BuiltInEffectStyles
{
    public static readonly string[] Kinds = ["Blur", "Pixelate"];
    public static IReadOnlyList<EffectGalleryEntry> For(string kind)
    {
        int[] strengths = kind switch { "Blur" => [2, 4, 8, 12, 20, 32], "Pixelate" => [2, 4, 8, 16, 32, 64], _ => [] };
        string[] names = ["Subtle", "Light", "Medium", "Strong", "Heavy", "Maximum"];
        return strengths.Select((strength, index) => new EffectGalleryEntry($"builtin:{kind}:{strength}", $"{names[index]} {kind.ToLowerInvariant()}", new() { EffectStrength = strength }, true)).ToArray();
    }
    public static int ClampStrength(string kind, int strength) => kind switch
    {
        "Blur" => Math.Clamp(strength, Core.Documents.ImageEffect.MinBlur, Core.Documents.ImageEffect.MaxBlur),
        "Pixelate" => Math.Clamp(strength, Core.Documents.ImageEffect.MinBlock, Core.Documents.ImageEffect.MaxBlock),
        _ => throw new ArgumentException("Choose Blur or Pixelate.", nameof(kind)),
    };
}

public sealed partial class ToolStyleStore
{
    public const int MaxEffectGalleryPerTool = 40;
    private Dictionary<string, EffectGalleryEntry[]> _effectGallery = new(StringComparer.Ordinal);
    public event Action? Changed;

    public IReadOnlyList<EffectGalleryEntry> EffectGalleryFor(string kind, bool includeHidden = false)
    {
        if (!BuiltInEffectStyles.Kinds.Contains(kind, StringComparer.Ordinal)) return [];
        if (!_effectGallery.TryGetValue(kind, out var entries)) _effectGallery[kind] = entries = BuiltInEffectStyles.For(kind).ToArray();
        return includeHidden ? entries : entries.Where(entry => !entry.Hidden).ToArray();
    }
    public EffectGalleryEntry SaveEffectGallery(string kind, string name, ToolStyle style)
    {
        name = ValidName(name); var entries = EffectGalleryFor(kind, true).ToList();
        int strength = BuiltInEffectStyles.ClampStrength(kind, style.EffectStrength);
        if (entries.Count >= MaxEffectGalleryPerTool) throw new InvalidOperationException("This tool already has 40 styles. Delete a saved style first.");
        var entry = new EffectGalleryEntry(Guid.NewGuid().ToString("N"), name, new() { EffectStrength = strength });
        entries.Add(entry); _effectGallery[kind] = entries.ToArray(); PersistStyles(); return entry;
    }
    public bool RenameEffectGallery(string kind, string id, string name)
    {
        name = ValidName(name); return UpdateEffectGallery(kind, id, entry => entry.BuiltIn ? entry : entry with { Name = name });
    }
    public bool HideEffectGallery(string kind, string id, bool hidden = true) => UpdateEffectGallery(kind, id, entry => entry with { Hidden = hidden });
    public bool DeleteEffectGallery(string kind, string id)
    {
        var entries = EffectGalleryFor(kind, true); var entry = entries.FirstOrDefault(item => item.Id == id); if (entry is null) return false;
        if (entry.BuiltIn) return HideEffectGallery(kind, id);
        _effectGallery[kind] = entries.Where(item => item.Id != id).ToArray(); PersistStyles(); return true;
    }
    public EffectGalleryEntry? DuplicateEffectGallery(string kind, string id)
    {
        var entry = EffectGalleryFor(kind, true).FirstOrDefault(item => item.Id == id);
        return entry is null ? null : SaveEffectGallery(kind, (entry.Name.Length > 35 ? entry.Name[..35] : entry.Name) + " copy", entry.Style);
    }
    public bool MoveEffectGallery(string kind, string id, int index)
    {
        var entries = EffectGalleryFor(kind, true).ToList(); var entry = entries.FirstOrDefault(item => item.Id == id); if (entry is null) return false;
        index = Math.Clamp(index, 0, entries.Count - 1); if (entries.IndexOf(entry) == index) return false;
        entries.Remove(entry); entries.Insert(index, entry); _effectGallery[kind] = entries.ToArray(); PersistStyles(); return true;
    }
    public bool MoveEffectGalleryBy(string kind, string id, int delta) => MoveEffectGallery(kind, id, EffectGalleryFor(kind, true).ToList().FindIndex(entry => entry.Id == id) + delta);
    public void RestoreBuiltInEffects(string kind)
    {
        _effectGallery[kind] = EffectGalleryFor(kind, true).Select(entry => entry.BuiltIn ? entry with { Hidden = false } : entry).ToArray(); PersistStyles();
    }
    private bool UpdateEffectGallery(string kind, string id, Func<EffectGalleryEntry, EffectGalleryEntry> update)
    {
        var entries = EffectGalleryFor(kind, true).ToArray(); int index = Array.FindIndex(entries, entry => entry.Id == id); if (index < 0) return false;
        var changed = update(entries[index]); if (changed == entries[index]) return false;
        entries[index] = changed; _effectGallery[kind] = entries; PersistStyles(); return true;
    }
    private static string ValidName(string name)
    {
        name = (name ?? "").Trim(); if (name.Length is 0 or > 40) throw new ArgumentException("Style name must be 1–40 characters."); return name;
    }
    private void PersistStyles()
    {
        try { _file.Save(new ToolStyleFile(1, _styles, _effectGallery)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastWarning = $"Styles could not be saved: {ex.Message}"; }
        Changed?.Invoke();
    }
    private static ToolStyleFile SanitizeFile(ToolStyleFile file)
    {
        var styles = (file.Styles ?? new()).Where(item => item.Value is not null && item.Key.Length <= 40)
            .ToDictionary(item => item.Key, item => item.Value.Sanitize(), StringComparer.Ordinal);
        var gallery = new Dictionary<string, EffectGalleryEntry[]>(StringComparer.Ordinal);
        foreach (string kind in BuiltInEffectStyles.Kinds)
        {
            var builtIns = BuiltInEffectStyles.For(kind).ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var entries = new List<EffectGalleryEntry>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in file.EffectGallery?.GetValueOrDefault(kind) ?? [])
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || !seen.Add(entry.Id)) continue;
                if (builtIns.TryGetValue(entry.Id, out var builtIn)) entries.Add(builtIn with { Hidden = entry.Hidden });
                else if (!entry.BuiltIn && entry.Style is not null && !string.IsNullOrWhiteSpace(entry.Name) && entry.Name.Length <= 40)
                    entries.Add(entry with { Style = new() { EffectStrength = BuiltInEffectStyles.ClampStrength(kind, entry.Style.EffectStrength) } });
            }
            foreach (var builtIn in builtIns.Values) if (seen.Add(builtIn.Id)) entries.Add(builtIn);
            while (entries.Count > MaxEffectGalleryPerTool) { int index = entries.FindLastIndex(entry => !entry.BuiltIn); if (index < 0) break; entries.RemoveAt(index); }
            gallery[kind] = entries.ToArray();
        }
        return new ToolStyleFile(1, styles, gallery);
    }
}
