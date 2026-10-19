using LedgerMatch.Core.Importing;

namespace LedgerMatch.DataGen;

/// <summary>The mapping profiles that read the files under samples/ (see samples/expected-results.md).</summary>
public static class SampleProfiles
{
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
        DateFormats = ["yyyy-MM-dd"],
        Amount = ColumnRef.ByName("金額"),
        Customer = ColumnRef.ByName("客戶名稱"),
        ReceiptNo = ColumnRef.ByName("單據編號"),
        Note = ColumnRef.ByName("備註"),
        AccountName = ColumnRef.ByName("入帳帳戶"),
    };
}
