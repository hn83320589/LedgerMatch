using LedgerMatch.Core.Domain;

namespace LedgerMatch.Core.Reporting;

public sealed record MatchWithLinks(Match Match, IReadOnlyList<long> ReceiptIds);

/// <summary>Everything the calculator needs for one account and month.</summary>
public sealed record ReconciliationData(
    Account Account,
    YearMonth Month,
    IReadOnlyList<BankTransaction> Transactions,
    IReadOnlyList<Receipt> Receipts,
    IReadOnlyList<MatchWithLinks> Matches,
    IReadOnlyList<BookEntry> BookEntries,
    StatementBalance? Statement);

public sealed record ReconciliationItem(DateOnly Date, string Reference, string Description, Money Amount, string? Note);

public sealed record ReconciliationSection(string Title, IReadOnlyList<ReconciliationItem> Items)
{
    public Money Total => Items.Sum(i => i.Amount);
}

/// <summary>
/// Bank reconciliation in the two-sided format (README "餘額調節表"):
/// <code>
/// 調整後銀行餘額 = 銀行對帳單期末餘額 + 在途存款 + 其他帳面已記銀行未記 − 未兌現項目
/// 調整後帳面餘額 = 帳面期末餘額 + 銀行已收帳面未記 − 銀行已付帳面未記
/// 差異 = 調整後銀行餘額 − 調整後帳面餘額（應為 0）
/// </code>
/// </summary>
public sealed record ReconciliationReport(
    string AccountName,
    YearMonth Month,
    DateTimeOffset GeneratedAt,
    Money ComputedBankBalance,
    Money? StatementBalance,
    ReconciliationSection DepositsInTransit,
    ReconciliationSection OtherBookItems,
    ReconciliationSection OutstandingItems,
    Money BookBalance,
    ReconciliationSection BankOnlyDeposits,
    ReconciliationSection BankOnlyWithdrawals,
    IReadOnlyList<string> DifferenceNotes)
{
    /// <summary>The statement figure when the user entered one, otherwise the balance computed from imported lines.</summary>
    public Money BankBalance => StatementBalance ?? ComputedBankBalance;

    /// <summary>Outstanding items are stored negative; they reduce the bank side by their magnitude.</summary>
    public Money AdjustedBankBalance => BankBalance + DepositsInTransit.Total + OtherBookItems.Total + OutstandingItems.Total;

    public Money AdjustedBookBalance => BookBalance + BankOnlyDeposits.Total + BankOnlyWithdrawals.Total;

    public Money Difference => AdjustedBankBalance - AdjustedBookBalance;

    public bool IsBalanced => Difference == Money.Zero;
}

public static class ReconciliationCalculator
{
    /// <remarks>
    /// Book balance = opening + receipts issued ≤ month end + receipts of effective matches issued later
    /// + book entries dated ≤ month end − fees of effective matches + bank lines confirmed into the book
    /// (withdrawals on import, deposits confirmed directly) that are not already represented by a cleared book entry.
    /// A confirmed match is effective at month end when its deposit is posted in or before the month: the cash has
    /// arrived, so all of its receipts count in that month's book even if one was issued after month end.
    /// Unconfirmed suggestions never count.
    /// </remarks>
    public static ReconciliationReport Compute(ReconciliationData data, DateTimeOffset generatedAt)
    {
        var month = data.Month;
        var monthEnd = month.LastDay;
        var txns = data.Transactions.Where(t => t.AccountId == data.Account.Id && t.PostingPeriod <= month).ToList();
        var txnIds = txns.Select(t => t.Id).ToHashSet();

        var effective = data.Matches
            .Where(m => m.Match.Confidence == MatchConfidence.Confirmed
                && m.Match.Period <= month
                && txnIds.Contains(m.Match.DepositTxnId))
            .ToList();
        var clearedReceipts = effective.SelectMany(m => m.ReceiptIds).ToHashSet();
        var receipts = data.Receipts
            .Where(r => r.AccountId == data.Account.Id && (r.IssueDate <= monthEnd || clearedReceipts.Contains(r.Id)))
            .ToList();
        var matchedDeposits = effective.Select(m => m.Match.DepositTxnId).ToHashSet();
        var suggested = data.Matches.Where(m => m.Match.Confidence == MatchConfidence.Suggested).Select(m => m.Match.DepositTxnId).ToHashSet();

        var entries = data.BookEntries.Where(e => e.AccountId == data.Account.Id && e.EntryDate <= monthEnd).ToList();
        var coveredTxns = entries.Where(e => e.ClearedTxnId is { } id && txnIds.Contains(id)).Select(e => e.ClearedTxnId!.Value).ToHashSet();

        var opening = data.Account.OpeningBalance;
        var bank = opening + txns.Sum(t => t.Amount);
        var book = opening
            + receipts.Sum(r => r.Amount)
            + entries.Sum(e => e.Amount)
            - effective.Sum(m => m.Match.Fee)
            + txns.Where(t => t.BookConfirmed && !coveredTxns.Contains(t.Id)).Sum(t => t.Amount);

        var inTransit = receipts
            .Where(r => !clearedReceipts.Contains(r.Id))
            .OrderBy(r => r.IssueDate).ThenBy(r => r.ReceiptNo, StringComparer.Ordinal)
            .Select(r => new ReconciliationItem(r.IssueDate, r.ReceiptNo, r.Customer, r.Amount, r.Note))
            .ToList();

        var unclearedEntries = entries
            .Where(e => e.ClearedTxnId is not { } id || !txnIds.Contains(id))
            .OrderBy(e => e.EntryDate).ThenBy(e => e.Id)
            .ToList();

        var bankOnly = txns
            .Where(t => !t.BookConfirmed && !coveredTxns.Contains(t.Id) && !matchedDeposits.Contains(t.Id))
            .OrderBy(t => t.TxnDate).ThenBy(t => t.DedupKey, StringComparer.Ordinal)
            .ToList();

        var notes = new List<string>();
        if (data.Statement is { } statement && statement.EndingBalance != bank)
        {
            notes.Add($"對帳單期末餘額 {statement.EndingBalance} 與已匯入交易計算的 {bank} 相差 {statement.EndingBalance - bank}，可能有交易尚未匯入或匯入有誤。");
        }

        return new ReconciliationReport(
            data.Account.Name,
            month,
            generatedAt,
            bank,
            data.Statement?.EndingBalance,
            new ReconciliationSection("在途存款（收款單已記、銀行尚未入帳）", inTransit),
            new ReconciliationSection("其他帳面已記、銀行未記", unclearedEntries.Where(e => e.Amount.IsPositive).Select(EntryItem).ToList()),
            new ReconciliationSection("未兌現項目（帳面已付、銀行尚未扣款）", unclearedEntries.Where(e => e.Amount.IsNegative).Select(EntryItem).ToList()),
            book,
            new ReconciliationSection("銀行已入帳、帳面未記（未配對入帳）", bankOnly.Where(t => t.IsDeposit).Select(t => TxnItem(t, suggested)).ToList()),
            new ReconciliationSection("銀行已扣款、帳面未記", bankOnly.Where(t => !t.IsDeposit).Select(t => TxnItem(t, suggested)).ToList()),
            notes);
    }

    private static ReconciliationItem EntryItem(BookEntry e) => new(e.EntryDate, $"帳面#{e.Id}", e.Description, e.Amount, null);

    private static ReconciliationItem TxnItem(BankTransaction t, HashSet<long> suggested) =>
        new(t.TxnDate, t.Serial ?? $"列{t.SourceLine}", t.Description, t.Amount, suggested.Contains(t.Id) ? "有建議配對，尚未確認" : t.Note);
}
