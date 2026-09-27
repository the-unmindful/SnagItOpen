using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Storage.Assets;

namespace SnagItOpen.Storage.History;

/// <summary>One recent capture. The PNG lives in the shared asset store; the thumbnail in history/thumbs.</summary>
public sealed record CaptureEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string AssetId { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public long Bytes { get; init; }
    public string? Name { get; init; }
    public bool Pinned { get; init; }
    public string? ThumbnailFile { get; init; }
    public string? Source { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Capture {CapturedAt:yyyy-MM-dd HH:mm:ss}" : Name!;
}

public sealed record CaptureIndex(int Version, CaptureEntry[] Entries);

/// <summary>Automatic cleanup limits. Pinned entries are exempt.</summary>
public sealed record RetentionPolicy(int MaxCount = 200, long MaxBytes = 500L * 1024 * 1024)
{
    /// <summary>
    /// Entries to delete, oldest first, until unpinned count and total bytes fit. Entries whose asset is
    /// protected (open document, undo, save in progress) are skipped but still count.
    /// </summary>
    public IReadOnlyList<CaptureEntry> SelectForDeletion(IReadOnlyList<CaptureEntry> entries, ISet<string>? protectedAssets = null)
    {
        var result = new List<CaptureEntry>();
        var unpinned = entries.Where(e => !e.Pinned).OrderBy(e => e.CapturedAt).ThenBy(e => e.Id).ToList();
        int count = unpinned.Count;
        long bytes = entries.Sum(e => e.Bytes);
        foreach (var e in unpinned)
        {
            if (count <= MaxCount && bytes <= MaxBytes) break;
            if (protectedAssets?.Contains(e.AssetId) == true) continue;
            result.Add(e);
            count--;
            bytes -= e.Bytes;
        }
        return result;
    }
}

/// <summary>
/// Recent capture library. The index is written atomically; if damaged it is rebuilt from per-entry
/// metadata sidecar files under history/meta.
/// </summary>
public sealed class CaptureHistoryStore
{
    private readonly string _root;
    private readonly FileAssetStore _assets;
    private readonly object _gate = new();
    private List<CaptureEntry> _entries = [];

    public CaptureHistoryStore(string historyRoot, FileAssetStore assets)
    {
        _root = Path.GetFullPath(historyRoot);
        _assets = assets;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(MetaDir);
        Directory.CreateDirectory(ThumbDir);
    }

