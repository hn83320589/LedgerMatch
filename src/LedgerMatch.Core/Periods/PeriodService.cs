using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Periods;

/// <summary>
/// Month closing and the pending queue for late transactions. Closing and reopening are audited but are
/// deliberately not undo steps: reopening must always go through <see cref="Reopen"/> with a reason.
/// </summary>
public sealed class PeriodService(ILedgerStore store, ILedgerQueries queries, LedgerCommandExecutor executor, UndoRedoService history, TimeProvider time)
{
    public bool IsClosed(YearMonth month) => store.GetClosedPeriods().Contains(month);

    public void Close(YearMonth month)
    {
        var existing = queries.GetPeriod(month);
        if (existing?.IsClosed == true)
        {
            throw new LedgerException($"{month} 已經結帳。");
        }

        var now = time.GetUtcNow();
        var changes = new ChangeSetBuilder($"結帳 {month}");
        if (existing is null)
        {
            changes.Insert(new Period(store.ReserveIds(1), month, true, now));
        }
        else
        {
            changes.Update(existing, existing with { IsClosed = true, ClosedAt = now });
        }

        changes.Insert(new PeriodEvent(store.ReserveIds(1), month, PeriodAction.Close, null, now));
        executor.Execute(changes.Build());
    }

    public void Reopen(YearMonth month, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new LedgerException("重新開帳必須輸入理由。");
        }

        var existing = queries.GetPeriod(month);
        if (existing?.IsClosed != true)
        {
            throw new LedgerException($"{month} 尚未結帳。");
        }

        var now = time.GetUtcNow();
        executor.Execute(new ChangeSetBuilder($"重新開帳 {month}：{reason.Trim()}")
            .Update(existing, existing with { IsClosed = false, ClosedAt = null })
            .Insert(new PeriodEvent(store.ReserveIds(1), month, PeriodAction.Reopen, reason.Trim(), now))
            .Build());
    }

    /// <summary>The first month after <paramref name="month"/> that is not closed.</summary>
    public YearMonth NextOpenMonthAfter(YearMonth month)
    {
        var closed = store.GetClosedPeriods();
        var candidate = month.Next();
        while (closed.Contains(candidate))
        {
            candidate = candidate.Next();
        }

        return candidate;
    }

    /// <summary>
    /// Books pending items into the first open month after their own month, keeping the original date.
    /// One undo step for the whole selection.
    /// </summary>
    public IReadOnlyList<BankTransaction> PostToNextOpenMonth(IReadOnlyCollection<long> pendingIds)
    {
        var items = LoadPending(pendingIds);
        var id = store.ReserveIds(Math.Max(1, items.Count));
        var changes = new ChangeSetBuilder($"待處理改列入帳（{items.Count} 筆）");
        var posted = new List<BankTransaction>();
        foreach (var item in items)
        {
            var txn = new BankTransaction(
                id++, item.AccountId, item.TxnDate, NextOpenMonthAfter(YearMonth.Of(item.TxnDate)), item.Amount, item.Description, item.Serial,
                item.Note, item.DedupKey, item.ImportBatchId, item.SourceLine, null, ClassificationSource.None, item.Amount.IsNegative);
            changes.Insert(txn);
            changes.Update(item, item with { Status = PendingStatus.Posted, PostedTxnId = txn.Id });
            posted.Add(txn);
        }

        history.Execute(changes.Build());
        return posted;
    }

    /// <summary>Marks pending items as discarded; they stay recorded so re-importing the file does not bring them back.</summary>
    public void Discard(IReadOnlyCollection<long> pendingIds)
    {
        var items = LoadPending(pendingIds);
        var changes = new ChangeSetBuilder($"捨棄待處理項目（{items.Count} 筆）");
        foreach (var item in items)
        {
            changes.Update(item, item with { Status = PendingStatus.Discarded });
        }

        history.Execute(changes.Build());
    }

    private List<PendingItem> LoadPending(IReadOnlyCollection<long> ids)
    {
        var items = ids.Distinct().Select(id => queries.GetPendingItem(id) ?? throw new LedgerException($"找不到待處理項目 #{id}。")).ToList();
        if (items.Any(i => i.Status != PendingStatus.Pending))
        {
            throw new LedgerException("只能處理狀態為「待處理」的項目。");
        }

        return items;
    }
}
