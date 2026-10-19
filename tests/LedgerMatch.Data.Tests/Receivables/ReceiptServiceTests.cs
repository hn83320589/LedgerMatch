using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Tests.Importing;

namespace LedgerMatch.Data.Tests.Receivables;

public class ReceiptServiceTests : IDisposable
{
    private readonly TestLedger _ledger = new();
    private readonly Account _account;
    private readonly long _profileId;

    public ReceiptServiceTests()
    {
        _account = _ledger.BankA();
        _profileId = _ledger.Setup.SaveProfile(TestProfiles.Receipts).Id;
    }

    public void Dispose() => _ledger.Dispose();

    private const string Header = "開立日期,客戶名稱,單據編號,金額,備註\n";

    [Fact]
    public void Import_AddsSkipsIdenticalAndReportsConflicts()
    {
        _ledger.AddReceipt(_account, "R-1", 100000, new DateOnly(2026, 3, 1), "王小姐");

        var summary = _ledger.Receipts.Import(_account.Id, _profileId, TestProfiles.Utf8(Header +
            "2026-03-01,王小姐,R-1,\"1,000\",\n" +   // line 2 identical → skipped
            "2026-03-02,咖啡廳,R-2,350,\n" +         // line 3 new
            "2026-03-02,咖啡廳,R-2,350,\n" +         // line 4 repeated inside the file → skipped
            "2026-03-03,別人,R-1,999,\n" +           // line 5 conflicting content → error
            "2026-03-04,,R-3,1,\n"), "receipts.csv"); // line 6 parse error

        Assert.Equal((1, 2), (summary.Added, summary.SkippedDuplicates));
        Assert.Equal([5, 6], summary.Errors.Select(e => e.LineNumber));
        Assert.Contains("內容不同", summary.Errors[0].Reason);
        Assert.Equal(2, _ledger.Store.GetReceipts(_account.Id).Count);
    }

    [Fact]
    public void Import_IsIdempotent()
    {
        var csv = Header + "2026-03-02,咖啡廳,R-2,350,\n2026-03-05,麵包店,R-3,1200,\n";
        _ledger.Receipts.Import(_account.Id, _profileId, TestProfiles.Utf8(csv), "a.csv");
        var second = _ledger.Receipts.Import(_account.Id, _profileId, TestProfiles.Utf8(csv), "a.csv");

        Assert.Equal((0, 2), (second.Added, second.SkippedDuplicates));
    }

    [Fact]
    public void Import_RowInClosedMonth_IsReportedNotAdded()
    {
        _ledger.Executor.Execute(new Core.Persistence.ChangeSetBuilder("結帳")
            .Insert(new Period(_ledger.Store.ReserveIds(1), new YearMonth(2026, 2), true, null)).Build());

        var summary = _ledger.Receipts.Import(_account.Id, _profileId, TestProfiles.Utf8(Header + "2026-02-27,x,R-9,1,\n"), "a.csv");

        Assert.Contains("已結帳", Assert.Single(summary.Errors).Reason);
    }

    [Fact]
    public void Add_ValidatesInput()
    {
        var day = new DateOnly(2026, 3, 1);
        Assert.Throws<LedgerException>(() => _ledger.Receipts.Add(_account.Id, day, "x", "R-1", Money.Zero, null));
        Assert.Throws<LedgerException>(() => _ledger.Receipts.Add(_account.Id, day, " ", "R-1", Money.FromCents(1), null));
        Assert.Throws<LedgerException>(() => _ledger.Receipts.Add(999, day, "x", "R-1", Money.FromCents(1), null));
        _ledger.Receipts.Add(_account.Id, day, "x", "R-1", Money.FromCents(1), null);
        Assert.Throws<LedgerException>(() => _ledger.Receipts.Add(_account.Id, day, "y", "R-1", Money.FromCents(2), null));
    }

    [Fact]
    public void EditAndDelete_AreUndoable()
    {
        var r = _ledger.AddReceipt(_account, "R-1", 1000, new DateOnly(2026, 3, 1));
        _ledger.Receipts.Update(r with { Customer = "新客戶", Note = "  " });
        Assert.Equal("新客戶", _ledger.Store.GetReceipt(r.Id)!.Customer);
        Assert.Null(_ledger.Store.GetReceipt(r.Id)!.Note);

        _ledger.Receipts.Delete(r.Id);
        _ledger.History.Undo();
        _ledger.History.Undo();

        Assert.Equal(r, _ledger.Store.GetReceipt(r.Id));
    }

    [Fact]
    public void MatchedReceipt_AllowsNoteEditsButNotAmountChanges()
    {
        _ledger.ImportUtf8(_account, "交易日期,摘要,金額,交易序號,備註\n2026/03/02,存入,10,T1,\n");
        var r = _ledger.AddReceipt(_account, "R-1", 1000, new DateOnly(2026, 3, 1));
        _ledger.Matching.Run(_account.Id, new YearMonth(2026, 3));

        _ledger.Receipts.Update(r with { Note = "已收" });
        var ex = Assert.Throws<LedgerException>(() => _ledger.Receipts.Update(r with { Amount = Money.FromCents(999) }));
        Assert.Contains("解除配對", ex.Message);
    }
}
