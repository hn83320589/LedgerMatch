using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Persistence;

/// <summary>Read side of the ledger used by the application services.</summary>
public interface ILedgerQueries
{
    IReadOnlyList<MappingProfile> GetProfiles();

    MappingProfile? GetProfile(long id);

    IReadOnlyList<Account> GetAccounts();

    Account? GetAccount(long id);

    Setting? GetSetting(string key);

    /// <summary>Dedup keys of the account's transactions whose original date is within the range (inclusive).</summary>
    IReadOnlySet<string> GetTransactionDedupKeys(long accountId, DateOnly from, DateOnly to);

    /// <summary>Dedup keys of the account's pending items (any status) whose date is within the range (inclusive).</summary>
    IReadOnlySet<string> GetPendingDedupKeys(long accountId, DateOnly from, DateOnly to);

    BankTransaction? GetTransaction(long id);

    IReadOnlyList<BankTransaction> GetTransactions(long accountId);

    /// <summary>Deposits (amount &gt; 0) posted in the period that have no match (confirmed or suggested).</summary>
    IReadOnlyList<BankTransaction> GetUnmatchedDeposits(long accountId, YearMonth period);

    Receipt? GetReceipt(long id);

    Receipt? GetReceiptByNumber(string receiptNo);

    IReadOnlyList<Receipt> GetReceipts(long accountId);

    /// <summary>Receipts of the account issued within the range (inclusive) that are not linked to any match.</summary>
    IReadOnlyList<Receipt> GetUnmatchedReceipts(long accountId, DateOnly from, DateOnly to);

    Match? GetMatch(long id);

    Match? GetMatchForDeposit(long depositTxnId);

    Match? GetMatchForReceipt(long receiptId);

    IReadOnlyList<MatchLink> GetMatchLinks(long matchId);

    /// <summary>Matches whose deposit belongs to the account and is posted in the period.</summary>
    IReadOnlyList<Match> GetMatches(long accountId, YearMonth period);

    IReadOnlyList<TransactionRow> FindTransactions(TransactionFilter filter, TransactionCursor? after = null, int take = int.MaxValue);

    int CountTransactions(TransactionFilter filter);

    IReadOnlyList<Category> GetCategories();

    Category? GetCategory(long id);

    /// <summary>True when a transaction, split line or rule refers to the category.</summary>
    bool IsCategoryInUse(long categoryId);

    IReadOnlyList<SplitLine> GetSplitLines(long txnId);

    IReadOnlyList<Rule> GetRules();

    Rule? GetRule(long id);

    IReadOnlyList<Period> GetPeriods();

    Period? GetPeriod(YearMonth month);

    IReadOnlyList<PeriodEvent> GetPeriodEvents();

    PendingItem? GetPendingItem(long id);

    IReadOnlyList<PendingItem> GetPendingItems(PendingStatus? status = null);

    IReadOnlyList<BookEntry> GetBookEntries(long accountId);

    BookEntry? GetBookEntry(long id);

    BookEntry? GetBookEntryClearedBy(long txnId);

    StatementBalance? GetStatementBalance(long accountId, YearMonth month);

    /// <summary>All rows the reconciliation of <paramref name="month"/> depends on.</summary>
    Reporting.ReconciliationData GetReconciliationData(long accountId, YearMonth month);

    /// <summary>Months that have transactions or receipts, ascending.</summary>
    IReadOnlyList<YearMonth> GetActiveMonths();
}
