using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Data.Tests.Importing;

public class StatementImportTests : IDisposable
{
    private readonly TestLedger _ledger = new();

    public void Dispose() => _ledger.Dispose();

    private const string BankAHeader = "交易日期,摘要,金額,交易序號,備註\n";
    private const string BankBHeader = "乙銀行 存款交易明細\n日期,摘要,提款,存款,備註\n";

    // 甲銀行 (serial column): 1–15 and 10–31 overlap on the 10th–15th; T0003/T0004 are genuine identical cash deposits.
    private const string BankAFirstHalf = BankAHeader +
        "2026/03/01,現金存入,500,T0001,\n" +
        "2026/03/05,房租,\"(20,000)\",T0002,\n" +
        "2026/03/10,現金存入,800,T0003,\n" +
        "2026/03/10,現金存入,800,T0004,\n" +
        "2026/03/15,刷卡撥款,\"1,960\",T0005,\n";

    private const string BankASecondHalf = BankAHeader +
        "2026/03/10,現金存入,800,T0003,\n" +
        "2026/03/10,現金存入,800,T0004,\n" +
        "2026/03/15,刷卡撥款,\"1,960\",T0005,\n" +
        "2026/03/20,水電費,-1234.5,T0006,\n" +
        "2026/03/31,利息,0.75,T0007,\n";

    // 乙銀行 (no serial column): same overlap shape; three identical cash deposits on the 12th.
    private const string BankBFirstHalf = BankBHeader +
        "115/03/02,現金存入,,300,\n" +
        "115/03/12,現金存入,,500,\n" +
        "115/03/12,現金存入,,500,\n" +
        "115/03/12,現金存入,,500,\n" +
        "115/03/14,電話費,999,,\n";

    private const string BankBSecondHalf = BankBHeader +
        "115/03/12,現金存入,,500,\n" +
        "115/03/12,現金存入,,500,\n" +
        "115/03/12,現金存入,,500,\n" +
        "115/03/14,電話費,999,,\n" +
        "115/03/25,現金存入,,500,\n";

    private List<string> Ledger(Account account) =>
        _ledger.Store.GetTransactions(account.Id)
            .Select(t => $"{t.TxnDate:MM-dd}|{t.Amount.Cents}|{t.Description}|{t.Serial}")
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void SameFileTwice_SecondImportAddsNothing()
    {
        var account = _ledger.BankA();

        var first = _ledger.ImportUtf8(account, BankAFirstHalf);
        var second = _ledger.ImportUtf8(account, BankAFirstHalf);

        Assert.Equal((5, 0, 0), (first.Added, first.SkippedDuplicates, first.Errors.Count));
        Assert.Equal((0, 5, 0), (second.Added, second.SkippedDuplicates, second.Errors.Count));
        Assert.Equal(5, _ledger.Store.GetTransactions(account.Id).Count);
    }

    [Fact]
    public void Summary_CountsAddedSkippedAndErrorsWithLineNumbers()
    {
        var account = _ledger.BankA();
        _ledger.ImportUtf8(account, BankAHeader + "2026/03/01,現金存入,500,T0001,\n");

        var summary = _ledger.ImportUtf8(account, BankAHeader +
            "2026/03/01,現金存入,500,T0001,\n" +   // line 2: duplicate
            "2026/3/2,壞日期,100,T0009,\n" +        // line 3: bad date
            "2026/03/03,壞金額,1.2.3,T0010,\n" +     // line 4: bad amount
            "2026/03/04,新的,100,T0011,\n");         // line 5: new

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.SkippedDuplicates);
        Assert.Equal([3, 4], summary.Errors.Select(e => e.LineNumber));
        var batch = _ledger.Store.Find<ImportBatch>(summary.BatchId)!;
        Assert.Equal((1, 1, 2, 0), (batch.Added, batch.Skipped, batch.Errors, batch.Pending));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlappingFiles_WithSerial_SameResultInEitherOrder(bool firstHalfFirst)
    {
        var account = _ledger.BankA();
        var files = firstHalfFirst ? new[] { BankAFirstHalf, BankASecondHalf } : [BankASecondHalf, BankAFirstHalf];

        var a = _ledger.ImportUtf8(account, files[0]);
        var b = _ledger.ImportUtf8(account, files[1]);

        Assert.Equal(7, Ledger(account).Count);
        Assert.Equal(2, Ledger(account).Count(r => r.StartsWith("03-10|80000|現金存入", StringComparison.Ordinal)));
        Assert.Equal((7, 3), (a.Added + b.Added, b.SkippedDuplicates));
        Assert.Equal(ExpectedBankA, Ledger(account));
    }

