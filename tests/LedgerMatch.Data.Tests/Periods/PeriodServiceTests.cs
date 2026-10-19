using LedgerMatch.Core;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Data.Tests.Periods;

public class PeriodServiceTests : IDisposable
{
    private static readonly YearMonth Feb = new(2026, 2);
    private static readonly YearMonth March = new(2026, 3);
    private const string Header = "交易日期,摘要,金額,交易序號,備註\n";

    private readonly TestLedger _ledger = new();
    private readonly Account _account;
    private readonly Category _category;

    public PeriodServiceTests()
    {
        _account = _ledger.BankA();
        _ledger.ImportUtf8(_account, Header +
            "2026/02/10,現金存入,1000,F1,\n" +
            "2026/02/11,文具,-200,F2,\n" +
            "2026/02/12,刷卡撥款,500,F3,\n");
        _ledger.AddReceipt(_account, "R-F1", 100000, new DateOnly(2026, 2, 10));
        _ledger.AddReceipt(_account, "R-F3", 50000, new DateOnly(2026, 2, 12));
        _category = _ledger.Categories.Add("雜支");
        _ledger.Matching.Run(_account.Id, Feb);
    }

    public void Dispose() => _ledger.Dispose();

    private BankTransaction Txn(string serial) => _ledger.Store.GetTransactions(_account.Id).Single(t => t.Serial == serial);

    [Fact]
    public void ClosedMonth_RejectsEveryKindOfChange()
    {
        _ledger.Periods.Close(Feb);
        var match = _ledger.Store.GetMatchForDeposit(Txn("F1").Id)!;

        Assert.Throws<PeriodClosedException>(() => _ledger.Classification.SetCategory([Txn("F2").Id], _category.Id));
        Assert.Throws<PeriodClosedException>(() => _ledger.Classification.SplitByWeights(Txn("F2").Id, [new(_category.Id, 1), new(Category.FeeCategoryId, 1)]));
        Assert.Throws<PeriodClosedException>(() => _ledger.Matching.Unmatch([match.Id]));
        Assert.Throws<PeriodClosedException>(() => _ledger.Matching.Run(_account.Id, Feb));
        Assert.Throws<PeriodClosedException>(() => _ledger.Receipts.Update(_ledger.Store.GetReceiptByNumber("R-F1")! with { Note = "x" }));
        Assert.Throws<PeriodClosedException>(() => _ledger.Receipts.Add(_account.Id, new DateOnly(2026, 2, 20), "x", "R-NEW", Money.FromCents(1), null));
        Assert.Throws<PeriodClosedException>(() => _ledger.History.Undo()); // last step was the February matching run
    }

    [Fact]
    public void ClosedMonth_IsSkippedByRules()
    {
        _ledger.Rules.Save(new Rule(0, "文具", 0, "文具", null, null, null, null, _category.Id, true));
        _ledger.Periods.Close(Feb);

        Assert.Empty(_ledger.Rules.Preview(new RuleScope()));
        Assert.Equal(0, _ledger.Rules.Apply(new RuleScope()));
    }

    [Fact]
    public void LateTransaction_GoesToPendingQueue_WithoutTouchingTheClosedMonth()
    {
        _ledger.Periods.Close(Feb);
        var febBefore = _ledger.Store.FindTransactions(new TransactionFilter { AccountId = _account.Id, Period = Feb });

        var summary = _ledger.ImportUtf8(_account, Header +
            "2026/02/12,刷卡撥款,500,F3,\n" +     // already booked → duplicate
            "2026/02/27,晚到的入帳,300,F4,\n" +   // closed month → pending
            "2026/03/01,三月入帳,700,M1,\n");     // open month → booked

        Assert.Equal((1, 1, 1), (summary.Added, summary.SkippedDuplicates, summary.Pending));
        Assert.Equal(febBefore, _ledger.Store.FindTransactions(new TransactionFilter { AccountId = _account.Id, Period = Feb }));
        var pending = Assert.Single(_ledger.Store.GetPendingItems(PendingStatus.Pending));
        Assert.Equal(("晚到的入帳", new DateOnly(2026, 2, 27)), (pending.Description, pending.TxnDate));

        // Re-importing does not duplicate the pending item.
        Assert.Equal(0, _ledger.ImportUtf8(_account, Header + "2026/02/27,晚到的入帳,300,F4,\n").Pending);
    }

