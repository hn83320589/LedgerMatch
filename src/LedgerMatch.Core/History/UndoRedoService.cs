using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.History;

/// <summary>
/// Session-scoped undo/redo over committed change sets. Each <see cref="Execute"/> call is one step,
/// however many rows it touches. Undo/redo go through the same executor, so they are audited and
/// refused for closed periods or rows changed since.
/// </summary>
public sealed class UndoRedoService
{
    public const int MinimumCapacity = 30;
    public const int DefaultCapacity = 50;

    private readonly LedgerCommandExecutor _executor;
    private readonly LinkedList<ChangeSet> _undo = new();
    private readonly Stack<ChangeSet> _redo = new();

    public UndoRedoService(LedgerCommandExecutor executor, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, MinimumCapacity);
        _executor = executor;
        Capacity = capacity;
    }

    public event EventHandler? Changed;

    public int Capacity { get; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public string? NextUndoOperation => _undo.Last?.Value.Operation;

    public string? NextRedoOperation => _redo.TryPeek(out var next) ? next.Operation : null;

    /// <summary>Commits an undoable operation. Empty change sets are ignored.</summary>
    public void Execute(ChangeSet changeSet)
    {
        if (changeSet.IsEmpty)
        {
            return;
        }

        _executor.Execute(changeSet);
        _undo.AddLast(changeSet);
        if (_undo.Count > Capacity)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        var step = _undo.Last?.Value ?? throw new InvalidOperationException("沒有可以復原的操作。");
        _executor.Execute(step.Invert($"復原：{step.Operation}"));
        _undo.RemoveLast();
        _redo.Push(step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!_redo.TryPeek(out var step))
        {
            throw new InvalidOperationException("沒有可以重做的操作。");
        }

        _executor.Execute(step with { Operation = $"重做：{step.Operation}" });
        _redo.Pop();
        _undo.AddLast(step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops history, e.g. after an operation that cannot be undone changed the same rows.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
