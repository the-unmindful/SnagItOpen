using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;

namespace SnagItOpen.Storage.Assets;

/// <summary>Collects asset IDs referenced by documents (layers and imported stamps).</summary>
public static class AssetReferences
{
    public static IEnumerable<string> Of(DocumentState doc)
    {
        foreach (var a in doc.Assets) yield return a.Id;
        foreach (var i in doc.Images) yield return i.AssetId;
        foreach (var an in doc.Annotations) if (an is StampAnnotation { AssetId: { } s }) yield return s;
    }

    public static HashSet<string> Of(IEnumerable<DocumentState> docs)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in docs) foreach (var id in Of(d)) set.Add(id);
        return set;
    }
}

/// <summary>
/// Deletes session assets that no owner references. Owners: open documents incl. undo/redo,
/// in-progress saves, recovery snapshots, and the capture library. A grace period protects
/// assets written moments ago by an import that has not been committed yet.
/// </summary>
public sealed class AssetRetentionService
{
    private readonly FileAssetStore _store;
    private readonly List<Func<IEnumerable<string>>> _owners = [];
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AssetRetentionService(FileAssetStore store) => _store = store;

    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromMinutes(10);

    public void AddOwner(Func<IEnumerable<string>> owner) { lock (_gate) _owners.Add(owner); }

    /// <summary>Temporarily protects assets (e.g. during a project save). Dispose to release.</summary>
    public IDisposable Protect(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        lock (_gate) foreach (var i in list) _pinned.Add(i);
        return new Releaser(() => { lock (_gate) foreach (var i in list) _pinned.Remove(i); });
    }

    public HashSet<string> Referenced()
    {
        lock (_gate)
        {
            var set = new HashSet<string>(_pinned, StringComparer.Ordinal);
            foreach (var o in _owners) foreach (var id in o()) set.Add(id);
            return set;
        }
    }

    /// <summary>Deletes unreferenced assets older than the grace period. Returns deleted count.</summary>
    public int Collect(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        _store.CleanupTemporaryFiles();
        var keep = Referenced();
        int n = 0;
        foreach (var id in _store.Enumerate().ToList())
        {
            if (keep.Contains(id)) continue;
            try
            {
                var age = now - File.GetLastWriteTimeUtc(_store.PathFor(id));
                if (age < GracePeriod) continue;
            }
            catch (IOException) { continue; }
            // Re-check just before deleting in case an owner appeared meanwhile.
            if (Referenced().Contains(id)) continue;
            if (_store.Delete(id)) n++;
        }
        return n;
    }

    private sealed class Releaser(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() => Interlocked.Exchange(ref _a, null)?.Invoke();
    }
}
