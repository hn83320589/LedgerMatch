using System.Globalization;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;

namespace LedgerMatch.Presentation;

/// <summary>Display and input conversions shared by view models.</summary>
public static class Formatting
{
    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Amount(Money money) => money.ToString();

    /// <summary>Parses user-typed money ("1,234.5", "-30"); returns false with a message on bad input.</summary>
    public static bool TryParseMoney(string? text, out Money money, out string error) =>
        AmountParser.TryParse(text ?? string.Empty, ',', parenthesesNegative: true, out money, out error);

    public static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy/M/d", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Column text: a header name, or "#3" for the third column; empty means "not used".</summary>
    public static string Column(ColumnRef? column) => column switch
    {
        null => string.Empty,
        { Index: { } i } => $"#{i}",
        _ => column.Name!,
    };

    public static ColumnRef? ParseColumn(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.StartsWith('#') && int.TryParse(text[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? ColumnRef.ByIndex(index)
            : ColumnRef.ByName(text);
    }

    public static string MatchKind(MatchKind kind) => kind switch
    {
        Core.Domain.MatchKind.Exact => "精確",
        Core.Domain.MatchKind.OneToMany => "一對多",
        Core.Domain.MatchKind.WithFee => "含手續費",
        _ => "人工",
    };

    public static string Confidence(MatchConfidence confidence) => confidence == MatchConfidence.Confirmed ? "已確認" : "建議";
}
