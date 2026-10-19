namespace LedgerMatch.Core.Domain;

/// <summary>A receipt (收款單) the owner issued; the book-side record of money expected in the given account.</summary>
public sealed record Receipt(
    long Id,
    long AccountId,
    DateOnly IssueDate,
    string Customer,
    string ReceiptNo,
    Money Amount,
    string? Note,
    long? ImportBatchId) : IEntity, IPeriodLocked
{
    public YearMonth LockPeriod => YearMonth.Of(IssueDate);
}

public enum MatchKind
{
    Exact,
    OneToMany,
    WithFee,
    Manual,
}

public enum MatchConfidence
{
    Confirmed,
    Suggested,
}

/// <summary>
/// One deposit paired with one or more receipts. <see cref="Fee"/> is the separate fee entry
/// (receipts total − deposit), zero when amounts are equal. Belongs to the deposit's posting period.
/// </summary>
public sealed record Match(
    long Id,
    long DepositTxnId,
    YearMonth Period,
    MatchKind Kind,
    MatchConfidence Confidence,
    Money Fee,
    string? Note) : IEntity, IPeriodLocked
{
    public YearMonth LockPeriod => Period;
}

public sealed record MatchLink(long Id, long MatchId, long ReceiptId, YearMonth Period) : IEntity, IPeriodLocked
{
    public YearMonth LockPeriod => Period;
}
