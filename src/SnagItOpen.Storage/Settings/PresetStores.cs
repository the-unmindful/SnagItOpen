using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Storage.Settings;

/// <summary>Named layout + background defaults. Contains no image assets.</summary>
public sealed record LayoutPreset(string Name, LayoutOptions Layout, Rgba32 Background, bool BuiltIn = false);

public sealed record LayoutPresetFile(int Version, LayoutPreset[] Presets);

/// <summary>User layout presets merged with read-only built-ins.</summary>
public sealed class LayoutPresetStore
{
    public static readonly LayoutPreset[] BuiltIns =
    [
        new("Vertical compact", LayoutOptions.Default with { Mode = LayoutMode.Vertical }, Rgba32.Transparent, true),
        new("Vertical spaced", LayoutOptions.Default with { Mode = LayoutMode.Vertical, Gap = 12, Padding = 16, Alignment = CrossAlignment.Center }, Rgba32.White, true),
        new("Horizontal comparison", LayoutOptions.Default with { Mode = LayoutMode.Horizontal, Gap = 16, Padding = 16, Alignment = CrossAlignment.Center, Scale = ScaleMode.MatchCrossAxis }, Rgba32.White, true),
    ];

    private readonly JsonFileStore<LayoutPresetFile> _file;
    private List<LayoutPreset> _user = [];

    public LayoutPresetStore(string path)
    {
        _file = new JsonFileStore<LayoutPresetFile>(path, () => new LayoutPresetFile(1, []), Sanitize);
    }

    public string? LastWarning { get; private set; }

    public IReadOnlyList<LayoutPreset> All => [.. BuiltIns, .. _user];

    public void Load()
    {
        var r = _file.Load();
        LastWarning = r.Warning;
        _user = r.Value.Presets.ToList();
    }

    public void SaveOrReplace(LayoutPreset preset)
    {
        var name = ValidateName(preset.Name);
        LayoutEngineValidate(preset.Layout);
        _user.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _user.Add(preset with { Name = name, BuiltIn = false });
        Persist();
    }

