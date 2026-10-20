using LedgerMatch.Core;
using LedgerMatch.Core.Books;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Core.Tests.Importing;

namespace LedgerMatch.Data.Tests.Books;

public class BookServiceTests : IDisposable
{
    private static readonly YearMonth March = new(2026, 3);
    private static readonly YearMonth April = new(2026, 4);
    private readonly TestLedger _ledger = new();
    private readonly Account _account;
    private readonly BookService _books;
    private readonly ReconciliationService _reports;

    public BookServiceTests()
    {
        _account = _ledger.CreateAccount(TestProfiles.BankA, "甲銀行帳戶", 1_000_000);
        _books = new BookService(_ledger.Store, _ledger.Store, _ledger.History);
        _reports = new ReconciliationService(_ledger.Store, _ledger.Time);
        _ledger.ImportUtf8(_account, "交易日期,摘要,金額,交易序號,備註\n2026/03/25,利息,12.34,T1,\n2026/04/03,支票兌現,-3000,T2,\n");
    }

    public void Dispose() => _ledger.Dispose();

    private BankTransaction Txn(string serial) => _ledger.Store.GetTransactions(_account.Id).Single(t => t.Serial == serial);

    [Fact]
    public void UnclearedCheque_IsOutstandingUntilTheBankShowsIt()
    {
        var cheque = _books.AddEntry(_account.Id, new DateOnly(2026, 3, 28), Money.FromCents(-300_000), "支票 #001 麵粉行");

        var march = _reports.Generate(_account.Id, March);
        Assert.Equal(-300_000, march.OutstandingItems.Total.Cents);
        Assert.True(march.IsBalanced);

        _books.ClearEntry(cheque.Id, Txn("T2").Id);

        var april = _reports.Generate(_account.Id, April);
        Assert.Empty(april.OutstandingItems.Items);
        Assert.Empty(april.BankOnlyWithdrawals.Items);
        Assert.True(april.IsBalanced);
        Assert.Equal(-300_000, _reports.Generate(_account.Id, March).OutstandingItems.Total.Cents); // still outstanding as of March
    }

    [Fact]
    public void ClearEntry_RequiresSameAmount()
    {
        var entry = _books.AddEntry(_account.Id, new DateOnly(2026, 3, 28), Money.FromCents(-299_999), "支票");
        Assert.Throws<LedgerException>(() => _books.ClearEntry(entry.Id, Txn("T2").Id));
    }

    [Fact]
    public void DepositBookedDirectly_LeavesBankOnlyList_AndCannotBeMatched()
    {
        Assert.Single(_reports.Generate(_account.Id, March).BankOnlyDeposits.Items);

        _books.SetDepositsBooked([Txn("T1").Id], booked: true);

        var report = _reports.Generate(_account.Id, March);
        Assert.Empty(report.BankOnlyDeposits.Items);
        Assert.Equal(1_001_234, report.BookBalance.Cents);
        Assert.True(report.IsBalanced);
        var receipt = _ledger.AddReceipt(_account, "R-1", 1234, new DateOnly(2026, 3, 25));
        Assert.Throws<LedgerException>(() => _ledger.Matching.ManualMatch(Txn("T1").Id, [receipt.Id], false));
        Assert.Empty(_ledger.Store.GetUnmatchedDeposits(_account.Id, March));
    }

    [Fact]
    public void ConfirmedMatch_WithAReceiptIssuedNextMonth_IsBookedInTheDepositMonth()
    {
        var early = _ledger.AddReceipt(_account, "R-1", 1000, new DateOnly(2026, 3, 24));
        var late = _ledger.AddReceipt(_account, "R-2", 234, new DateOnly(2026, 4, 1));
        _ledger.Matching.ManualMatch(Txn("T1").Id, [early.Id, late.Id], false);

        var march = _reports.Generate(_account.Id, March);

        Assert.Empty(march.DepositsInTransit.Items);
        Assert.Empty(march.BankOnlyDeposits.Items);
        Assert.Equal(1_001_234, march.BookBalance.Cents);
        Assert.Equal(march.ComputedBankBalance, march.AdjustedBankBalance);
        Assert.True(march.IsBalanced);
    }

    [Fact]
    public void StatementBalance_SetChangeRemove_AreUndoable()
    {
        _books.SetStatementBalance(_account.Id, March, Money.FromCents(1));
        _books.SetStatementBalance(_account.Id, March, Money.FromCents(2));
        Assert.Equal(Money.FromCents(2), _ledger.Store.GetStatementBalance(_account.Id, March)!.EndingBalance);

        _books.SetStatementBalance(_account.Id, March, null);
        Assert.Null(_ledger.Store.GetStatementBalance(_account.Id, March));

        _ledger.History.Undo();
        _ledger.History.Undo();
        Assert.Equal(Money.FromCents(1), _ledger.Store.GetStatementBalance(_account.Id, March)!.EndingBalance);
    }

    [Fact]
    public void EntryInClosedMonth_IsProtected()
    {
        _ledger.Periods.Close(March);
        Assert.Throws<PeriodClosedException>(() => _books.AddEntry(_account.Id, new DateOnly(2026, 3, 30), Money.FromCents(-1), "x"));
    }
}
