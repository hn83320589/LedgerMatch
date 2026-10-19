using LedgerMatch.Core;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Data.Tests.Classification;

public class ClassificationTests : IDisposable
{
    private static readonly YearMonth March = new(2026, 3);
    private readonly TestLedger _ledger = new();
    private readonly Account _account;
    private readonly Category _utilities;
    private readonly Category _rent;
    private readonly Category _supplies;

    public ClassificationTests()
    {
        _account = _ledger.BankA();
        _ledger.ImportUtf8(_account,
            "交易日期,摘要,金額,交易序號,備註\n" +
            "2026/03/01,台電電費,-1500,T1,\n" +
            "2026/03/02,自來水費,-300,T2,\n" +
            "2026/03/03,房租,-20000,T3,\n" +
            "2026/03/04,麵粉 原料,-10000,T4,\n" +
            "2026/03/05,現金存入,5000,T5,\n" +
            "2026/02/27,台電電費,-1400,T0,\n");
        _utilities = _ledger.Categories.Add("水電費");
        _rent = _ledger.Categories.Add("租金");
        _supplies = _ledger.Categories.Add("原料");
        _ledger.History.Clear();
    }

    public void Dispose() => _ledger.Dispose();

    private BankTransaction Txn(string serial) => _ledger.Store.GetTransactions(_account.Id).Single(t => t.Serial == serial);

    private int AuditCount => _ledger.Store.GetAuditEntries(0, 100000).Count;

    private Rule NewRule(string name, long categoryId, string? contains = null, string? regex = null, long? max = null) =>
        _ledger.Rules.Save(new Rule(0, name, 0, contains, regex, null, max is null ? null : Money.FromCents(max.Value), null, categoryId, true));

    // ---------- categories ----------

    [Fact]
    public void Categories_RenameDeleteAndProtection()
    {
        Assert.Throws<LedgerException>(() => _ledger.Categories.Add("租金"));
        _ledger.Categories.Rename(_rent.Id, "店租");
        Assert.Equal("店租", _ledger.Store.GetCategory(_rent.Id)!.Name);

        Assert.Throws<LedgerException>(() => _ledger.Categories.Delete(Category.FeeCategoryId));
        _ledger.Classification.SetCategory([Txn("T3").Id], _rent.Id);
        Assert.Throws<LedgerException>(() => _ledger.Categories.Delete(_rent.Id));
        _ledger.Categories.Delete(_supplies.Id);
        Assert.Null(_ledger.Store.GetCategory(_supplies.Id));
    }

    // ---------- manual classification & split ----------

    [Fact]
    public void ManualClassification_BatchIsOneUndoStep()
    {
        _ledger.Classification.SetCategory([Txn("T1").Id, Txn("T2").Id], _utilities.Id);
        Assert.All(new[] { Txn("T1"), Txn("T2") }, t => Assert.Equal((_utilities.Id, ClassificationSource.Manual), (t.CategoryId!.Value, t.ClassifiedBy)));

        _ledger.History.Undo();
        Assert.All(new[] { Txn("T1"), Txn("T2") }, t => Assert.Null(t.CategoryId));
    }

    [Fact]
    public void SplitByWeights_IsCentExact()
    {
        var lines = _ledger.Classification.SplitByWeights(Txn("T4").Id,
            [new SplitWeight(_supplies.Id, 1), new SplitWeight(_rent.Id, 1), new SplitWeight(_utilities.Id, 1)]);

        Assert.Equal([-333334L, -333333, -333333], lines.Select(l => l.Amount.Cents));
        Assert.Equal(Txn("T4").Amount, _ledger.Store.GetSplitLines(Txn("T4").Id).Sum(l => l.Amount));
        Assert.Equal(1m, _ledger.Store.GetSplitLines(Txn("T4").Id)[0].Weight);
    }

