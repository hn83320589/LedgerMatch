using System.Text;
using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Tests.Importing;

/// <summary>The two statement styles used across tests (mirrors the files under samples/).</summary>
internal static class TestProfiles
{
    static TestProfiles()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>甲銀行: UTF-8, single signed amount, yyyy/MM/dd, thousands separator, parentheses negative, serial column.</summary>
    public static MappingProfile BankA => new()
    {
        Name = "甲銀行",
        Encoding = TextEncodingKind.Utf8,
        HeaderRowCount = 1,
        Date = ColumnRef.ByName("交易日期"),
        DateFormats = ["yyyy/MM/dd"],
        AmountMode = AmountMode.Signed,
        Amount = ColumnRef.ByName("金額"),
        Description = ColumnRef.ByName("摘要"),
        Serial = ColumnRef.ByName("交易序號"),
        Note = ColumnRef.ByName("備註"),
        ThousandsSeparator = ',',
        ParenthesesNegative = true,
    };

    /// <summary>乙銀行: Big5, title line + header, debit/credit columns, ROC dates, no serial column.</summary>
    public static MappingProfile BankB => new()
    {
        Name = "乙銀行",
        Encoding = TextEncodingKind.Big5,
        HeaderRowCount = 2,
        Date = ColumnRef.ByName("日期"),
        DateFormats = ["yyyy/MM/dd"],
        RocYear = true,
        AmountMode = AmountMode.DebitCredit,
        Debit = ColumnRef.ByName("提款"),
        Credit = ColumnRef.ByName("存款"),
        Description = ColumnRef.ByName("摘要"),
        Note = ColumnRef.ByIndex(5),
        ThousandsSeparator = ',',
        ParenthesesNegative = false,
    };

    public static MappingProfile Receipts => new()
    {
        Name = "收款單",
        Kind = ProfileKind.Receipt,
        Date = ColumnRef.ByName("開立日期"),
        DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd"],
        Amount = ColumnRef.ByName("金額"),
        Customer = ColumnRef.ByName("客戶名稱"),
        ReceiptNo = ColumnRef.ByName("單據編號"),
        Note = ColumnRef.ByName("備註"),
    };

    public static MemoryStream Utf8(string text, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new MemoryStream(bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes);
    }

    public static MemoryStream Big5(string text) => new(Encoding.GetEncoding(950).GetBytes(text));
}