    private static readonly List<string> ExpectedBankA =
    [
        "03-01|50000|現金存入|T0001",
        "03-05|-2000000|房租|T0002",
        "03-10|80000|現金存入|T0003",
        "03-10|80000|現金存入|T0004",
        "03-15|196000|刷卡撥款|T0005",
        "03-20|-123450|水電費|T0006",
        "03-31|75|利息|T0007",
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlappingFiles_WithoutSerial_KeepIdenticalRowsAndSameResultInEitherOrder(bool firstHalfFirst)
    {
        var account = _ledger.BankB();
        var files = firstHalfFirst ? new[] { BankBFirstHalf, BankBSecondHalf } : [BankBSecondHalf, BankBFirstHalf];

        _ledger.ImportBig5(account, files[0]);
        var second = _ledger.ImportBig5(account, files[1]);

        Assert.Equal(4, second.SkippedDuplicates);
        Assert.Equal(
            [
                "03-02|30000|現金存入|",
                "03-12|50000|現金存入|",
                "03-12|50000|現金存入|",
                "03-12|50000|現金存入|",
                "03-14|-99900|電話費|",
                "03-25|50000|現金存入|",
            ],
            Ledger(account));
    }

    [Fact]
    public void IdenticalRows_SurviveRepeatedAndOverlappingImports()
    {
        var account = _ledger.BankB();
        foreach (var file in new[] { BankBFirstHalf, BankBSecondHalf, BankBFirstHalf, BankBSecondHalf, BankBFirstHalf })
        {
            _ledger.ImportBig5(account, file);
        }

        Assert.Equal(3, Ledger(account).Count(r => r == "03-12|50000|現金存入|"));
        Assert.Equal(6, Ledger(account).Count);
    }

    [Fact]
    public void ImportedTransactions_HaveExpectedDefaults()
    {
        var account = _ledger.BankA();
        _ledger.ImportUtf8(account, BankAFirstHalf);

        var txns = _ledger.Store.GetTransactions(account.Id);
        var rent = txns.Single(t => t.Serial == "T0002");
        var cash = txns.First(t => t.Serial == "T0001");
        Assert.True(rent.BookConfirmed);
        Assert.False(cash.BookConfirmed);
        Assert.Equal(new YearMonth(2026, 3), cash.PostingPeriod);
        Assert.Equal(2, cash.SourceLine);
        Assert.Equal(ClassificationSource.None, cash.ClassifiedBy);
    }

    [Fact]
    public void Import_IsAuditedAsOneOperation()
    {
        var account = _ledger.BankA();
        var auditBefore = _ledger.Store.GetAuditEntries(0, 1000).Count;

        _ledger.ImportUtf8(account, BankAFirstHalf, "march.csv");

        var entries = _ledger.Store.GetAuditEntries(0, 1000).Skip(auditBefore).ToList();
        Assert.Equal(6, entries.Count); // batch + 5 transactions
        Assert.All(entries, e => Assert.Equal("匯入對帳單 march.csv", e.Operation));
    }

    [Fact]
    public void Cancelled_BeforeSave_LeavesDatabaseUntouched()
    {
        var account = _ledger.BankA();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ImportProgress>(p =>
        {
            if (p.Stage == ImportStage.Planning)
            {
                cts.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            _ledger.Statements.Import(account.Id, Core.Tests.Importing.TestProfiles.Utf8(BankAFirstHalf), "x.csv", progress, cts.Token));
        Assert.Empty(_ledger.Store.GetTransactions(account.Id));
    }

    [Fact]
    public async Task ImportAsync_RunsInBackgroundAndReportsProgress()
    {
        var account = _ledger.BankA();
        var stages = new List<ImportStage>();

        var summary = await _ledger.Statements.ImportAsync(
            account.Id, Core.Tests.Importing.TestProfiles.Utf8(BankAFirstHalf), "x.csv", new SyncProgress<ImportProgress>(p => stages.Add(p.Stage)));

        Assert.Equal(5, summary.Added);
        Assert.Equal(ImportStage.Done, stages[^1]);
    }

    [Fact]
    public void UnknownAccount_Throws()
    {
        Assert.Throws<LedgerException>(() => _ledger.ImportUtf8(new Account(999999, "x", 1, Money.Zero, default), BankAFirstHalf));
    }
}

/// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
