using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Tests.PropertyTesting;

namespace LedgerMatch.Core.Tests.Importing;

public class ImportPlannerTests
{
    private static readonly Dictionary<string, ExistingRow> None = [];
    private static readonly HashSet<YearMonth> NoClosed = [];

    private static Dictionary<string, ExistingRow> Existing(IEnumerable<(string Key, ParsedStatementRow Row)> rows) =>
        rows.ToDictionary(r => r.Key, r => new ExistingRow(r.Row.Date, r.Row.Amount, r.Row.Description));

    private static ParsedStatementRow Row(int day, long cents, string desc, string? serial = null, int line = 0) =>
        new(line, new DateOnly(2026, 3, day), Money.FromCents(cents), desc, serial, null);

    [Fact]
    public void Keys_WithoutSerial_NumberIdenticalRowsInFileOrder()
    {
        var keys = ImportPlanner.BuildKeys([Row(1, 500, "現金存入"), Row(1, 500, "現金存入"), Row(1, 500, "其他"), Row(1, 500, "現金存入")], hasSerial: false);

        Assert.Equal(
            ["N|2026-03-01|500|現金存入|1", "N|2026-03-01|500|現金存入|2", "N|2026-03-01|500|其他|1", "N|2026-03-01|500|現金存入|3"],
            keys);
    }

    [Fact]
    public void Keys_NormalizeWidthAndWhitespace()
    {
        var keys = ImportPlanner.BuildKeys([Row(1, 1, "  ＡＴＭ   轉帳 "), Row(1, 1, "ATM 轉帳")], hasSerial: false);
        Assert.Equal("N|2026-03-01|1|ATM 轉帳|1", keys[0]);
        Assert.Equal("N|2026-03-01|1|ATM 轉帳|2", keys[1]);
    }

    [Fact]
    public void Keys_WithSerial_UseSerialOnly()
    {
        Assert.Equal(["S|A001", "S|A002"], ImportPlanner.BuildKeys([Row(1, 5, "x", " A001 "), Row(1, 5, "x", "A002")], hasSerial: true));
    }

    [Fact]
    public void Plan_SplitsNewDuplicateAndClosedPeriodRows()
    {
        var rows = new[] { Row(1, 100, "a"), Row(2, 200, "b"), Row(3, 300, "c") };
        var existing = Existing([("N|2026-03-02|200|b|1", Row(2, 200, "b"))]);
        var febRow = new ParsedStatementRow(0, new DateOnly(2026, 2, 27), Money.FromCents(50), "late", null, null);

        var plan = ImportPlanner.Plan([.. rows, febRow], false, existing, new HashSet<YearMonth> { new(2026, 2) });

        Assert.Equal(["a", "c"], plan.ToInsert.Select(p => p.Row.Description));
        Assert.Equal(["b"], plan.Duplicates.Select(p => p.Row.Description));
        Assert.Equal(["late"], plan.ToPending.Select(p => p.Row.Description));
    }

