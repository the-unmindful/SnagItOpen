using System.Text.Json;
using SnagItOpen.Core.Documents;

namespace SnagItOpen.Storage.Recovery;

/// <summary>An autosaved draft. Assets live in the shared asset store (retained while a draft references them).</summary>
public sealed record RecoverySnapshot
{
    public int Format { get; init; } = 1;
    public Guid DocumentId { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.Now;
    public string? ProjectPath { get; init; }
    public DocumentState Document { get; init; } = DocumentState.CreateEmpty();
}

public sealed record RecoveryCandidate(string Path, RecoverySnapshot Snapshot);

/// <summary>
/// Versioned autosave snapshots: recovery/&lt;docId&gt;/&lt;revision&gt;.json. Writes are atomic and serialized per
/// document; a completion with an older revision than the newest on disk is discarded.
/// </summary>
public sealed class RecoveryStore
{
    private const int KeepPerDocument = 2;
    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RecoveryStore(string root)
    {
        _root = System.IO.Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    private string DocDir(Guid id) => System.IO.Path.Combine(_root, id.ToString("N"));
    private static string FileFor(string dir, long rev) => System.IO.Path.Combine(dir, rev.ToString("D12") + ".json");

    /// <summary>Writes a snapshot. Returns false when a newer revision already exists (stale completion).</summary>
    public async Task<bool> SaveAsync(RecoverySnapshot snap, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = DocDir(snap.DocumentId);
            Directory.CreateDirectory(dir);
            if (Revisions(dir).FirstOrDefault() is var newest && newest >= snap.Revision && newest > 0) return false;
            await AtomicFile.WriteJsonAsync(FileFor(dir, snap.Revision), snap, Json.Options, ct).ConfigureAwait(false);
            foreach (var old in Revisions(dir).Skip(KeepPerDocument)) AtomicFile.TryDelete(FileFor(dir, old));
            return true;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Newest valid snapshot per document; corrupt newest falls back to an older revision.</summary>
    public IReadOnlyList<RecoveryCandidate> FindCandidates()
    {
        var list = new List<RecoveryCandidate>();
        if (!Directory.Exists(_root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            foreach (var rev in Revisions(dir))
            {
                var path = FileFor(dir, rev);
                if (TryRead(path) is { } s) { list.Add(new RecoveryCandidate(path, s)); break; }
            }
        }
        return list.OrderByDescending(c => c.Snapshot.SavedAt).ToList();
    }

    /// <summary>Removes snapshots of a document with revision ≤ <paramref name="upToRevision"/> (all if null).</summary>
    public void Discard(Guid documentId, long? upToRevision = null)
    {
        var dir = DocDir(documentId);
        if (!Directory.Exists(dir)) return;
        foreach (var rev in Revisions(dir))
            if (upToRevision is null || rev <= upToRevision) AtomicFile.TryDelete(FileFor(dir, rev));
        foreach (var tmp in Directory.EnumerateFiles(dir, "*.tmp")) AtomicFile.TryDelete(tmp);
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (IOException) { }
    }

    /// <summary>Asset IDs referenced by any stored snapshot (owners for retention).</summary>
    public ISet<string> ReferencedAssets()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_root)) return set;
        foreach (var f in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
            if (TryRead(f) is { } s) foreach (var a in s.Document.Assets) set.Add(a.Id);
        return set;
    }

    private static IEnumerable<long> Revisions(string dir) =>
        Directory.EnumerateFiles(dir, "*.json")
            .Select(f => long.TryParse(System.IO.Path.GetFileNameWithoutExtension(f), out var r) ? r : -1)
            .Where(r => r >= 0).OrderByDescending(r => r).ToList();

    private static RecoverySnapshot? TryRead(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 32L * 1024 * 1024) return null;
            var s = JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllBytes(path), Json.Options);
            if (s?.Document is null) return null;
            var doc = Projects.ProjectStore.Migrate(s.Document);
            return DocumentValidator.IsValid(doc) ? s with { Document = doc } : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Quiet-period autosave: waits <see cref="QuietPeriod"/> after the last change and never saves more often
/// than <see cref="MinInterval"/>. Revisions are monotonic; saves are serialized.
/// </summary>
public sealed class AutosaveScheduler : IDisposable
{
    private readonly RecoveryStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private DocumentState? _pending;
    private string? _pendingPath;
    private long _revision;
    private DateTimeOffset _lastChange, _lastSave = DateTimeOffset.MinValue;
    private Task _saving = Task.CompletedTask;
    private bool _disposed;

    public AutosaveScheduler(RecoveryStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _now = clock ?? (() => DateTimeOffset.Now);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(10);
    public long LastSavedRevision { get; private set; }
    public event Action<Exception>? Failed;

    /// <summary>Records a dirty document state. Returns its revision.</summary>
    public long NotifyChanged(DocumentState doc, string? projectPath)
    {
        lock (_gate)
        {
            if (_disposed) return _revision;
            _pending = doc;
            _pendingPath = projectPath;
            _lastChange = _now();
            _revision++;
            _timer.Change(QuietPeriod, Timeout.InfiniteTimeSpan);
            return _revision;
        }
    }

    /// <summary>Document saved explicitly or closed: clear pending draft and older snapshots.</summary>
    public void MarkClean(Guid documentId)
    {
        long rev;
        lock (_gate) { _pending = null; rev = _revision; }
        _ = _saving.ContinueWith(_ => _store.Discard(documentId, rev), TaskScheduler.Default);
    }

    /// <summary>Evaluates timing; public for deterministic tests.</summary>
    public Task Tick()
    {
        DocumentState doc; string? path; long rev;
        lock (_gate)
        {
            if (_disposed || _pending is null) return Task.CompletedTask;
            var now = _now();
            var quietLeft = QuietPeriod - (now - _lastChange);
            var intervalLeft = MinInterval - (now - _lastSave);
            var wait = quietLeft > intervalLeft ? quietLeft : intervalLeft;
            if (wait > TimeSpan.Zero) { _timer.Change(wait, Timeout.InfiniteTimeSpan); return Task.CompletedTask; }
            doc = _pending; path = _pendingPath; rev = _revision;
            _pending = null;
            _lastSave = now;
            var prev = _saving;
            _saving = prev.ContinueWith(async _ =>
            {
                try
                {
                    if (await _store.SaveAsync(new RecoverySnapshot { DocumentId = doc.Id, Revision = rev, Document = doc, ProjectPath = path }).ConfigureAwait(false))
                        LastSavedRevision = rev;
                }
                catch (Exception ex) { Failed?.Invoke(ex); }
            }, TaskScheduler.Default).Unwrap();
            return _saving;
        }
    }

    public Task FlushAsync()
    {
        lock (_gate) { _lastChange = DateTimeOffset.MinValue; _lastSave = DateTimeOffset.MinValue; }
        return Tick();
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        _timer.Dispose();
    }
}
