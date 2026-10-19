using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Tests.Persistence;

/// <summary>Minimal store for testing the executor and undo logic without SQLite.</summary>
internal sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly Dictionary<(Type, long), IEntity> _rows = [];
    private readonly List<AuditEntry> _audit = [];
    private long _nextId = 1;

    public HashSet<YearMonth> Closed { get; } = [];

    public IReadOnlyCollection<IEntity> Rows => _rows.Values;

    public T Get<T>(long id) where T : IEntity => (T)_rows[(typeof(T), id)];

    public bool Exists<T>(long id) => _rows.ContainsKey((typeof(T), id));

    public long ReserveIds(int count)
    {
        var first = _nextId;
        _nextId += count;
        return first;
    }

    public IReadOnlySet<YearMonth> GetClosedPeriods() => Closed;

    public void Commit(ChangeSet changes, IReadOnlyList<PendingAuditEntry> audit)
    {
        var staged = new Dictionary<(Type, long), IEntity>(_rows);
        foreach (var c in changes.Changes)
        {
            var key = (c.EntityType, c.EntityId);
            staged.TryGetValue(key, out var current);
            if (!Equals(current, c.Before))
            {
                throw new ConcurrencyConflictException($"{key} changed");
            }

            if (c.After is null)
            {
                staged.Remove(key);
            }
            else
            {
                staged[key] = c.After;
            }
        }

        _rows.Clear();
        foreach (var (k, v) in staged)
        {
            _rows[k] = v;
        }

        var prev = _audit.Count == 0 ? AuditHasher.GenesisHash : _audit[^1].Hash;
        foreach (var a in audit)
        {
            var seq = _audit.Count + 1L;
            var hash = AuditHasher.Compute(prev, seq, a);
            _audit.Add(new AuditEntry(seq, a.At, a.Operation, a.EntityType, a.EntityId, a.Action, a.BeforeJson, a.AfterJson, prev, hash));
            prev = hash;
        }
    }

    public IReadOnlyList<AuditEntry> GetAuditEntries(long afterSequence, int take) =>
        _audit.Where(a => a.Sequence > afterSequence).Take(take).ToList();
}
