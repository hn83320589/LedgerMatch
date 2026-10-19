namespace LedgerMatch.Core.Importing;

public enum ProfileKind
{
    Statement,
    Receipt,
}

public enum TextEncodingKind
{
    Utf8,
    Big5,
}

public enum AmountMode
{
    /// <summary>One amount column; positive = deposit, negative = withdrawal.</summary>
    Signed,

    /// <summary>Two columns: debit (withdrawal) and credit (deposit), both written as positive numbers.</summary>
    DebitCredit,
}

/// <summary>Reference to a CSV column, either by header name or by 1-based index.</summary>
public sealed record ColumnRef
{
    private ColumnRef(string? name, int? index)
    {
        Name = name;
        Index = index;
    }

    public string? Name { get; }

    public int? Index { get; }

    public static ColumnRef ByName(string name) => new(name, null);

    public static ColumnRef ByIndex(int oneBasedIndex) => new(null, oneBasedIndex);

    public override string ToString() => Name is not null ? $"「{Name}」" : $"第 {Index} 欄";
}

/// <summary>
/// Describes how to read one CSV layout. Bank accounts bind a <see cref="ProfileKind.Statement"/> profile;
/// receipt imports use a <see cref="ProfileKind.Receipt"/> profile.
/// </summary>
public sealed record MappingProfile
{
    public long Id { get; init; }

    public required string Name { get; init; }

    public ProfileKind Kind { get; init; } = ProfileKind.Statement;

    public TextEncodingKind Encoding { get; init; } = TextEncodingKind.Utf8;

    public char Delimiter { get; init; } = ',';

    /// <summary>Rows to skip before data. Column names are looked up in the last header row.</summary>
    public int HeaderRowCount { get; init; } = 1;

    public required ColumnRef Date { get; init; }

    /// <summary>.NET exact date patterns tried in order, e.g. "yyyy/MM/dd".</summary>
    public required IReadOnlyList<string> DateFormats { get; init; }

    /// <summary>Dates use the ROC (民國) year; the year is converted to Gregorian before matching a "yyyy" pattern.</summary>
    public bool RocYear { get; init; }

    public AmountMode AmountMode { get; init; } = AmountMode.Signed;

    public ColumnRef? Amount { get; init; }

    public ColumnRef? Debit { get; init; }

    public ColumnRef? Credit { get; init; }

    public ColumnRef? Description { get; init; }

    public ColumnRef? Serial { get; init; }

    public ColumnRef? Note { get; init; }

    public ColumnRef? Customer { get; init; }

    public ColumnRef? ReceiptNo { get; init; }

    /// <summary>Grouping separator to strip, or null when amounts never contain one.</summary>
    public char? ThousandsSeparator { get; init; } = ',';

    /// <summary>Treat "(1,234.00)" as a negative amount.</summary>
    public bool ParenthesesNegative { get; init; } = true;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("設定檔名稱不可空白。");
        }

        if (Delimiter is '"' or '\r' or '\n')
        {
            errors.Add("分隔符號不可為引號或換行。");
        }

        if (HeaderRowCount is < 0 or > 20)
        {
            errors.Add("標題列數必須介於 0 到 20。");
        }

        if (DateFormats.Count == 0 || DateFormats.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("至少需要一個日期格式。");
        }

        if (ThousandsSeparator is '.')
        {
            errors.Add("千分位符號不可為小數點。");
        }

        RequireColumn(errors, Date, "日期欄");

        if (Kind == ProfileKind.Statement)
        {
            RequireColumn(errors, Description, "摘要欄");
            if (AmountMode == AmountMode.Signed)
            {
                RequireColumn(errors, Amount, "金額欄");
            }
            else
            {
                RequireColumn(errors, Debit, "借方（支出）欄");
                RequireColumn(errors, Credit, "貸方（存入）欄");
            }
        }
        else
        {
            if (AmountMode != AmountMode.Signed)
            {
                errors.Add("收款單設定檔只支援單一金額欄。");
            }

            RequireColumn(errors, Amount, "金額欄");
            RequireColumn(errors, Customer, "客戶名稱欄");
            RequireColumn(errors, ReceiptNo, "單據編號欄");
        }

        foreach (var column in AllColumns())
        {
            if (column.Index is < 1)
            {
                errors.Add($"欄號必須從 1 開始：{column}。");
            }

            if (column.Name is not null && HeaderRowCount == 0)
            {
                errors.Add($"以標題名稱指定欄位 {column} 時，標題列數至少為 1。");
            }
        }

        return errors;
    }

    internal IEnumerable<ColumnRef> AllColumns() =>
        new[] { Date, Amount, Debit, Credit, Description, Serial, Note, Customer, ReceiptNo }.OfType<ColumnRef>();

    private static void RequireColumn(List<string> errors, ColumnRef? column, string label)
    {
        if (column is null)
        {
            errors.Add($"必須指定{label}。");
        }
    }
}
