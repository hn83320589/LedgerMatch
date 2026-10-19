using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Core.Tests.Importing;

namespace LedgerMatch.Data.Tests.Samples;

/// <summary>Runs the files under samples/ end to end and checks every figure in samples/expected-results.md.</summary>
public class SampleDataTests : IDisposable
{
    private static readonly YearMonth March = new(2026, 3);
    private readonly TestLedger _ledger = new();
    private readonly Account _bankA;
    private readonly Account _bankB;
    private readonly ReconciliationService _reconciliation;

    public SampleDataTests()
    {
        _bankA = _ledger.CreateAccount(TestProfiles.BankA, "甲銀行帳戶", openingCents: 5_000_000);
        _bankB = _ledger.CreateAccount(TestProfiles.BankB, "乙銀行帳戶", openingCents: 2_000_000);
        _reconciliation = new ReconciliationService(_ledger.Store, _ledger.Time);
    }

    public void Dispose() => _ledger.Dispose();

    internal static MappingProfile ReceiptProfile => TestProfiles.Receipts with { AccountName = ColumnRef.ByName("入帳帳戶") };

    internal static string SamplePath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LedgerMatch.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "samples", file);
    }

    private ImportSummary Import(Account account, string file)
    {
        using var stream = File.OpenRead(SamplePath(file));
        return _ledger.Statements.Import(account.Id, stream, file);
    }

    private void ImportAll(bool reverse = false)
    {
        string[] a = ["bank-a-2026-03-part1.csv", "bank-a-2026-03-part2.csv"];
        string[] b = ["bank-b-2026-03-part1.csv", "bank-b-2026-03-part2.csv"];
        foreach (var file in reverse ? a.Reverse() : a)
        {
            Import(_bankA, file);
        }

        foreach (var file in reverse ? b.Reverse() : b)
        {
            Import(_bankB, file);
        }

        ImportReceipts();
    }

    private Core.Receivables.ReceiptImportSummary ImportReceipts()
    {
        var profile = _ledger.Setup.SaveProfile(ReceiptProfile with { Name = $"收款單{Guid.NewGuid():N}" });
        using var stream = File.OpenRead(SamplePath("receipts-2026-03.csv"));
        return _ledger.Receipts.Import(null, profile.Id, stream, "receipts-2026-03.csv");
    }

    [Fact]
    public void Imports_ProduceTheDocumentedCounts()
    {
        var a1 = Import(_bankA, "bank-a-2026-03-part1.csv");
        var a2 = Import(_bankA, "bank-a-2026-03-part2.csv");
        var b1 = Import(_bankB, "bank-b-2026-03-part1.csv");
        var b2 = Import(_bankB, "bank-b-2026-03-part2.csv");
        var r = ImportReceipts();
        var again = ImportReceipts();

        Assert.Equal((8, 0, "8"), (a1.Added, a1.SkippedDuplicates, string.Join(",", a1.Errors.Select(e => e.LineNumber))));
        Assert.Equal((4, 4, "10"), (a2.Added, a2.SkippedDuplicates, string.Join(",", a2.Errors.Select(e => e.LineNumber))));
        Assert.Equal((6, 0, "8"), (b1.Added, b1.SkippedDuplicates, string.Join(",", b1.Errors.Select(e => e.LineNumber))));
        Assert.Equal((1, 5, "9"), (b2.Added, b2.SkippedDuplicates, string.Join(",", b2.Errors.Select(e => e.LineNumber))));
        Assert.Equal((16, 0, "18"), (r.Added, r.SkippedDuplicates, string.Join(",", r.Errors.Select(e => e.LineNumber))));
        Assert.Equal((0, 16), (again.Added, again.SkippedDuplicates));
        Assert.Equal(12, _ledger.Store.GetTransactions(_bankA.Id).Count);
        Assert.Equal(7, _ledger.Store.GetTransactions(_bankB.Id).Count);
        Assert.Equal(3, _ledger.Store.GetTransactions(_bankB.Id).Count(t => t.TxnDate == new DateOnly(2026, 3, 12)));
        Assert.Equal(2, _ledger.Store.GetTransactions(_bankA.Id).Count(t => t.TxnDate == new DateOnly(2026, 3, 10)));
        Assert.Equal("咖啡廳 \"小日子\", 中山店", _ledger.Store.GetReceiptByNumber("R-0311")!.Customer);
    }

    [Fact]
    public void ReverseImportOrder_GivesTheSameLedger()
    {
        ImportAll();
        var forward = Snapshot();

        using var other = new TestLedger();
        var a = other.CreateAccount(TestProfiles.BankA, "甲銀行帳戶", 5_000_000);
        var b = other.CreateAccount(TestProfiles.BankB, "乙銀行帳戶", 2_000_000);
        foreach (var (account, file) in new[] { (a, "bank-a-2026-03-part2.csv"), (a, "bank-a-2026-03-part1.csv"), (b, "bank-b-2026-03-part2.csv"), (b, "bank-b-2026-03-part1.csv") })
        {
            using var stream = File.OpenRead(SamplePath(file));
            other.Statements.Import(account.Id, stream, file);
        }

        var reverse = other.Store.GetTransactions(a.Id).Concat(other.Store.GetTransactions(b.Id)).Select(Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void Matching_ProducesTheDocumentedPairs()
    {
        ImportAll();

        var a = _ledger.Matching.Run(_bankA.Id, March);
        var b = _ledger.Matching.Run(_bankB.Id, March);

        Assert.Equal((4, 3), (a.Confirmed, a.Suggested));
        Assert.Equal((3, 1), (b.Confirmed, b.Suggested));
        Assert.Equal(
            [
                "A0301001|Exact|Confirmed|0.00|R-0301",
                "A0303001|Exact|Confirmed|0.00|R-0302",
                "A0308001|OneToMany|Suggested|0.00|R-0303,R-0304",
                "A0310001|Exact|Confirmed|0.00|R-0305",
                "A0310002|Exact|Confirmed|0.00|R-0306",
                "A0314001|WithFee|Suggested|30.00|R-0307",
                "A0320001|WithFee|Suggested|240.00|R-0308,R-0309",
            ],
            Pairs(_bankA));
        Assert.Equal(
            [
                "2026-03-02 1200.00|Exact|Confirmed|0.00|R-B01",
                "2026-03-12 500.00|Exact|Confirmed|0.00|R-B02",
                "2026-03-12 500.00|Exact|Confirmed|0.00|R-B03",
                "2026-03-25 2600.00|OneToMany|Suggested|0.00|R-B04,R-B05",
            ],
            Pairs(_bankB));
    }

    [Fact]
    public void Reconciliation_MatchesHandCalculation_BeforeAndAfterConfirmingSuggestions()
    {
        ImportAll();
        _ledger.Matching.Run(_bankA.Id, March);
        _ledger.Matching.Run(_bankB.Id, March);

        AssertReport(_reconciliation.Generate(_bankA.Id, March), bank: 6_553_784, book: 6_674_550, inTransit: 2_633_000, inTransitCount: 7, bankOnly: 2_512_234, bankOnlyCount: 5, adjusted: 9_186_784);
        AssertReport(_reconciliation.Generate(_bankB.Id, March), bank: 630_100, book: 580_100, inTransit: 260_000, inTransitCount: 2, bankOnly: 310_000, bankOnlyCount: 2, adjusted: 890_100);

        foreach (var account in new[] { _bankA, _bankB })
        {
            _ledger.Matching.Confirm(_ledger.Store.GetMatches(account.Id, March).Select(m => m.Id).ToList());
        }

        AssertReport(_reconciliation.Generate(_bankA.Id, March), bank: 6_553_784, book: 6_647_550, inTransit: 245_000, inTransitCount: 2, bankOnly: 151_234, bankOnlyCount: 2, adjusted: 6_798_784);
        AssertReport(_reconciliation.Generate(_bankB.Id, March), bank: 630_100, book: 580_100, inTransit: 0, inTransitCount: 0, bankOnly: 50_000, bankOnlyCount: 1, adjusted: 630_100);
    }

    [Fact]
    public void StatementBalance_FromTheBankAgreesWithImportedLines()
    {
        ImportAll();
        var books = new Core.Books.BookService(_ledger.Store, _ledger.Store, _ledger.History);
        books.SetStatementBalance(_bankA.Id, March, Money.FromCents(6_553_784));

        var report = _reconciliation.Generate(_bankA.Id, March);

        Assert.Equal(Money.FromCents(6_553_784), report.StatementBalance);
        Assert.True(report.IsBalanced);
    }

    private static void AssertReport(ReconciliationReport r, long bank, long book, long inTransit, int inTransitCount, long bankOnly, int bankOnlyCount, long adjusted)
    {
        Assert.Equal(bank, r.ComputedBankBalance.Cents);
        Assert.Equal(book, r.BookBalance.Cents);
        Assert.Equal((inTransit, inTransitCount), (r.DepositsInTransit.Total.Cents, r.DepositsInTransit.Items.Count));
        Assert.Equal((bankOnly, bankOnlyCount), (r.BankOnlyDeposits.Total.Cents, r.BankOnlyDeposits.Items.Count));
        Assert.Empty(r.BankOnlyWithdrawals.Items);
        Assert.Empty(r.OutstandingItems.Items);
        Assert.Equal(adjusted, r.AdjustedBankBalance.Cents);
        Assert.Equal(adjusted, r.AdjustedBookBalance.Cents);
        Assert.True(r.IsBalanced);
    }

    private List<string> Pairs(Account account) =>
        _ledger.Store.GetMatches(account.Id, March)
            .Select(m =>
            {
                var t = _ledger.Store.GetTransaction(m.DepositTxnId)!;
                var receipts = _ledger.Store.GetMatchLinks(m.Id).Select(l => _ledger.Store.GetReceipt(l.ReceiptId)!.ReceiptNo).Order(StringComparer.Ordinal);
                var id = t.Serial ?? $"{t.TxnDate:yyyy-MM-dd} {t.Amount.ToPlainString()}";
                return $"{id}|{m.Kind}|{m.Confidence}|{m.Fee.ToPlainString()}|{string.Join(",", receipts)}";
            })
            .Order(StringComparer.Ordinal)
            .ToList();

    private List<string> Snapshot() =>
        _ledger.Store.GetTransactions(_bankA.Id).Concat(_ledger.Store.GetTransactions(_bankB.Id)).Select(Key).Order(StringComparer.Ordinal).ToList();

    private static string Key(BankTransaction t) => $"{t.TxnDate:yyyy-MM-dd}|{t.Amount.Cents}|{t.Description}|{t.Serial}|{t.Note}";
}