    public string IndexPath => Path.Combine(_root, "index.json");
    public string MetaDir => Path.Combine(_root, "meta");
    public string ThumbDir => Path.Combine(_root, "thumbs");
    public string? LastWarning { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<CaptureEntry> Entries
    {
        get { lock (_gate) return _entries.OrderByDescending(e => e.CapturedAt).ToList(); }
    }

    public long TotalBytes { get { lock (_gate) return _entries.Sum(e => e.Bytes); } }

    public void Load()
    {
        lock (_gate)
        {
            LastWarning = null;
            var store = new Settings.JsonFileStore<CaptureIndex>(IndexPath, () => new CaptureIndex(1, []));
            var r = store.Load();
            if (r.Warning is not null || (!File.Exists(IndexPath) && Directory.EnumerateFiles(MetaDir, "*.json").Any()))
            {
                _entries = RebuildFromMeta();
                LastWarning = r.Warning is null ? null : "Capture history index was damaged and has been rebuilt.";
                SaveIndexLocked();
            }
            else _entries = r.Value.Entries.Where(e => e is not null && DocumentValidator.IsHash(e.AssetId)).ToList();
            // Drop entries whose asset vanished.
            _entries.RemoveAll(e => !_assets.Contains(e.AssetId));
        }
    }

    public CaptureEntry Add(ImageAsset asset, byte[]? thumbnailPng, string? source = null)
    {
        CaptureEntry entry;
        lock (_gate)
        {
            long bytes = 0;
            try { bytes = new FileInfo(_assets.PathFor(asset.Id)).Length; } catch (IOException) { }
            entry = new CaptureEntry { AssetId = asset.Id, Width = asset.Width, Height = asset.Height, Bytes = bytes, Source = source };
            if (thumbnailPng is { Length: > 0 })
            {
                var tf = entry.Id.ToString("N") + ".png";
                try { File.WriteAllBytes(Path.Combine(ThumbDir, tf), thumbnailPng); entry = entry with { ThumbnailFile = tf }; }
                catch (IOException) { }
            }
            _entries.Add(entry);
            WriteMeta(entry);
            SaveIndexLocked();
        }
        Changed?.Invoke();
        return entry;
    }

    public void Update(Guid id, Func<CaptureEntry, CaptureEntry> change)
    {
        lock (_gate)
        {
            int i = _entries.FindIndex(e => e.Id == id);
            if (i < 0) return;
            var n = change(_entries[i]) with { Id = id, AssetId = _entries[i].AssetId };
            _entries[i] = n;
            WriteMeta(n);
            SaveIndexLocked();
        }
        Changed?.Invoke();
    }

    public void Rename(Guid id, string? name) => Update(id, e => e with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 120)] });
    public void SetPinned(Guid id, bool pinned) => Update(id, e => e with { Pinned = pinned });

    public string? ThumbnailPath(CaptureEntry e) => e.ThumbnailFile is { } f ? Path.Combine(ThumbDir, f) : null;

    /// <summary>
    /// Deletes library entries (references first), then deletes assets that no remaining entry and
    /// no protected owner references. Returns deleted entry count.
    /// </summary>
    public int Delete(IReadOnlyCollection<Guid> ids, ISet<string>? protectedAssets = null)
    {
        List<CaptureEntry> removed;
        lock (_gate)
        {
            removed = _entries.Where(e => ids.Contains(e.Id)).ToList();
            if (removed.Count == 0) return 0;
            _entries.RemoveAll(e => ids.Contains(e.Id));
            SaveIndexLocked();
            foreach (var e in removed)
            {
                AtomicFile.TryDelete(MetaPath(e.Id));
                if (ThumbnailPath(e) is { } t) AtomicFile.TryDelete(t);
            }
            var stillUsed = _entries.Select(e => e.AssetId).ToHashSet(StringComparer.Ordinal);
            foreach (var a in removed.Select(e => e.AssetId).Distinct())
                if (!stillUsed.Contains(a) && protectedAssets?.Contains(a) != true) _assets.Delete(a);
        }
        Changed?.Invoke();
        return removed.Count;
    }

    /// <summary>Applies the retention policy. Protected assets are never deleted.</summary>
    public int ApplyRetention(RetentionPolicy policy, ISet<string>? protectedAssets = null)
    {
        IReadOnlyList<CaptureEntry> victims;
        lock (_gate) victims = policy.SelectForDeletion(_entries, protectedAssets);
        return victims.Count == 0 ? 0 : Delete(victims.Select(v => v.Id).ToArray(), protectedAssets);
    }

    /// <summary>Asset IDs referenced by the library (owners for asset retention).</summary>
    public ISet<string> ReferencedAssets()
    {
        lock (_gate) return _entries.Select(e => e.AssetId).ToHashSet(StringComparer.Ordinal);
    }

    private string MetaPath(Guid id) => Path.Combine(MetaDir, id.ToString("N") + ".json");

    private void WriteMeta(CaptureEntry e)
    {
        try { AtomicFile.WriteJson(MetaPath(e.Id), e, Json.Options); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void SaveIndexLocked()
    {
        try { AtomicFile.WriteJson(IndexPath, new CaptureIndex(1, _entries.ToArray()), Json.Options); }
        catch (IOException ex) { LastWarning = $"Could not write capture history: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { LastWarning = $"Could not write capture history: {ex.Message}"; }
    }

    private List<CaptureEntry> RebuildFromMeta()
    {
        var list = new List<CaptureEntry>();
        foreach (var f in Directory.EnumerateFiles(MetaDir, "*.json"))
        {
            try
            {
                var e = System.Text.Json.JsonSerializer.Deserialize<CaptureEntry>(File.ReadAllBytes(f), Json.Options);
                if (e is not null && DocumentValidator.IsHash(e.AssetId) && _assets.Contains(e.AssetId)) list.Add(e);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { }
        }
        return list;
    }
}