    [Fact]
    public void SplitByAmounts_MustAddUp()
    {
        var ex = Assert.Throws<LedgerException>(() => _ledger.Classification.SplitByAmounts(Txn("T4").Id,
            [new SplitShare(_supplies.Id, Money.FromCents(-600000)), new SplitShare(_rent.Id, Money.FromCents(-300000))]));
        Assert.Contains("差", ex.Message);

        _ledger.Classification.SplitByAmounts(Txn("T4").Id,
            [new SplitShare(_supplies.Id, Money.FromCents(-700000)), new SplitShare(_rent.Id, Money.FromCents(-300000))]);
        Assert.Equal(2, _ledger.Store.GetSplitLines(Txn("T4").Id).Count);
    }

    [Fact]
    public void SplitDeposits_AreAllowedToo()
    {
        _ledger.Classification.SplitByWeights(Txn("T5").Id, [new SplitWeight(_supplies.Id, 0.3m), new SplitWeight(_rent.Id, 0.7m)]);
        Assert.Equal([150000L, 350000], _ledger.Store.GetSplitLines(Txn("T5").Id).Select(l => l.Amount.Cents));
    }

    [Fact]
    public void Resplit_ReplacesLines_AndUndoRestoresPreviousSplit()
    {
        var id = Txn("T4").Id;
        _ledger.Classification.SplitByWeights(id, [new SplitWeight(_supplies.Id, 1), new SplitWeight(_rent.Id, 1)]);
        _ledger.Classification.SplitByWeights(id, [new SplitWeight(_supplies.Id, 1), new SplitWeight(_rent.Id, 3)]);
        Assert.Equal([-250000L, -750000], _ledger.Store.GetSplitLines(id).Select(l => l.Amount.Cents));

        _ledger.History.Undo();
        Assert.Equal([-500000L, -500000], _ledger.Store.GetSplitLines(id).Select(l => l.Amount.Cents));

        _ledger.Classification.RemoveSplit(id);
        Assert.Empty(_ledger.Store.GetSplitLines(id));
    }

    [Fact]
    public void SplitTransaction_CannotBeClassifiedDirectly()
    {
        _ledger.Classification.SplitByWeights(Txn("T4").Id, [new SplitWeight(_supplies.Id, 1), new SplitWeight(_rent.Id, 1)]);
        Assert.Throws<LedgerException>(() => _ledger.Classification.SetCategory([Txn("T4").Id], _rent.Id));
    }

    // ---------- rules ----------

    [Fact]
    public void Rules_PriorityOrderDecidesTheCategory()
    {
        var broad = NewRule("費用", _supplies.Id, contains: "費");
        var specific = NewRule("水電", _utilities.Id, regex: "^(台電|自來水)");

        Assert.All(_ledger.Rules.Preview(new RuleScope(_account.Id, March)).Where(h => h.TxnId == Txn("T1").Id), h => Assert.Equal(_supplies.Id, h.NewCategoryId));

        _ledger.Rules.Reorder([specific.Id, broad.Id]);

        var hits = _ledger.Rules.Preview(new RuleScope(_account.Id, March));
        Assert.Equal(_utilities.Id, hits.Single(h => h.TxnId == Txn("T1").Id).NewCategoryId);
        Assert.Equal(_utilities.Id, hits.Single(h => h.TxnId == Txn("T2").Id).NewCategoryId);
        Assert.Equal([1, 2], _ledger.Store.GetRules().Select(r => r.Priority));
    }

    [Fact]
    public void Preview_DoesNotWriteAnything()
    {
        NewRule("水電", _utilities.Id, contains: "電費");
        var auditBefore = AuditCount;
        var before = _ledger.Store.GetTransactions(_account.Id);

        var hits = _ledger.Rules.Preview(new RuleScope());

        Assert.Equal(2, hits.Count); // T1 (March) and T0 (February, also open)
        Assert.Equal(before, _ledger.Store.GetTransactions(_account.Id));
        Assert.Equal(auditBefore, AuditCount);
    }

