using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Classification;

public sealed record SplitShare(long CategoryId, Money Amount);

public sealed record SplitWeight(long CategoryId, decimal Weight);

/// <summary>Manual classification and splitting of any transaction (deposits and withdrawals). Every call is one undo step.</summary>
public sealed class ClassificationService(ILedgerStore store, ILedgerQueries queries, UndoRedoService history)
{
    public int SetCategory(IReadOnlyCollection<long> txnIds, long? categoryId)
    {
        if (categoryId is { } id && queries.GetCategory(id) is null)
        {
            throw new LedgerException($"找不到科目 #{id}。");
        }

        var changes = new ChangeSetBuilder($"手動歸類（{txnIds.Count} 筆）");
        foreach (var txn in txnIds.Distinct().Select(Load))
        {
            if (queries.GetSplitLines(txn.Id).Count > 0)
            {
                throw new LedgerException($"{txn.TxnDate:yyyy-MM-dd} {txn.Description} 已拆分，請改編輯拆分內容。");
            }

            changes.Update(txn, txn with
            {
                CategoryId = categoryId,
                ClassifiedBy = categoryId is null ? ClassificationSource.None : ClassificationSource.Manual,
            });
        }

        var built = changes.Build();
        history.Execute(built);
        return built.Changes.Count;
    }

    /// <summary>Splits by user-entered amounts, which must add up exactly to the transaction amount.</summary>
    public IReadOnlyList<SplitLine> SplitByAmounts(long txnId, IReadOnlyList<SplitShare> shares)
    {
        var txn = Load(txnId);
        if (shares.Count < 2)
        {
            throw new LedgerException("拆分至少需要兩份。");
        }

        var total = shares.Sum(s => s.Amount);
        if (total != txn.Amount)
        {
            throw new LedgerException($"各份合計 {total} 必須等於交易金額 {txn.Amount}（差 {txn.Amount - total}）。");
        }

        if (shares.Any(s => s.Amount == Money.Zero || s.Amount.IsNegative != txn.Amount.IsNegative))
        {
            throw new LedgerException("每一份金額都不可為零，且正負號必須與交易相同。");
        }

        return Replace(txn, shares.Select(s => (s.CategoryId, s.Amount, (decimal?)null)).ToList(), "拆分（金額）");
    }

    /// <summary>Splits proportionally with <see cref="Allocation.ByWeights"/>; shares are cent-exact and sum to the amount.</summary>
    public IReadOnlyList<SplitLine> SplitByWeights(long txnId, IReadOnlyList<SplitWeight> weights)
    {
        var txn = Load(txnId);
        if (weights.Count < 2)
        {
            throw new LedgerException("拆分至少需要兩份。");
        }

        IReadOnlyList<Money> amounts;
        try
        {
            amounts = Allocation.ByWeights(txn.Amount, weights.Select(w => w.Weight).ToList());
        }
        catch (ArgumentException ex)
        {
            throw new LedgerException(ex.Message, ex);
        }

        return Replace(txn, weights.Select((w, i) => (w.CategoryId, amounts[i], (decimal?)w.Weight)).ToList(), "拆分（比例）");
    }

    public void RemoveSplit(long txnId)
    {
        var txn = Load(txnId);
        var changes = new ChangeSetBuilder($"取消拆分 {txn.TxnDate:yyyy-MM-dd} {txn.Description}");
        foreach (var line in queries.GetSplitLines(txnId))
        {
            changes.Delete(line);
        }

        history.Execute(changes.Build());
    }

    private List<SplitLine> Replace(BankTransaction txn, List<(long CategoryId, Money Amount, decimal? Weight)> shares, string operation)
    {
        foreach (var categoryId in shares.Select(s => s.CategoryId).Distinct())
        {
            _ = queries.GetCategory(categoryId) ?? throw new LedgerException($"找不到科目 #{categoryId}。");
        }

        var changes = new ChangeSetBuilder($"{operation} {txn.TxnDate:yyyy-MM-dd} {txn.Description}");
        foreach (var old in queries.GetSplitLines(txn.Id))
        {
            changes.Delete(old);
        }

        var id = store.ReserveIds(shares.Count);
        var lines = shares.Select((s, i) => new SplitLine(id + i, txn.Id, i + 1, s.CategoryId, s.Amount, s.Weight, txn.PostingPeriod)).ToList();
        foreach (var line in lines)
        {
            changes.Insert(line);
        }

        history.Execute(changes.Build());
        return lines;
    }

    private BankTransaction Load(long txnId) => queries.GetTransaction(txnId) ?? throw new LedgerException($"找不到交易 #{txnId}。");
}
