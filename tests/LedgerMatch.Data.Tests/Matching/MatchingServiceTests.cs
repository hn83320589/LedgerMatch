using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Data.Tests.Importing;

namespace LedgerMatch.Data.Tests.Matching;

public class MatchingServiceTests : IDisposable
{
    private static readonly YearMonth March = new(2026, 3);
    private readonly TestLedger _ledger = new();
    private readonly Account _account;

    public MatchingServiceTests()
    {
        _account = _ledger.BankA();
        _ledger.ImportUtf8(_account,
            "交易日期,摘要,金額,交易序號,備註\n" +
            "2026/03/02,現金存入 R-0001,\"1,200\",T1,\n" +   // exact by number
            "2026/03/05,現金存入,800,T2,\n" +                 // exact by date
            "2026/03/10,刷卡撥款,\"1,960\",T3,\n" +           // one-to-many 1,000 + 960
            "2026/03/15,匯款,\"4,985\",T4,\n" +               // fee: 5,000 − 15
            "2026/03/20,利息,0.75,T5,\n" +                    // unmatched
            "2026/03/21,房租,-20000,T6,\n");                  // withdrawal, never matched
        _ledger.AddReceipt(_account, "R-0001", 120000, new DateOnly(2026, 2, 20));
        _ledger.AddReceipt(_account, "R-0002", 80000, new DateOnly(2026, 3, 3));
        _ledger.AddReceipt(_account, "R-0003", 100000, new DateOnly(2026, 3, 8));
        _ledger.AddReceipt(_account, "R-0004", 96000, new DateOnly(2026, 3, 9));
        _ledger.AddReceipt(_account, "R-0005", 500000, new DateOnly(2026, 3, 14));
        _ledger.AddReceipt(_account, "R-0099", 77700, new DateOnly(2026, 3, 18)); // never matched
        _ledger.History.Clear();
    }

    public void Dispose() => _ledger.Dispose();

    private BankTransaction Txn(string serial) => _ledger.Store.GetTransactions(_account.Id).Single(t => t.Serial == serial);

    private Receipt Rec(string no) => _ledger.Store.GetReceiptByNumber(no)!;

    private (Match Match, List<string> Receipts) MatchOf(string serial)
    {
        var m = _ledger.Store.GetMatchForDeposit(Txn(serial).Id)!;
        return (m, _ledger.Store.GetMatchLinks(m.Id).Select(l => _ledger.Store.GetReceipt(l.ReceiptId)!.ReceiptNo).Order().ToList());
    }

    private int DbFingerprint() =>
        _ledger.Store.GetMatches(_account.Id, March).Count * 1000 + _ledger.Store.GetAuditEntries(0, 100000).Count;

    [Fact]
    public void Run_PersistsAllThreeStagesWithConfidenceLevels()
    {
        var summary = _ledger.Matching.Run(_account.Id, March);

        Assert.Equal((2, 2), (summary.Confirmed, summary.Suggested));
        Assert.Equal((MatchKind.Exact, MatchConfidence.Confirmed), (MatchOf("T1").Match.Kind, MatchOf("T1").Match.Confidence));
        Assert.Equal(["R-0001"], MatchOf("T1").Receipts);
        Assert.Equal(["R-0002"], MatchOf("T2").Receipts);
        Assert.Equal((MatchKind.OneToMany, MatchConfidence.Suggested), (MatchOf("T3").Match.Kind, MatchOf("T3").Match.Confidence));
        Assert.Equal(["R-0003", "R-0004"], MatchOf("T3").Receipts);
        var fee = MatchOf("T4");
        Assert.Equal((MatchKind.WithFee, Money.FromCents(1500)), (fee.Match.Kind, fee.Match.Fee));
        Assert.Null(_ledger.Store.GetMatchForDeposit(Txn("T5").Id));
        Assert.Null(_ledger.Store.GetMatchForReceipt(Rec("R-0099").Id));
    }

    [Fact]
    public void Run_IsOneUndoStep_AndRedoRestoresIt()
    {
        _ledger.Matching.Run(_account.Id, March);
        Assert.Equal(4, _ledger.Store.GetMatches(_account.Id, March).Count);

        _ledger.History.Undo();
        Assert.Empty(_ledger.Store.GetMatches(_account.Id, March));

        _ledger.History.Redo();
        Assert.Equal(4, _ledger.Store.GetMatches(_account.Id, March).Count);
    }

    [Fact]
    public void Rerun_OnlyConsidersWhatIsStillUnmatched()
    {
        _ledger.Matching.Run(_account.Id, March);
        var again = _ledger.Matching.Run(_account.Id, March);

        Assert.Equal((0, 0), (again.Confirmed, again.Suggested));
        Assert.Equal(4, _ledger.Store.GetMatches(_account.Id, March).Count);
    }

