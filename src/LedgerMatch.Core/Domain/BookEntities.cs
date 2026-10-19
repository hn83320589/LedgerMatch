using System.Text.Json.Serialization;

namespace LedgerMatch.Core.Domain;

/// <summary>
/// A book-side entry the bank has not necessarily seen yet, e.g. a cheque written (negative) or other
/// adjustment. Once the bank shows it, it is cleared by linking the bank transaction of the same amount.
/// </summary>
public sealed record BookEntry(
    long Id,
    long AccountId,
    DateOnly EntryDate,
    Money Amount,
    string Description,
    long? ClearedTxnId) : IEntity, IPeriodLocked
{
    [JsonIgnore]
    public YearMonth LockPeriod => YearMonth.Of(EntryDate);
}

/// <summary>Ending balance printed on the bank statement, entered by the user to check the import is complete.</summary>
public sealed record StatementBalance(long Id, long AccountId, YearMonth Month, Money EndingBalance) : IEntity, IPeriodLocked
{
    [JsonIgnore]
    public YearMonth LockPeriod => Month;
}