    [Fact]
    public void Plan_RepeatedSerialInsideOneFile_SameTransaction_IsDuplicate()
    {
        var plan = ImportPlanner.Plan([Row(1, 1, "x", "A1", line: 2), Row(1, 1, " x ", "A1", line: 3)], true, None, NoClosed);

        Assert.Single(plan.ToInsert);
        Assert.Single(plan.Duplicates);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Plan_RepeatedSerialInsideOneFile_DifferentTransaction_IsAnErrorNamingBothLines()
    {
        var plan = ImportPlanner.Plan([Row(1, 1, "x", "A1", line: 2), Row(2, 2, "y", "A1", line: 3)], true, None, NoClosed);

        Assert.Equal(["x"], plan.ToInsert.Select(p => p.Row.Description));
        Assert.Empty(plan.Duplicates);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(3, conflict.LineNumber);
        Assert.Contains("A1", conflict.Reason);
        Assert.Contains("第 2 列", conflict.Reason);
    }

    [Fact]
    public void Plan_SerialAlreadyUsedByADifferentTransaction_IsAnErrorNotADuplicate()
    {
        // The serial was imported for February; banks that restart numbering would otherwise lose this row silently.
        var february = new ParsedStatementRow(0, new DateOnly(2026, 2, 10), Money.FromCents(500), "二月存入", "000123", null);
        var plan = ImportPlanner.Plan([Row(10, 900, "三月存入", "000123", line: 4), Row(11, 5, "z", "000124", line: 5)], true, Existing([("S|000123", february)]), NoClosed);

        Assert.Equal(["z"], plan.ToInsert.Select(p => p.Row.Description));
        Assert.Empty(plan.Duplicates);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(4, conflict.LineNumber);
        Assert.Contains("000123", conflict.Reason);
        Assert.Contains("2026-02-10", conflict.Reason);
    }

    [Fact]
    public void Plan_SerialAlreadyImportedForTheSameTransaction_IsDuplicate()
    {
        var plan = ImportPlanner.Plan([Row(10, 900, "ＡＴＭ 存入", "000123")], true, Existing([("S|000123", Row(10, 900, "ATM 存入", "000123"))]), NoClosed);

        Assert.Single(plan.Duplicates);
        Assert.Empty(plan.Conflicts);
    }

    /// <summary>
    /// Property: for any true ledger (with deliberate same-day/same-amount/same-description rows) exported as
    /// overlapping whole-day files, importing those files any number of times in any order yields exactly the
    /// true ledger.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Property_OverlappingFilesInAnyOrder_YieldTheTrueLedger(bool hasSerial)
    {
        Prop.ForAll(seed: 20261019, cases: 300, random =>
        {
            var truth = GenerateLedger(random, hasSerial);
            var files = SplitIntoOverlappingFiles(random, truth);
            var imports = random.Shuffle(files.Concat(files.Where(_ => random.Next(3) == 0)));

            var db = new List<(string Key, ParsedStatementRow Row)>();
            foreach (var file in imports)
            {
                var plan = ImportPlanner.Plan(file, hasSerial, Existing(db), NoClosed);
                Assert.Empty(plan.Conflicts);
                db.AddRange(plan.ToInsert.Select(p => (p.DedupKey, p.Row)));
            }

            Assert.Equal(Canonical(truth), Canonical(db.Select(d => d.Row)));
        });
    }

    private static List<ParsedStatementRow> GenerateLedger(Random random, bool hasSerial)
    {
        var rows = new List<ParsedStatementRow>();
        var serial = 0;
        string[] descriptions = ["現金存入", "刷卡撥款", "房租", "ATM 轉帳"];
        for (var day = 1; day <= 10; day++)
        {
            var count = random.Next(0, 5);
            for (var i = 0; i < count; i++)
            {
                // Small value space so identical (date, amount, description) rows are common.
                var amount = (random.Next(1, 4) * 100) * (random.Next(4) == 0 ? -1 : 1);
                rows.Add(new ParsedStatementRow(0, new DateOnly(2026, 3, day), Money.FromCents(amount), descriptions[random.Next(2)],
                    hasSerial ? $"T{++serial:D5}" : null, null));
            }
        }

        return rows;
    }

    private static List<List<ParsedStatementRow>> SplitIntoOverlappingFiles(Random random, List<ParsedStatementRow> truth)
    {
        var files = new List<List<ParsedStatementRow>>();
        var start = 1;
        while (start <= 10)
        {
            var end = Math.Min(10, start + random.Next(0, 5));
            files.Add(truth.Where(r => r.Date.Day >= start && r.Date.Day <= end).ToList());
            start = Math.Max(start + 1, end - random.Next(0, 3)); // next file may start inside the previous range
        }

        return files;
    }

    private static List<string> Canonical(IEnumerable<ParsedStatementRow> rows) =>
        rows.Select(r => $"{r.Date:yyyy-MM-dd}|{r.Amount.Cents}|{r.Description}|{r.Serial}").Order(StringComparer.Ordinal).ToList();
}