    public void Rename(string oldName, string newName)
    {
        var name = ValidateName(newName);
        var idx = _user.FindIndex(p => p.Name == oldName);
        if (idx < 0) throw new InvalidOperationException("Built-in presets cannot be renamed.");
        if (All.Any(p => !ReferenceEquals(p, _user[idx]) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A preset named '{name}' already exists.");
        _user[idx] = _user[idx] with { Name = name };
        Persist();
    }

    public bool Delete(string name)
    {
        int n = _user.RemoveAll(p => p.Name == name);
        if (n > 0) Persist();
        return n > 0;
    }

    private void Persist() => _file.Save(new LayoutPresetFile(1, _user.ToArray()));

    private static string ValidateName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 64) throw new ArgumentException("Preset name must be 1–64 characters.");
        if (BuiltIns.Any(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("That name is used by a built-in preset.");
        return n;
    }

    private static void LayoutEngineValidate(LayoutOptions o) => Core.Layout.LayoutEngine.ValidateOptions(o);

    private static LayoutPresetFile Sanitize(LayoutPresetFile f)
    {
        var valid = new List<LayoutPreset>();
        foreach (var p in f.Presets ?? [])
        {
            if (p?.Layout is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 64) continue;
            try { Core.Layout.LayoutEngine.ValidateOptions(p.Layout); } catch (ArgumentException) { continue; }
            if (!Enum.IsDefined(p.Layout.Mode) || !Enum.IsDefined(p.Layout.Alignment) || !Enum.IsDefined(p.Layout.Scale)) continue;
            if (BuiltIns.Any(b => string.Equals(b.Name, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (valid.Any(v => string.Equals(v.Name, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            valid.Add(p with { BuiltIn = false });
        }
        return new LayoutPresetFile(1, valid.ToArray());
    }
}

/// <summary>Last-used style per annotation tool (color, stroke, font, etc.).</summary>
public sealed record ToolStyle
{
    public Rgba32 Color { get; init; } = Rgba32.Red;
    public Rgba32? Fill { get; init; }
    public double StrokeWidth { get; init; } = 3;
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 24;
    public bool Bold { get; init; }
    public bool Dashed { get; init; }
    public int EffectStrength { get; init; } = 8;
    public double Zoom { get; init; } = 2;
    public string? Symbol { get; init; }

    public ToolStyle Sanitize() => this with
    {
        StrokeWidth = double.IsFinite(StrokeWidth) ? Math.Clamp(StrokeWidth, 0.5, 64) : 3,
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 4, 400) : 24,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 100 ? "Segoe UI" : FontFamily,
        EffectStrength = Math.Clamp(EffectStrength, 1, 64),
        Zoom = double.IsFinite(Zoom) ? Math.Clamp(Zoom, 1.25, 8) : 2,
    };
}

public sealed record ToolStyleFile(int Version, Dictionary<string, ToolStyle> Styles);

public sealed class ToolStyleStore
{
    private readonly JsonFileStore<ToolStyleFile> _file;
    private Dictionary<string, ToolStyle> _styles = new(StringComparer.Ordinal);

    public ToolStyleStore(string path) =>
        _file = new JsonFileStore<ToolStyleFile>(path, () => new ToolStyleFile(1, new()), f => new ToolStyleFile(1,
            (f.Styles ?? new()).Where(kv => kv.Value is not null && kv.Key.Length <= 40)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Sanitize(), StringComparer.Ordinal)));

    public string? LastWarning { get; private set; }

    public void Load()
    {
        var r = _file.Load();
        LastWarning = r.Warning;
        _styles = new Dictionary<string, ToolStyle>(r.Value.Styles, StringComparer.Ordinal);
    }

    public ToolStyle Get(string tool, ToolStyle? fallback = null) =>
        _styles.TryGetValue(tool, out var s) ? s : fallback ?? new ToolStyle();

    public void Set(string tool, ToolStyle style)
    {
        _styles[tool] = style.Sanitize();
        try { _file.Save(new ToolStyleFile(1, _styles)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Output format for quick-output presets.</summary>
public enum QuickOutputFormat { Png, Jpeg }

/// <summary>A reusable capture recipe, optionally bound to a hotkey and a quick output.</summary>
public sealed record CapturePreset
{
    public string Name { get; init; } = "Preset";
    public CaptureMode Mode { get; init; } = CaptureMode.Region;
    public int DelaySeconds { get; init; }
    public bool IncludeCursor { get; init; }
    public CaptureDestination Destination { get; init; } = CaptureDestination.AppendBelow;
    public CaptureShape Shape { get; init; } = CaptureShape.Rectangle;
    public PixelSize? FixedSize { get; init; }
    public double? AspectRatio { get; init; }
    public string Hotkey { get; init; } = "";
    /// <summary>When set, the capture is also saved into this local folder.</summary>
    public string? OutputFolder { get; init; }
    /// <summary>Filename template: {date}, {time}, {n}. No path separators.</summary>
    public string FileNameTemplate { get; init; } = "Capture {date} {time}";
    public QuickOutputFormat OutputFormat { get; init; } = QuickOutputFormat.Png;
    public bool CopyToClipboard { get; init; }
    public LastRegion? Region { get; init; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64) return "Name must be 1–64 characters.";
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Destination) || !Enum.IsDefined(Shape) || !Enum.IsDefined(OutputFormat)) return "Invalid option.";
        if (!CaptureOptions.AllowedDelays.Contains(DelaySeconds)) return "Delay must be 0, 3, 5 or 10 seconds.";
        if (FixedSize is { } fs && (fs.Width < 1 || fs.Height < 1 || fs.Width > Limits.MaxDimension || fs.Height > Limits.MaxDimension)) return "Fixed size is out of range.";
        if (AspectRatio is { } ar && (!double.IsFinite(ar) || ar <= 0.01 || ar > 100)) return "Aspect ratio is out of range.";
        if (FileNameTemplate is null || FileNameTemplate.Length is 0 or > 120 || FileNameTemplate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "File name template contains invalid characters.";
        if (OutputFolder is { } f && (f.Length > 260 || !Path.IsPathFullyQualified(f))) return "Output folder must be a full local path.";
        return null;
    }
}

public sealed record CapturePresetFile(int Version, CapturePreset[] Presets);

public sealed class CapturePresetStore
{
    private readonly JsonFileStore<CapturePresetFile> _file;

    public CapturePresetStore(string path) =>
        _file = new JsonFileStore<CapturePresetFile>(path, () => new CapturePresetFile(1, []),
            f => new CapturePresetFile(1, (f.Presets ?? []).Where(p => p is not null && p.Validate() is null)
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray()));

    public List<CapturePreset> Presets { get; private set; } = [];
    public string? LastWarning { get; private set; }

    public void Load()
    {
        var r = _file.Load();
        LastWarning = r.Warning;
        Presets = r.Value.Presets.ToList();
    }

    public void Save(IEnumerable<CapturePreset> presets)
    {
        var list = presets.ToList();
        foreach (var p in list) if (p.Validate() is { } e) throw new ArgumentException($"{p.Name}: {e}");
        if (list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("Preset names must be unique.");
        _file.Save(new CapturePresetFile(1, list.ToArray()));
        Presets = list;
    }

    /// <summary>Resolves a unique, non-overwriting output file name.</summary>
    public static string ResolveOutputPath(CapturePreset preset, DateTime now, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var folder = preset.OutputFolder ?? throw new InvalidOperationException("Preset has no output folder.");
        var ext = preset.OutputFormat == QuickOutputFormat.Jpeg ? ".jpg" : ".png";
        var baseName = preset.FileNameTemplate
            .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HH-mm-ss"), StringComparison.OrdinalIgnoreCase);
        bool hasCounter = baseName.Contains("{n}", StringComparison.OrdinalIgnoreCase);
        for (int n = 1; n < 100_000; n++)
        {
            string name = hasCounter
                ? baseName.Replace("{n}", n.ToString(), StringComparison.OrdinalIgnoreCase)
                : n == 1 ? baseName : $"{baseName} ({n})";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var p = Path.Combine(folder, name + ext);
            if (!exists(p)) return p;
        }
        throw new IOException("Could not find a free file name.");
    }
}

/// <summary>Persists the last captured region with its monitor topology.</summary>
public sealed class LastRegionStore
{
    private readonly JsonFileStore<LastRegionHolder> _file;
    public LastRegionStore(string path) => _file = new JsonFileStore<LastRegionHolder>(path, () => new LastRegionHolder(null));

    public LastRegion? Load() => _file.Load().Value.Region is { Bounds.IsEmpty: false } r ? r : null;

    public void Save(LastRegion region)
    {
        try { _file.Save(new LastRegionHolder(region)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Returns the region only when captured on the same topology.</summary>
    public static LastRegion? Resolve(LastRegion? stored, string currentFingerprint) =>
        stored is not null && stored.TopologyFingerprint == currentFingerprint ? stored : null;

    public sealed record LastRegionHolder(LastRegion? Region);
}
