namespace LedgerMatch.Core.Importing;

/// <summary>Parses a receipt (收款單) CSV using the same profile mechanism as statements.</summary>
public static class ReceiptFileParser
{
    public static ParseResult<ParsedReceiptRow> Parse(Stream stream, MappingProfile profile)
    {
        var table = ProfileTable.Load(stream, profile, ProfileKind.Receipt);
        var rows = new List<ParsedReceiptRow>();
        var errors = new List<RowError>();

        foreach (var record in table.DataRecords)
        {
            if (TryParseRow(table, profile, record, out var row, out var error))
            {
                rows.Add(row);
            }
            else
            {
                errors.Add(new RowError(record.LineNumber, error));
            }
        }

        return new ParseResult<ParsedReceiptRow>(rows, errors);
    }

    private static bool TryParseRow(ProfileTable table, MappingProfile profile, CsvRecord record, out ParsedReceiptRow row, out string error)
    {
        row = null!;
        if (record.Error is not null)
        {
            error = record.Error;
            return false;
        }

        if (!table.TryGet(record, profile.Date, out var dateText, out error)
            || !table.TryGet(record, profile.Customer, out var customer, out error)
            || !table.TryGet(record, profile.ReceiptNo, out var receiptNo, out error)
            || !table.TryGet(record, profile.Amount, out var amountText, out error)
            || !table.TryGet(record, profile.Note, out var note, out error)
            || !table.TryGet(record, profile.AccountName, out var accountName, out error))
        {
            return false;
        }

        if (!DateParser.TryParse(dateText!, profile.DateFormats, profile.RocYear, out var date, out error)
            || !AmountParser.TryParse(amountText!, profile.ThousandsSeparator, profile.ParenthesesNegative, out var amount, out error))
        {
            return false;
        }

        if (!amount.IsPositive)
        {
            error = $"收款單金額必須大於零「{amountText}」";
            return false;
        }

        if (string.IsNullOrEmpty(receiptNo))
        {
            error = "單據編號空白";
            return false;
        }

        if (string.IsNullOrEmpty(customer))
        {
            error = "客戶名稱空白";
            return false;
        }

        row = new ParsedReceiptRow(
            record.LineNumber, date, customer, receiptNo, amount, string.IsNullOrEmpty(note) ? null : note, string.IsNullOrEmpty(accountName) ? null : accountName);
        return true;
    }
}