    [Theory]
    [InlineData(MatchingStage.Exact)]
    [InlineData(MatchingStage.OneToMany)]
    [InlineData(MatchingStage.WithFee)]
    public void Cancel_DuringAnyStage_LeavesDatabaseExactlyAsBefore(MatchingStage cancelAt)
    {
        var before = DbFingerprint();
        using var cts = new CancellationTokenSource();
        var reports = new List<MatchingProgress>();
        var progress = new SyncProgress<MatchingProgress>(p =>
        {
            reports.Add(p);
            if (p.Stage == cancelAt)
            {
                cts.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => _ledger.Matching.Run(_account.Id, March, progress, cts.Token));

        Assert.Equal(before, DbFingerprint());
        Assert.False(_ledger.History.CanUndo);
        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task RunAsync_ReportsProgressAndCompletes()
    {
        var reports = new List<MatchingProgress>();
        var summary = await _ledger.Matching.RunAsync(_account.Id, March, new SyncProgress<MatchingProgress>(reports.Add));

        Assert.Equal(4, summary.Confirmed + summary.Suggested);
        Assert.Contains(reports, r => r.Stage == MatchingStage.WithFee);
    }

    [Fact]
    public void ConfirmBatch_IsOneStep()
    {
        _ledger.Matching.Run(_account.Id, March);
        var suggested = _ledger.Store.GetMatches(_account.Id, March).Where(m => m.Confidence == MatchConfidence.Suggested).Select(m => m.Id).ToList();

        Assert.Equal(2, _ledger.Matching.Confirm(suggested));
        Assert.All(_ledger.Store.GetMatches(_account.Id, March), m => Assert.Equal(MatchConfidence.Confirmed, m.Confidence));

        _ledger.History.Undo();
        Assert.Equal(2, _ledger.Store.GetMatches(_account.Id, March).Count(m => m.Confidence == MatchConfidence.Suggested));
    }

    [Fact]
    public void Reject_RemovesOnlySuggestions_FreeingReceipts()
    {
        _ledger.Matching.Run(_account.Id, March);
        var all = _ledger.Store.GetMatches(_account.Id, March).Select(m => m.Id).ToList();

        Assert.Equal(2, _ledger.Matching.Reject(all));

        Assert.Equal(2, _ledger.Store.GetMatches(_account.Id, March).Count);
        Assert.Null(_ledger.Store.GetMatchForReceipt(Rec("R-0003").Id));
    }

    [Fact]
    public void Unmatch_RemovesConfirmedMatch_AndIsUndoable()
    {
        _ledger.Matching.Run(_account.Id, March);
        var exact = MatchOf("T1").Match;

        _ledger.Matching.Unmatch([exact.Id]);
        Assert.Null(_ledger.Store.GetMatchForReceipt(Rec("R-0001").Id));

        _ledger.History.Undo();
        Assert.Equal(exact, _ledger.Store.GetMatchForReceipt(Rec("R-0001").Id));
    }

    [Fact]
    public void ManualMatch_EqualAmounts()
    {
        var m = _ledger.Matching.ManualMatch(Txn("T3").Id, [Rec("R-0003").Id, Rec("R-0004").Id], acceptDifferenceAsFee: false);

        Assert.Equal((MatchKind.Manual, MatchConfidence.Confirmed, Money.Zero), (m.Kind, m.Confidence, m.Fee));
        Assert.Equal(["R-0003", "R-0004"], MatchOf("T3").Receipts);
    }

    [Fact]
    public void ManualMatch_DifferenceRequiresExplicitFee()
    {
        var ex = Assert.Throws<LedgerException>(() => _ledger.Matching.ManualMatch(Txn("T4").Id, [Rec("R-0005").Id], false));
        Assert.Contains("手續費", ex.Message);

        var m = _ledger.Matching.ManualMatch(Txn("T4").Id, [Rec("R-0005").Id], true);
        Assert.Equal(Money.FromCents(1500), m.Fee);
    }

    [Fact]
    public void ManualMatch_RejectsInvalidSelections()
    {
        Assert.Throws<LedgerException>(() => _ledger.Matching.ManualMatch(Txn("T6").Id, [Rec("R-0099").Id], true));        // withdrawal
        Assert.Throws<LedgerException>(() => _ledger.Matching.ManualMatch(Txn("T2").Id, [], false));                         // nothing selected

        _ledger.Matching.ManualMatch(Txn("T2").Id, [Rec("R-0002").Id], false);
        Assert.Throws<LedgerException>(() => _ledger.Matching.ManualMatch(Txn("T2").Id, [Rec("R-0099").Id], true));        // deposit already matched
        Assert.Throws<LedgerException>(() => _ledger.Matching.ManualMatch(Txn("T1").Id, [Rec("R-0002").Id], true));        // receipt already matched
        Assert.Throws<LedgerException>(() => _ledger.Matching.ManualMatch(Txn("T3").Id, [Rec("R-0003").Id], true));        // receipts below deposit
    }

    [Fact]
    public void MatchedReceipt_CannotBeDeletedUntilUnmatched()
    {
        _ledger.Matching.Run(_account.Id, March);
        var receipt = Rec("R-0001");

        var ex = Assert.Throws<LedgerException>(() => _ledger.Receipts.Delete(receipt.Id));
        Assert.Contains("解除配對", ex.Message);

        _ledger.Matching.Unmatch([MatchOf("T1").Match.Id]);
        _ledger.Receipts.Delete(receipt.Id);
        Assert.Null(_ledger.Store.GetReceipt(receipt.Id));
    }

    [Fact]
    public void SavedSettings_AreUsedByTheEngine()
    {
        _ledger.Settings.SaveMatchingSettings(MatchingSettings.Default with { FeeFixedCap = Money.FromCents(1000), FeeRateCap = null });
        _ledger.Reopen();

        _ledger.Matching.Run(_account.Id, March);

        Assert.Null(_ledger.Store.GetMatchForDeposit(Txn("T4").Id)); // fee 15.00 exceeds the 10.00 cap
        Assert.Equal(Money.FromCents(1000), _ledger.Settings.GetMatchingSettings().FeeFixedCap);
    }

    [Fact]
    public void ClosedPeriod_RefusesToRun()
    {
        _ledger.Executor.Execute(new ChangeSetBuilder("結帳").Insert(new Period(_ledger.Store.ReserveIds(1), March, true, null)).Build());
        Assert.Throws<PeriodClosedException>(() => _ledger.Matching.Run(_account.Id, March));
    }
}
