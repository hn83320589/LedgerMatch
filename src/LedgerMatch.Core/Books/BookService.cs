using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Books;

/// <summary>
/// Book-side items that are not receipts: manual entries (e.g. cheques) and their clearing, deposits taken
/// into the book without a receipt (e.g. interest), and the statement ending balance. All are undo steps.
/// </summary>
public sealed class BookService(ILedgerStore store, ILedgerQueries queries, UndoRedoService history)
{
    public BookEntry AddEntry(long accountId, DateOnly date, Money amount, string description)
    {
        _ = queries.GetAccount(accountId) ?? throw new LedgerException($"找不到帳戶 #{accountId}。");
        if (amount == Money.Zero)
        {
            throw new LedgerException("帳面分錄金額不可為零。");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new LedgerException("請輸入帳面分錄說明。");
        }

        var entry = new BookEntry(store.ReserveIds(1), accountId, date, amount, description.Trim(), null);
        history.Execute(new ChangeSetBuilder($"新增帳面分錄「{entry.Description}」").Insert(entry).Build());
        return entry;
    }

    public void DeleteEntry(long entryId)
    {
        var entry = LoadEntry(entryId);
        if (entry.ClearedTxnId is not null)
        {
            throw new LedgerException("已兌現的帳面分錄請先取消兌現再刪除。");
        }

        history.Execute(new ChangeSetBuilder($"刪除帳面分錄「{entry.Description}」").Delete(entry).Build());
    }

    /// <summary>Marks the entry as cleared by a bank line of the same account and exactly the same amount.</summary>
    public void ClearEntry(long entryId, long txnId)
    {
        var entry = LoadEntry(entryId);
        var txn = queries.GetTransaction(txnId) ?? throw new LedgerException($"找不到交易 #{txnId}。");
        if (txn.AccountId != entry.AccountId || txn.Amount != entry.Amount)
        {
            throw new LedgerException($"兌現的銀行交易必須是同一帳戶、金額相同（{entry.Amount}）。");
        }

        if (queries.GetBookEntryClearedBy(txnId) is not null || queries.GetMatchForDeposit(txnId) is not null)
        {
            throw new LedgerException("這筆銀行交易已對應其他帳面分錄或收款單。");
        }

        history.Execute(new ChangeSetBuilder($"帳面分錄兌現「{entry.Description}」").Update(entry, entry with { ClearedTxnId = txnId }).Build());
    }

    public void UnclearEntry(long entryId)
    {
        var entry = LoadEntry(entryId);
        history.Execute(new ChangeSetBuilder($"取消兌現「{entry.Description}」").Update(entry, entry with { ClearedTxnId = null }).Build());
    }

    /// <summary>Takes deposits without receipts (e.g. interest) into the book, or reverses that.</summary>
    public int SetDepositsBooked(IReadOnlyCollection<long> txnIds, bool booked)
    {
        var changes = new ChangeSetBuilder(booked ? $"入帳直接列入帳面（{txnIds.Count} 筆）" : $"取消直接列入帳面（{txnIds.Count} 筆）");
        foreach (var id in txnIds.Distinct())
        {
            var txn = queries.GetTransaction(id) ?? throw new LedgerException($"找不到交易 #{id}。");
            if (!txn.IsDeposit)
            {
                throw new LedgerException("只有入帳可以直接列入帳面；支出匯入時即已列入。");
            }

            if (booked && queries.GetMatchForDeposit(id) is not null)
            {
                throw new LedgerException($"{txn.TxnDate:yyyy-MM-dd} {txn.Description} 已有配對，請先解除。");
            }

            changes.Update(txn, txn with { BookConfirmed = booked });
        }

        var built = changes.Build();
        history.Execute(built);
        return built.Changes.Count;
    }

    /// <summary>Records the ending balance printed on the statement; null removes it.</summary>
    public void SetStatementBalance(long accountId, YearMonth month, Money? endingBalance)
    {
        var existing = queries.GetStatementBalance(accountId, month);
        var changes = new ChangeSetBuilder($"對帳單期末餘額 {month}");
        if (endingBalance is null)
        {
            if (existing is not null)
            {
                changes.Delete(existing);
            }
        }
        else if (existing is null)
        {
            changes.Insert(new StatementBalance(store.ReserveIds(1), accountId, month, endingBalance.Value));
        }
        else
        {
            changes.Update(existing, existing with { EndingBalance = endingBalance.Value });
        }

        history.Execute(changes.Build());
    }

    private BookEntry LoadEntry(long id) => queries.GetBookEntry(id) ?? throw new LedgerException($"找不到帳面分錄 #{id}。");
}
