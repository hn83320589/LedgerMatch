using LedgerMatch.Core.Importing;
using static LedgerMatch.Core.Tests.Importing.TestProfiles;

namespace LedgerMatch.Core.Tests.Importing;

public class ReceiptFileParserTests
{
    [Fact]
    public void ParsesReceiptsAndReportsBadRows()
    {
        var file =
            "開立日期,客戶名稱,單據編號,金額,備註\n" +
            "2026-03-01,王小姐,R-0001,\"1,200\",生日蛋糕\n" +
            "2026/03/02,咖啡廳,R-0002,350.5,\n" +
            "2026-03-03,退款,R-0003,-100,\n" +
            "2026-03-04,,R-0004,100,\n" +
            "2026-03-05,某客戶,,100,\n";

        var result = ReceiptFileParser.Parse(Utf8(file), Receipts);

        Assert.Equal(
            [
                (new DateOnly(2026, 3, 1), "王小姐", "R-0001", 120000L, (string?)"生日蛋糕"),
                (new DateOnly(2026, 3, 2), "咖啡廳", "R-0002", 35050L, null),
            ],
            result.Rows.Select(r => (r.IssueDate, r.Customer, r.ReceiptNo, r.Amount.Cents, r.Note)));
        Assert.Equal([4, 5, 6], result.Errors.Select(e => e.LineNumber));
    }

    [Fact]
    public void StatementProfile_IsRejectedForReceipts()
    {
        Assert.Throws<ImportFileException>(() => ReceiptFileParser.Parse(Utf8("x"), BankA));
    }
}
