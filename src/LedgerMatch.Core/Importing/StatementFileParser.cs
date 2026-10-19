namespace LedgerMatch.Core.Importing;

/// <summary>Parses a bank statement CSV. Bad rows are reported with line number and reason; other rows still parse.</summary>
public static class StatementFileParser
{
    public static ParseResult<ParsedStatementRow> Parse(Stream stream, MappingProfile profile)
    {
        var table = ProfileTable.Load(stream, profile, ProfileKind.Statement);
        var rows = new List<ParsedStatementRow>();
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

        return new ParseResult<ParsedStatementRow>(rows, errors);
    }

    private static bool TryParseRow(ProfileTable table, MappingProfile profile, CsvRecord record, out ParsedStatementRow row, out string error)
    {
        row = null!;
        if (record.Error is not null)
        {
            error = record.Error;
            return false;
        }

        if (!table.TryGet(record, profile.Date, out var dateText, out error)
            || !table.TryGet(record, profile.Description, out var description, out error)
            || !table.TryGet(record, profile.Serial, out var serial, out error)
            || !table.TryGet(record, profile.Note, out var note, out error))
        {
            return false;
        }

        if (!DateParser.TryParse(dateText!, profile.DateFormats, profile.RocYear, out var date, out error))
        {
            return false;
        }

        if (!TryReadAmount(table, profile, record, out var amount, out error))
        {
            return false;
        }

        if (profile.Serial is not null && string.IsNullOrEmpty(serial))
        {
            error = "交易序號空白";
            return false;
        }

        row = new ParsedStatementRow(record.LineNumber, date, amount, description!, serial, NullIfEmpty(note));
        return true;
    }

    private static bool TryReadAmount(ProfileTable table, MappingProfile profile, CsvRecord record, out Money amount, out string error)
    {
        amount = Money.Zero;
        if (profile.AmountMode == AmountMode.Signed)
        {
            if (!table.TryGet(record, profile.Amount, out var text, out error)
                || !AmountParser.TryParse(text!, profile.ThousandsSeparator, profile.ParenthesesNegative, out amount, out error))
            {
                return false;
            }

            if (amount == Money.Zero)
            {
                error = "金額為零";
                return false;
            }

            return true;
        }

        if (!table.TryGet(record, profile.Debit, out var debitText, out error)
            || !table.TryGet(record, profile.Credit, out var creditText, out error)
            || !TryReadSide(debitText!, "借方", profile, out var debit, out error)
            || !TryReadSide(creditText!, "貸方", profile, out var credit, out error))
        {
            return false;
        }

        if (debit != Money.Zero && credit != Money.Zero)
        {
            error = "借方與貸方同時有金額";
            return false;
        }

        if (debit == Money.Zero && credit == Money.Zero)
        {
            error = "借方與貸方皆無金額";
            return false;
        }

        amount = credit - debit;
        return true;
    }

    private static bool TryReadSide(string text, string label, MappingProfile profile, out Money value, out string error)
    {
        value = Money.Zero;
        error = string.Empty;
        if (text.Length == 0)
        {
            return true;
        }

        if (!AmountParser.TryParse(text, profile.ThousandsSeparator, profile.ParenthesesNegative, out value, out error))
        {
            error = $"{label}：{error}";
            return false;
        }

        if (value.IsNegative)
        {
            error = $"{label}金額不可為負數「{text}」";
            return false;
        }

        return true;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
