using LedgerMatch.Core.Domain;

namespace LedgerMatch.Core.Persistence;

/// <summary>
/// The single path for every modifying operation: enforces closed periods, builds the audit trail
/// and commits atomically.
/// </summary>
public sealed class LedgerCommandExecutor(ILedgerStore store, TimeProvider time)
{
    public void Execute(ChangeSet changeSet)
    {
        if (changeSet.IsEmpty)
        {
            return;
        }

        PeriodGuard.EnsureWritable(changeSet, store.GetClosedPeriods());

        var at = time.GetUtcNow();
        var audit = changeSet.Changes
            .Select(c => new PendingAuditEntry(
                at,
                changeSet.Operation,
                c.EntityType.Name,
                c.EntityId,
                c.Kind.ToString(),
                c.Before is null ? null : LedgerJson.Serialize(c.Before),
                c.After is null ? null : LedgerJson.Serialize(c.After)))
            .ToList();

        store.Commit(changeSet, audit);
    }
}

public static class PeriodGuard
{
    public static void EnsureWritable(ChangeSet changeSet, IReadOnlySet<YearMonth> closedPeriods)
    {
        if (closedPeriods.Count == 0)
        {
            return;
        }

        foreach (var change in changeSet.Changes)
        {
            EnsureOpen(change.Before, closedPeriods);
            EnsureOpen(change.After, closedPeriods);
        }
    }

    public static void EnsureOpen(YearMonth month, IReadOnlySet<YearMonth> closedPeriods, string detail)
    {
        if (closedPeriods.Contains(month))
        {
            throw new PeriodClosedException(month, detail);
        }
    }

    private static void EnsureOpen(IEntity? entity, IReadOnlySet<YearMonth> closedPeriods)
    {
        if (entity is IPeriodLocked locked)
        {
            EnsureOpen(locked.LockPeriod, closedPeriods, $"{entity.GetType().Name} #{entity.Id}");
        }
    }
}
