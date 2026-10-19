using LedgerMatch.Core.Domain;

namespace LedgerMatch.Core.Persistence;

public enum ChangeKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>A row-level change with full before/after images. Insert has no Before, Delete has no After.</summary>
public sealed record EntityChange
{
    private EntityChange(IEntity? before, IEntity? after)
    {
        if (before is null && after is null)
        {
            throw new ArgumentException("A change needs a before or an after image.");
        }

        if (before is not null && after is not null && (before.GetType() != after.GetType() || before.Id != after.Id))
        {
            throw new ArgumentException("Before and after must be the same entity.");
        }

        Before = before;
        After = after;
    }

    public IEntity? Before { get; }

    public IEntity? After { get; }

    public ChangeKind Kind => (Before, After) switch
    {
        (null, _) => ChangeKind.Insert,
        (_, null) => ChangeKind.Delete,
        _ => ChangeKind.Update,
    };

    public Type EntityType => (After ?? Before)!.GetType();

    public long EntityId => (After ?? Before)!.Id;

    public static EntityChange Insert(IEntity entity) => new(null, entity);

    public static EntityChange Update(IEntity before, IEntity after) => new(before, after);

    public static EntityChange Delete(IEntity entity) => new(entity, null);

    public EntityChange Invert() => new(After, Before);
}

/// <summary>
/// One user-visible operation (possibly a batch). It is committed atomically, written to the audit log,
/// and is the unit of undo/redo.
/// </summary>
public sealed record ChangeSet(string Operation, IReadOnlyList<EntityChange> Changes)
{
    public bool IsEmpty => Changes.Count == 0;

    /// <summary>Reverses every change, in reverse order, so dependent rows are removed before their parents.</summary>
    public ChangeSet Invert(string operation) =>
        new(operation, Changes.Reverse().Select(c => c.Invert()).ToList());
}

public sealed class ChangeSetBuilder(string operation)
{
    private readonly List<EntityChange> _changes = [];

    public int Count => _changes.Count;

    public ChangeSetBuilder Insert(IEntity entity)
    {
        _changes.Add(EntityChange.Insert(entity));
        return this;
    }

    public ChangeSetBuilder Update(IEntity before, IEntity after)
    {
        if (!Equals(before, after))
        {
            _changes.Add(EntityChange.Update(before, after));
        }

        return this;
    }

    public ChangeSetBuilder Delete(IEntity entity)
    {
        _changes.Add(EntityChange.Delete(entity));
        return this;
    }

    public ChangeSet Build() => new(operation, _changes.ToList());
}
