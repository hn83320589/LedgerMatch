using LedgerMatch.Core.Importing;
using static LedgerMatch.Core.Tests.Importing.TestProfiles;

namespace LedgerMatch.Core.Tests.Importing;

public class StatementFileParserTests
{
    private const string BankAFile =
        "交易日期,摘要,金額,交易序號,備註\r\n" +
        "2026/03/01,現金存入,\"12,345.00\",A0001,\r\n" +
        "2026/03/02,房租,\"(1,200.50)\",A0002,三月\r\n" +
        "2026/03/02,\"刷卡撥款,聯合\",8000,A0003,\r\n" +
        "2026/03/03,手續費,-15,A0004,\r\n";

    private const string BankBFile =
        "乙銀行 存款交易明細\r\n" +
        "日期,摘要,提款,存款,備註\r\n" +
        "115/03/01,現金存入,,\"3,000\",\r\n" +
        "115/03/05,水電費,\"1,234.56\",,台電\r\n" +
        "115/03/31,利息,,0.75,\r\n";

    [Fact]
    public void BankA_SignedAmountsWithThousandsAndParentheses()
    {
        var result = StatementFileParser.Parse(Utf8(BankAFile), BankA);

        Assert.Empty(result.Errors);
        Assert.Equal(
            [
                (new DateOnly(2026, 3, 1), 1234500L, "現金存入", "A0001", (string?)null),
                (new DateOnly(2026, 3, 2), -120050L, "房租", "A0002", "三月"),
                (new DateOnly(2026, 3, 2), 800000L, "刷卡撥款,聯合", "A0003", null),
                (new DateOnly(2026, 3, 3), -1500L, "手續費", "A0004", null),
            ],
            result.Rows.Select(r => (r.Date, r.Amount.Cents, r.Description, r.Serial, r.Note)));
        Assert.Equal([2, 3, 4, 5], result.Rows.Select(r => r.LineNumber));
    }

    [Fact]
    public void BankA_AcceptsUtf8Bom()
    {
        var result = StatementFileParser.Parse(Utf8(BankAFile, bom: true), BankA);
        Assert.Equal(4, result.Rows.Count);
    }

    [Fact]
    public void BankB_Big5DebitCreditAndRocDates()
    {
        var result = StatementFileParser.Parse(Big5(BankBFile), BankB);

        Assert.Empty(result.Errors);
        Assert.Equal(
            [
                (new DateOnly(2026, 3, 1), 300000L, "現金存入", (string?)null, (string?)null),
                (new DateOnly(2026, 3, 5), -123456L, "水電費", null, "台電"),
                (new DateOnly(2026, 3, 31), 75L, "利息", null, null),
            ],
            result.Rows.Select(r => (r.Date, r.Amount.Cents, r.Description, r.Serial, r.Note)));
        Assert.Equal([3, 4, 5], result.Rows.Select(r => r.LineNumber));
    }

    [Fact]
    public void BadRowsReportLineAndReason_OtherRowsStillParse()
    {
        var file =
            "交易日期,摘要,金額,交易序號,備註\n" +
            "2026/03/01,ok,100,A1,\n" +
            "03/02/2026,wrong date,100,A2,\n" +
            "2026/03/03,bad amount,12x,A3,\n" +
            "2026/03/04,short row\n" +
            "2026/03/05,zero,0,A5,\n" +
            "2026/03/06,ok2,200,A6,\n";

        var result = StatementFileParser.Parse(Utf8(file), BankA);

        Assert.Equal(["ok", "ok2"], result.Rows.Select(r => r.Description));
        Assert.Equal([3, 4, 5, 6], result.Errors.Select(e => e.LineNumber));
        Assert.Contains("日期", result.Errors[0].Reason);
        Assert.Contains("金額", result.Errors[1].Reason);
        Assert.Contains("不存在", result.Errors[2].Reason);
        Assert.Contains("零", result.Errors[3].Reason);
    }

    [Fact]
    public void DateFormatNotMatchingData_FlagsEveryRowWithLineNumbers()
    {
        var profile = BankA with { DateFormats = ["yyyy-MM-dd"] };

        var result = StatementFileParser.Parse(Utf8(BankAFile), profile);

        Assert.Empty(result.Rows);
        Assert.Equal([2, 3, 4, 5], result.Errors.Select(e => e.LineNumber));
        Assert.All(result.Errors, e => Assert.Contains("yyyy-MM-dd", e.Reason));
    }

    [Theory]
    [InlineData("115/03/01,x,100,200,", "同時")]
    [InlineData("115/03/01,x,,,", "皆無")]
    [InlineData("115/03/01,x,-5,,", "負數")]
    public void DebitCredit_RejectsInconsistentRows(string row, string reason)
    {
        var result = StatementFileParser.Parse(Big5("title\n日期,摘要,提款,存款,備註\n" + row), BankB);
        Assert.Contains(reason, Assert.Single(result.Errors).Reason);
    }

    [Fact]
    public void WrongEncoding_ThrowsClearFileError()
    {
        var ex = Assert.Throws<ImportFileException>(() => StatementFileParser.Parse(Big5(BankBFile), BankB with { Encoding = TextEncodingKind.Utf8 }));
        Assert.Contains("UTF-8", ex.Message);
    }

    [Fact]
    public void MissingHeaderColumn_ThrowsWithAvailableColumns()
    {
        var ex = Assert.Throws<ImportFileException>(() => StatementFileParser.Parse(Utf8(BankAFile), BankA with { Description = ColumnRef.ByName("說明") }));
        Assert.Contains("說明", ex.Message);
        Assert.Contains("摘要", ex.Message);
    }

    [Fact]
    public void InvalidProfile_Throws()
    {
        Assert.Throws<ImportFileException>(() => StatementFileParser.Parse(Utf8(BankAFile), BankA with { Amount = null }));
    }

    [Fact]
    public void ReceiptProfile_IsRejectedForStatements()
    {
        Assert.Throws<ImportFileException>(() => StatementFileParser.Parse(Utf8(BankAFile), Receipts));
    }

    [Fact]
    public void EmptySerial_IsRowError()
    {
        var result = StatementFileParser.Parse(Utf8("交易日期,摘要,金額,交易序號,備註\n2026/03/01,x,1,,\n"), BankA);
        Assert.Contains("序號", Assert.Single(result.Errors).Reason);
    }

    [Fact]
    public void ColumnsByIndex_WorkWithoutHeader()
    {
        var profile = BankA with
        {
            HeaderRowCount = 0,
            Date = ColumnRef.ByIndex(1),
            Description = ColumnRef.ByIndex(2),
            Amount = ColumnRef.ByIndex(3),
            Serial = null,
            Note = null,
            Delimiter = ';',
        };

        var result = StatementFileParser.Parse(Utf8("2026/03/01;x;1.5\n"), profile);

        Assert.Equal(150, Assert.Single(result.Rows).Amount.Cents);
    }
}
