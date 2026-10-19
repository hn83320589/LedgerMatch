namespace LedgerMatch.Core.Domain;

/// <summary>Anything persisted through a <see cref="Persistence.ChangeSet"/>. Ids come from <see cref="Persistence.ILedgerStore.ReserveIds"/>.</summary>
public interface IEntity
{
    long Id { get; }
}

/// <summary>An entity that belongs to an accounting month and becomes read-only once that month is closed.</summary>
public interface IPeriodLocked
{
    YearMonth LockPeriod { get; }
}

public sealed record Account(
    long Id,
    string Name,
    long ProfileId,
    Money OpeningBalance,
    DateOnly OpeningDate) : IEntity;

public enum ClassificationSource
{
    None,
    Rule,
    Manual,
}

/// <summary>A bank statement line. Positive amount = deposit (入帳), negative = withdrawal (支出).</summary>
public sealed record BankTransaction(
    long Id,
    long AccountId,
    DateOnly TxnDate,
    YearMonth PostingPeriod,
    Money Amount,
    string Description,
    string? Serial,
    string? Note,
    string DedupKey,
    long? ImportBatchId,
    int? SourceLine,
    long? CategoryId,
    ClassificationSource ClassifiedBy,
    bool BookConfirmed) : IEntity, IPeriodLocked
{
    public bool IsDeposit => Amount.IsPositive;

    public YearMonth LockPeriod => PostingPeriod;
}

public enum ImportKind
{
    Statement,
    Receipt,
}

public sealed record ImportBatch(
    long Id,
    ImportKind Kind,
    long? AccountId,
    string FileName,
    string FileSha256,
    DateTimeOffset ImportedAt,
    int Added,
    int Skipped,
    int Errors,
    int Pending) : IEntity;

public enum PendingStatus
{
    Pending,
    Posted,
    Discarded,
}

/// <summary>An imported line whose date falls in a closed month; it waits for the user to decide.</summary>
public sealed record PendingItem(
    long Id,
    long AccountId,
    DateOnly TxnDate,
    Money Amount,
    string Description,
    string? Serial,
    string? Note,
    string DedupKey,
    long ImportBatchId,
    int SourceLine,
    PendingStatus Status,
    long? PostedTxnId) : IEntity;

public sealed record Period(long Id, YearMonth Month, bool IsClosed, DateTimeOffset? ClosedAt) : IEntity;

public enum PeriodAction
{
    Close,
    Reopen,
}

public sealed record PeriodEvent(long Id, YearMonth Month, PeriodAction Action, string? Reason, DateTimeOffset At) : IEntity;
