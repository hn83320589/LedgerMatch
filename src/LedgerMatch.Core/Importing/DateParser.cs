using System.Globalization;

namespace LedgerMatch.Core.Importing;

public static class DateParser
{
    private const int RocYearOffset = 1911;

    public static bool TryParse(string raw, IReadOnlyList<string> formats, bool rocYear, out DateOnly date, out string error)
    {
        date = default;
        error = string.Empty;
        var text = raw.Trim();

        if (text.Length == 0)
        {
            error = "日期空白";
            return false;
        }

        if (rocYear && !TryConvertRocYear(text, out text))
        {
            error = $"日期「{raw}」不是有效的民國年日期";
            return false;
        }

        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return true;
            }
        }

        error = $"日期「{raw}」不符合日期格式 {string.Join(" / ", formats)}";
        return false;
    }

    /// <summary>"115/03/01" → "2026/03/01"; "1150301" → "20260301".</summary>
    private static bool TryConvertRocYear(string text, out string converted)
    {
        converted = text;
        var yearLength = 0;
        while (yearLength < text.Length && char.IsAsciiDigit(text[yearLength]))
        {
            yearLength++;
        }

        if (yearLength == text.Length)
        {
            // Compact form: the last four digits are MMdd.
            yearLength = text.Length - 4;
        }

        if (yearLength is < 1 or > 3)
        {
            return false;
        }

        var rocYear = int.Parse(text.AsSpan(0, yearLength), CultureInfo.InvariantCulture);
        if (rocYear < 1)
        {
            return false;
        }

        converted = (rocYear + RocYearOffset).ToString("D4", CultureInfo.InvariantCulture) + text[yearLength..];
        return true;
    }
}
