using LedgerMatch.Core.Domain;

namespace LedgerMatch.Core.Persistence;

public enum TransactionStatusFilter
{
    All,
    UnmatchedDeposits,
    SuggestedMatches,
    ConfirmedMatches,
    Withdrawals,
    Unclassified,
}

/// <summary>Filter for the transaction list. Null criteria are ignored; amounts compare the signed amount.</summary>
public sealed record TransactionFilter
{
    public long? AccountId { get; init; }

    public YearMonth? Period { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public Money? MinAmount { get; init; }

    public Money? MaxAmount { get; init; }

    /// <summary>Case-insensitive substring of description, note or serial.</summary>
    public string? Keyword { get; init; }

    public TransactionStatusFilter Status { get; init; } = TransactionStatusFilter.All;
}

/// <summary>Keyset cursor: rows after (date, id) in (date, id) order.</summary>
public sealed record TransactionCursor(DateOnly Date, long Id);

/// <summary>A transaction with the state the list shows next to it.</summary>
public sealed record TransactionRow(
    BankTransaction Transaction,
    long? MatchId,
    MatchConfidence? MatchConfidence,
    bool IsSplit,
    string? CategoryName);
