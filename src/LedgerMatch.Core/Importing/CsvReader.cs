using System.Text;

namespace LedgerMatch.Core.Importing;

/// <summary>One CSV record. <see cref="LineNumber"/> is the 1-based physical line where the record starts.</summary>
public sealed record CsvRecord(int LineNumber, IReadOnlyList<string> Fields, string? Error = null);

/// <summary>RFC 4180 reader: quoted fields, doubled quotes, embedded newlines, CRLF/LF/CR endings.</summary>
public static class CsvReader
{
    public static IEnumerable<CsvRecord> Read(TextReader reader, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordStartLine = 1;
        var inQuotes = false;
        var recordHasContent = false;

        while (true)
        {
            var next = reader.Read();
            if (next == -1)
            {
                break;
            }

            var c = (char)next;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n' || (c == '\r' && reader.Peek() != '\n'))
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                recordHasContent = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                recordHasContent = true;
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                if (recordHasContent || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    yield return new CsvRecord(recordStartLine, fields.ToArray());
                }

                fields.Clear();
                field.Clear();
                recordHasContent = false;
                line++;
                recordStartLine = line;
            }
            else
            {
                field.Append(c);
                recordHasContent = true;
            }
        }

        if (inQuotes)
        {
            fields.Add(field.ToString());
            yield return new CsvRecord(recordStartLine, fields.ToArray(), "引號未閉合（\" 沒有對應的結尾）");
        }
        else if (recordHasContent || field.Length > 0)
        {
            fields.Add(field.ToString());
            yield return new CsvRecord(recordStartLine, fields.ToArray());
        }
    }
}
