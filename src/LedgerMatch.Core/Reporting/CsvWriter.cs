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

    public static string Escape(string? cell)
    {
        cell ??= string.Empty;
        var needsQuotes = cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 || cell.Length != cell.Trim().Length;
        return needsQuotes ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
    }
}
