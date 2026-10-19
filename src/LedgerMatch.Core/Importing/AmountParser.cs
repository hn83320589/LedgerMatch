using System.Globalization;

namespace LedgerMatch.Core.Importing;

/// <summary>Parses bank-style amount text ("1,234.50", "(300)", "NT$ 99", "100-") into exact cents.</summary>
public static class AmountParser
{
    private static readonly string[] CurrencyMarks = ["NT$", "NTD", "$", "元"];

    public static bool TryParse(string raw, char? thousandsSeparator, bool parenthesesNegative, out Money amount, out string error)
    {
        amount = Money.Zero;
        error = string.Empty;

        var text = raw.Trim();
        foreach (var mark in CurrencyMarks)
        {
            text = text.Replace(mark, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        text = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (text.Length == 0)
        {
            error = "金額空白";
            return false;
        }

        var negative = false;
        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            if (!parenthesesNegative)
            {
                error = $"金額「{raw}」使用括號，但設定檔未啟用括號負數";
                return false;
            }

            negative = true;
            text = text[1..^1];
        }

        if (text.StartsWith('-') || text.StartsWith('+'))
        {
            negative ^= text[0] == '-';
            text = text[1..];
        }
        else if (text.EndsWith('-'))
        {
            negative ^= true;
            text = text[..^1];
        }

        if (thousandsSeparator is { } sep)
        {
            text = text.Replace(sep.ToString(), string.Empty, StringComparison.Ordinal);
        }

        if (!IsPlainDecimal(text)
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            error = $"無法解析金額「{raw}」";
            return false;
        }

        if (value * 100m != decimal.Truncate(value * 100m))
        {
            error = $"金額「{raw}」超過小數點後兩位";
            return false;
        }

        amount = Money.FromDecimal(negative ? -value : value);
        return true;
    }

    private static bool IsPlainDecimal(string text)
    {
        var seenDot = false;
        var digits = 0;
        foreach (var c in text)
        {
            if (c == '.')
            {
                if (seenDot)
                {
                    return false;
                }

                seenDot = true;
            }
            else if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }
}
