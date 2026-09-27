using SnagItOpen.Core.Documents;

namespace SnagItOpen.Core.Editing;

/// <summary>One undoable step: immutable before/after snapshots plus a label.</summary>
public sealed record HistoryEntry(string Label, DocumentState Before, DocumentState After);

/// <summary>
/// Linear undo/redo over immutable document snapshots. States share unchanged arrays and records,
/// so entries store metadata only, never raster copies.
/// </summary>
public sealed class History
{
    private readonly LinkedList<HistoryEntry> _undo = new();
    private readonly Stack<HistoryEntry> _redo = new();
    private readonly int _capacity;

    public History(DocumentState initial, int capacity = Limits.MaxUndo)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public DocumentState Current { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Last?.Value.Label;
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>Raised after Current changes for any reason.</summary>
    public event Action<DocumentState>? Changed;

    /// <summary>
    /// Validates and commits <paramref name="next"/>. Returns false (no change) if identical.
    /// Throws <see cref="InvalidDocumentException"/> when invalid, leaving history untouched.
    /// </summary>
    public bool Execute(string label, DocumentState next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(next, Current)) return false;
        DocumentValidator.EnsureValid(next);
        _undo.AddLast(new HistoryEntry(label, Current, next));
        while (_undo.Count > _capacity) _undo.RemoveFirst();
        _redo.Clear();
        Current = next;
        Changed?.Invoke(Current);
        return true;
    }

    /// <summary>Applies a transformation; exceptions from <paramref name="edit"/> leave state untouched.</summary>
    public bool Execute(string label, Func<DocumentState, DocumentState> edit) => Execute(label, edit(Current));

    public bool Undo()
    {
        if (_undo.Last is not { } node) return false;
        _undo.RemoveLast();
        _redo.Push(node.Value);
        Current = node.Value.Before;
        Changed?.Invoke(Current);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var e = _redo.Pop();
        _undo.AddLast(e);
        Current = e.After;
        Changed?.Invoke(Current);
        return true;
    }

    /// <summary>Replaces the whole history (new/open document).</summary>
    public void Reset(DocumentState state)
    {
        _undo.Clear();
        _redo.Clear();
        Current = state ?? throw new ArgumentNullException(nameof(state));
        Changed?.Invoke(Current);
    }

    /// <summary>Every state reachable through undo/redo, including Current (used for asset retention).</summary>
    public IEnumerable<DocumentState> AllStates()
    {
        yield return Current;
        foreach (var e in _undo) { yield return e.Before; yield return e.After; }
        foreach (var e in _redo) { yield return e.Before; yield return e.After; }
    }
}

/// <summary>
/// A transient gesture (drag, slider). Previews do not touch history; Commit records one entry
/// from the baseline; Cancel restores the baseline.
/// </summary>
public sealed class Gesture
{
    private readonly History _history;
    public DocumentState Baseline { get; }
    public DocumentState Preview { get; private set; }
    public bool IsFinished { get; private set; }

    public Gesture(History history)
    {
        _history = history;
        Baseline = history.Current;
        Preview = Baseline;
    }

    public DocumentState Update(Func<DocumentState, DocumentState> fromBaseline)
    {
        if (IsFinished) throw new InvalidOperationException("Gesture finished.");
        Preview = fromBaseline(Baseline);
        return Preview;
    }

    public bool Commit(string label)
    {
        if (IsFinished) return false;
        IsFinished = true;
        if (ReferenceEquals(Preview, Baseline) || !Differs(Preview, Baseline)) return false;
        if (!ReferenceEquals(_history.Current, Baseline))
            throw new InvalidOperationException("History changed during gesture.");
        return _history.Execute(label, Preview);
    }

    public void Cancel()
    {
        IsFinished = true;
        Preview = Baseline;
    }

    private static bool Differs(DocumentState a, DocumentState b) =>
        a.ExportArea != b.ExportArea || a.Layout != b.Layout || a.Background != b.Background || a.AutoCanvas != b.AutoCanvas ||
        !a.Images.SequenceEqual(b.Images) || !a.Annotations.SequenceEqual(b.Annotations) ||
        !a.LayoutOrder.SequenceEqual(b.LayoutOrder) || !a.Assets.SequenceEqual(b.Assets);
}