    [Fact]
    public void PendingItem_PostsToNextOpenMonth_KeepingOriginalDate()
    {
        _ledger.Periods.Close(Feb);
        _ledger.Periods.Close(March);
        _ledger.ImportUtf8(_account, Header + "2026/02/27,晚到的入帳,300,F4,\n");
        var pending = _ledger.Store.GetPendingItems(PendingStatus.Pending).Single();

        var posted = Assert.Single(_ledger.Periods.PostToNextOpenMonth([pending.Id]));

        Assert.Equal((new DateOnly(2026, 2, 27), new YearMonth(2026, 4)), (posted.TxnDate, posted.PostingPeriod));
        Assert.Equal((PendingStatus.Posted, posted.Id), (_ledger.Store.GetPendingItem(pending.Id)!.Status, _ledger.Store.GetPendingItem(pending.Id)!.PostedTxnId!.Value));
        Assert.Equal(0, _ledger.ImportUtf8(_account, Header + "2026/02/27,晚到的入帳,300,F4,\n").Added);

        _ledger.History.Undo();
        Assert.Null(_ledger.Store.GetTransaction(posted.Id));
        Assert.Equal(PendingStatus.Pending, _ledger.Store.GetPendingItem(pending.Id)!.Status);
    }

    [Fact]
    public void PendingItem_CanBeDiscarded_AndStaysDiscarded()
    {
        _ledger.Periods.Close(Feb);
        _ledger.ImportUtf8(_account, Header + "2026/02/27,晚到的入帳,300,F4,\n");
        var pending = _ledger.Store.GetPendingItems(PendingStatus.Pending).Single();

        _ledger.Periods.Discard([pending.Id]);
        var again = _ledger.ImportUtf8(_account, Header + "2026/02/27,晚到的入帳,300,F4,\n");

        Assert.Equal((0, 1), (again.Pending, again.SkippedDuplicates));
        Assert.Throws<LedgerException>(() => _ledger.Periods.Discard([pending.Id]));
    }

    [Fact]
    public void Reopen_RequiresReason_AndIsRecorded()
    {
        _ledger.Periods.Close(Feb);

        Assert.Throws<LedgerException>(() => _ledger.Periods.Reopen(Feb, "   "));
        Assert.True(_ledger.Periods.IsClosed(Feb));

        _ledger.Periods.Reopen(Feb, "會計師要求更正文具費歸類");

        Assert.False(_ledger.Periods.IsClosed(Feb));
        _ledger.Classification.SetCategory([Txn("F2").Id], _category.Id);
        var events = _ledger.Store.GetPeriodEvents();
        Assert.Equal([PeriodAction.Close, PeriodAction.Reopen], events.Select(e => e.Action));
        Assert.Equal("會計師要求更正文具費歸類", events[1].Reason);
        Assert.Contains(_ledger.Store.GetAuditEntries(0, 10000), a => a.Operation == "重新開帳 2026-02：會計師要求更正文具費歸類");
    }

    [Fact]
    public void CloseTwice_OrReopenOpenMonth_AreErrors()
    {
        _ledger.Periods.Close(Feb);
        Assert.Throws<LedgerException>(() => _ledger.Periods.Close(Feb));
        Assert.Throws<LedgerException>(() => _ledger.Periods.Reopen(March, "x"));

        _ledger.Periods.Reopen(Feb, "更正");
        _ledger.Periods.Close(Feb);
        Assert.True(_ledger.Periods.IsClosed(Feb));
    }

    [Fact]
    public void CloseAndReopen_AreNotUndoSteps()
    {
        var before = _ledger.History.UndoCount;
        _ledger.Periods.Close(Feb);
        Assert.Equal(before, _ledger.History.UndoCount);
    }
}
