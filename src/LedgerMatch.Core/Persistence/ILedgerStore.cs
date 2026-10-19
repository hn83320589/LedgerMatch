namespace LedgerMatch.Core.Persistence;

/// <summary>Write side of the ledger. Implementations must apply a <see cref="ChangeSet"/> atomically.</summary>
public interface ILedgerStore
{
    /// <summary>Reserves <paramref name="count"/> unique ids and returns the first; ids are never reused.</summary>
    long ReserveIds(int count);

    IReadOnlySet<YearMonth> GetClosedPeriods();

    /// <summary>
    /// Applies all changes and appends the audit entries in one transaction.
    /// Updates and deletes only succeed when the stored row still equals the Before image;
    /// otherwise <see cref="ConcurrencyConflictException"/> is thrown and nothing is written.
    /// </summary>
    void Commit(ChangeSet changes, IReadOnlyList<PendingAuditEntry> audit);

    IReadOnlyList<AuditEntry> GetAuditEntries(long afterSequence, int take);
}

/// <summary>Base for business rule violations whose message can be shown to the user as-is.</summary>
public class LedgerException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class PeriodClosedException(YearMonth month, string detail)
    : LedgerException($"{month} 已結帳，不能修改：{detail}")
{
    public YearMonth Month { get; } = month;
}

public sealed class ConcurrencyConflictException(string message, Exception? inner = null) : LedgerException(message, inner);
