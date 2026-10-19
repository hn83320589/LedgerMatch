using System.Text.Json.Serialization;

namespace LedgerMatch.Core.Domain;

/// <summary>A user-defined account category (科目). The built-in fee category cannot be deleted.</summary>
public sealed record Category(long Id, string Name, bool IsSystem) : IEntity
{
    public const long FeeCategoryId = 1;
}

/// <summary>One share of a split transaction. <see cref="Weight"/> is set when the split was proportional.</summary>
public sealed record SplitLine(
    long Id,
    long TxnId,
    int Seq,
    long CategoryId,
    Money Amount,
    decimal? Weight,
    YearMonth Period) : IEntity, IPeriodLocked
{
    [JsonIgnore]
    public YearMonth LockPeriod => Period;
}

/// <summary>
/// Classification rule. All set conditions must hold (AND). Amount bounds are inclusive and compare the
/// signed amount (withdrawals are negative). Lower <see cref="Priority"/> runs first; first hit wins.
/// </summary>
public sealed record Rule(
    long Id,
    string Name,
    int Priority,
    string? DescriptionContains,
    string? DescriptionRegex,
    Money? MinAmount,
    Money? MaxAmount,
    long? AccountId,
    long CategoryId,
    bool Enabled) : IEntity;
