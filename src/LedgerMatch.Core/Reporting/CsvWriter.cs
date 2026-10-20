using System.Text;

namespace LedgerMatch.Core.Reporting;

/// <summary>RFC 4180 writer producing UTF-8 with BOM (so Excel opens Chinese text correctly) and CRLF line endings.</summary>
public sealed class CsvWriter
{
    private readonly StringBuilder _text = new();

    public CsvWriter Row(params string?[] cells)
    {
        _text.Append(string.Join(',', cells.Select(Escape))).Append("\r\n");
        return this;
    }

    public byte[] ToBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_text.ToString())];

    public override string ToString() => _text.ToString();

    /// <summary>
    /// Text that came from outside (bank memo, customer, receipt number, account name) is prefixed with an
    /// apostrophe when it starts like a formula, so Excel shows it as text instead of evaluating it.
    /// Amount and date columns must not go through this: negative amounts start with '-' and stay numeric.
    /// </summary>
    public static string SafeText(string? text)
    {
        text ??= string.Empty;
        return text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;
    }

    public static string Escape(string? cell)
    {
        cell ??= string.Empty;
        var needsQuotes = cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 || cell.Length != cell.Trim().Length;
        return needsQuotes ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
    }
}
