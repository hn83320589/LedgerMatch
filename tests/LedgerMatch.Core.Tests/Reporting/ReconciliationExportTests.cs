using System.Text;
using System.Text.RegularExpressions;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Reporting;
using Item = LedgerMatch.Core.Reporting.ReconciliationItem;

namespace LedgerMatch.Core.Tests.Reporting;

public class ReconciliationExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 5, 10, 0, 0, TimeSpan.FromHours(8));

    private static ReconciliationReport Report() => ReconciliationCalculator.Compute(ReconciliationCalculatorTests.HandExample(), Now);

    [Fact]
    public void Csv_HasUtf8BomAndRoundTripsThroughTheParser()
    {
        var bytes = ReconciliationCsv.Write(Report());

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        var records = CsvReader.Read(new StringReader(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)), ',').ToList();
        Assert.All(records, r => Assert.Null(r.Error));
        Assert.Contains(records, r => r.Fields.SequenceEqual(["帳戶", "甲銀行帳戶"]));
        Assert.Contains(records, r => r.Fields[0] == "調整後銀行餘額" && r.Fields[4] == "-994.25");
        Assert.Contains(records, r => r.Fields[0] == "調整後帳面餘額" && r.Fields[4] == "-994.25");
        Assert.Contains(records, r => r.Fields.Count > 2 && r.Fields[2] == "R15" && r.Fields[4] == "2500.00");
    }

    [Fact]
    public void Csv_EscapesCommasQuotesAndNewlines()
    {
        Assert.Equal("\"a,b\"", CsvWriter.Escape("a,b"));
        Assert.Equal("\"他說\"\"好\"\"\"", CsvWriter.Escape("他說\"好\""));
        Assert.Equal("\"line1\nline2\"", CsvWriter.Escape("line1\nline2"));
        Assert.Equal("\" pad\"", CsvWriter.Escape(" pad"));
        Assert.Equal("中文", CsvWriter.Escape("中文"));
    }

    [Fact]
    public void Csv_SpecialCharactersInDataSurviveRoundTrip()
    {
        var tricky = "王\"小姐\", 蛋糕\n訂金 <b>&</b>";
        var report = Report() with
        {
            DepositsInTransit = new ReconciliationSection("在途", [new ReconciliationItem(new DateOnly(2026, 3, 1), "R,1", tricky, Money.FromCents(100), null)]),
        };

        var bytes = ReconciliationCsv.Write(report);
        var records = CsvReader.Read(new StringReader(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)), ',').ToList();

        Assert.Contains(records, r => r.Fields.Count > 3 && r.Fields[2] == "R,1" && r.Fields[3] == tricky);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\",\"點我\")")]
    [InlineData("+1+2")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=1")]
    public void Csv_TextFromStatementsCannotBecomeAFormula_WhileAmountsStayNumeric(string hostile)
    {
        // Descriptions come from the bank (remitter's memo), customers and receipt numbers from users; the accountant opens the CSV in Excel.
        var report = Report() with
        {
            AccountName = hostile,
            BankOnlyWithdrawals = new ReconciliationSection("扣款", [new ReconciliationItem(new DateOnly(2026, 3, 1), hostile, hostile, Money.FromCents(-12_345), hostile)]),
        };

        var bytes = ReconciliationCsv.Write(report);
        var records = CsvReader.Read(new StringReader(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)), ',').ToList();

        Assert.Contains(records, r => r.Fields.SequenceEqual(["帳戶", "'" + hostile]));
        var item = Assert.Single(records, r => r.Fields.Count == 6 && r.Fields[1] == "2026-03-01");
        Assert.Equal(["'" + hostile, "'" + hostile, "-123.45", "'" + hostile], new[] { item.Fields[2], item.Fields[3], item.Fields[4], item.Fields[5] });
        Assert.Contains(records, r => r.Fields[0] == "調整後銀行餘額" && r.Fields[4] == "-994.25");
    }

    [Fact]
    public void Csv_SafeText_LeavesOrdinaryTextAlone()
    {
        Assert.Equal("刷卡撥款 -2", CsvWriter.SafeText("刷卡撥款 -2"));
        Assert.Equal("R-0301", CsvWriter.SafeText("R-0301"));
        Assert.Equal(string.Empty, CsvWriter.SafeText(null));
    }

    [Fact]
    public void Html_IsA4PortraitAndEscapesText()
    {
        var report = Report() with
        {
            AccountName = "甲銀行 <script>alert(1)</script> & \"引號\"",
        };

        var html = ReconciliationHtml.Render(report);

        Assert.Contains("@page { size: A4 portrait;", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;引號&quot;", html);
    }

    [Fact]
    public void Html_ShowsSameFiguresAsTheLayout()
    {
        var report = Report();
        var html = ReconciliationHtml.Render(report);

        foreach (var line in ReconciliationLayout.Lines(report).Where(l => l.Amount is not null))
        {
            Assert.Contains(System.Net.WebUtility.HtmlEncode(line.Amount!.Value.ToString()), html);
        }

        Assert.Contains("調整後銀行餘額", html);
        Assert.Contains("-994.25", html);
    }

    [Fact]
    public void Html_LongListsAreCutWithACountAndPointerToCsv()
    {
        var many = Enumerable.Range(1, 60)
            .Select(i => new ReconciliationItem(new DateOnly(2026, 3, 1 + (i % 28)), $"R{i:D3}", $"客戶{i}", Money.FromCents(100), null))
            .ToList();
        var report = Report() with { DepositsInTransit = new ReconciliationSection("在途存款", many) };

        var html = ReconciliationHtml.Render(report, maxItemRows: 28);

        Assert.Equal(28, Regex.Matches(html, "<tr class=\"item\">").Count);
        Assert.Contains("其餘 36 筆（合計 36.00）", html); // the other 4 sample items take their share of the 28 rows first
        Assert.Contains("CSV", html);
    }

    [Theory]
    [InlineData(new[] { 60, 50 }, 28, new[] { 24, 4 })]
    [InlineData(new[] { 2, 60, 60 }, 28, new[] { 2, 22, 4 })]
    [InlineData(new[] { 3, 1 }, 28, new[] { 3, 1 })]
    [InlineData(new[] { 10, 10, 10, 10, 10, 10, 10, 10 }, 28, new[] { 4, 4, 4, 4, 4, 4, 4, 0 })]
    public void Html_RowBudget_GivesEverySectionAShareFirst(int[] sizes, int budget, int[] expected)
    {
        Assert.Equal(expected, ReconciliationHtml.Quotas(sizes, budget));
    }

    [Fact]
    public void Html_TwoLongSections_BothShowRows()
    {
        Item[] Many(string prefix) => Enumerable.Range(1, 60).Select(i => new Item(new DateOnly(2026, 3, 1), $"{prefix}{i}", "x", Money.FromCents(100), null)).ToArray();
        var report = Report() with
        {
            DepositsInTransit = new ReconciliationSection("在途存款", Many("R")),
            BankOnlyDeposits = new ReconciliationSection("未配對入帳", Many("T")),
        };

        var html = ReconciliationHtml.Render(report);

        Assert.Contains(">T1<", html);
        Assert.Contains("其餘 38 筆", html); // 4 + 1 + 1 + 4 rows reserved, the remaining 18 go to the first section
        Assert.Contains("其餘 56 筆", html);
    }

    [Fact]
    public void Layout_ListsEveryItemOnce()
    {
        var report = Report();
        var items = ReconciliationLayout.Lines(report).Where(l => l.Kind == ReconciliationLineKind.Item).ToList();
        Assert.Equal(
            report.DepositsInTransit.Items.Count + report.OtherBookItems.Items.Count + report.OutstandingItems.Items.Count
                + report.BankOnlyDeposits.Items.Count + report.BankOnlyWithdrawals.Items.Count,
            items.Count);
    }
}