    [Fact]
    public void Apply_IsOneStep_AndUndoRestoresEveryTransaction()
    {
        NewRule("水電", _utilities.Id, regex: "^(台電|自來水)");
        NewRule("房租", _rent.Id, contains: "房租");
        var before = _ledger.Store.GetTransactions(_account.Id);

        Assert.Equal(4, _ledger.Rules.Apply(new RuleScope()));
        Assert.Equal((_utilities.Id, ClassificationSource.Rule), (Txn("T1").CategoryId!.Value, Txn("T1").ClassifiedBy));
        Assert.Equal(_rent.Id, Txn("T3").CategoryId);
        Assert.Equal("套用規則（4 筆）", _ledger.History.NextUndoOperation);

        _ledger.History.Undo();

        Assert.Equal(before, _ledger.Store.GetTransactions(_account.Id));
    }

    [Fact]
    public void Apply_SkipsManualSplitAndClosedPeriodTransactions()
    {
        _ledger.Classification.SetCategory([Txn("T2").Id], _supplies.Id);
        _ledger.Classification.SplitByWeights(Txn("T3").Id, [new SplitWeight(_rent.Id, 1), new SplitWeight(_supplies.Id, 1)]);
        _ledger.Executor.Execute(new ChangeSetBuilder("結帳").Insert(new Period(_ledger.Store.ReserveIds(1), new YearMonth(2026, 2), true, null)).Build());
        NewRule("全部", _utilities.Id, max: -1);

        var hits = _ledger.Rules.Preview(new RuleScope());

        Assert.Equal(new[] { Txn("T1").Id, Txn("T4").Id }.Order(), hits.Select(h => h.TxnId).Order());
    }

    [Fact]
    public void InvalidRule_IsRejected()
    {
        Assert.Throws<LedgerException>(() => NewRule("壞", _rent.Id, regex: "(["));
        Assert.Throws<LedgerException>(() => NewRule("無條件", _rent.Id));
        NewRule("正常", _rent.Id, contains: "x");
        Assert.Throws<LedgerException>(() => _ledger.Rules.Reorder([]));
    }

    // ---------- list queries ----------

    [Fact]
    public void FindTransactions_FiltersByStatusKeywordAmountAndPeriod()
    {
        _ledger.Classification.SetCategory([Txn("T1").Id], _utilities.Id);
        var march = new TransactionFilter { AccountId = _account.Id, Period = March };

        Assert.Equal(5, _ledger.Store.CountTransactions(march));
        Assert.Equal(["T5"], Serials(march with { Status = TransactionStatusFilter.UnmatchedDeposits }));
        Assert.Equal(["T2", "T3", "T4"], Serials(march with { Status = TransactionStatusFilter.Unclassified, MaxAmount = Money.FromCents(-1) }));
        Assert.Equal(["T1"], Serials(march with { Keyword = "台電" }));
        Assert.Equal(["T4"], Serials(march with { Keyword = "原料" }));
        Assert.Empty(Serials(march with { Keyword = "%" }));
        Assert.Equal(["T3", "T4"], Serials(march with { MaxAmount = Money.FromCents(-1000000) }));
        Assert.Equal("水電費", _ledger.Store.FindTransactions(march with { Keyword = "台電" }).Single().CategoryName);
    }

    [Fact]
    public void FindTransactions_KeysetPagingCoversAllRowsOnce()
    {
        var all = new TransactionFilter { AccountId = _account.Id };
        var seen = new List<string>();
        TransactionCursor? cursor = null;
        while (true)
        {
            var page = _ledger.Store.FindTransactions(all, cursor, 2);
            if (page.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Select(r => r.Transaction.Serial!));
            cursor = new TransactionCursor(page[^1].Transaction.TxnDate, page[^1].Transaction.Id);
        }

        Assert.Equal(["T0", "T1", "T2", "T3", "T4", "T5"], seen);
    }

    private List<string> Serials(TransactionFilter filter) =>
        _ledger.Store.FindTransactions(filter).Select(r => r.Transaction.Serial!).ToList();
}
