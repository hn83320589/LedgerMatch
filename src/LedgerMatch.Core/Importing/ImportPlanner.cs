using System.Globalization;
using System.Text;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Importing;

public sealed record PlannedRow(ParsedStatementRow Row, string DedupKey);

/// <summary>A transaction or pending item already stored under some dedup key.</summary>
public sealed record ExistingRow(DateOnly Date, Money Amount, string Description);

public sealed record ImportPlan(
    IReadOnlyList<PlannedRow> ToInsert,
    IReadOnlyList<PlannedRow> ToPending,
    IReadOnlyList<PlannedRow> Duplicates,
    IReadOnlyList<RowError> Conflicts);

/// <summary>
/// Idempotency rules (see README "冪等判斷"):
/// <list type="bullet">
/// <item>With a serial column the key is the serial, assumed unique within the account. A row whose serial is
/// already used (in the database or earlier in the file) is a duplicate when date, amount and normalised
/// description are the same, and an error row otherwise — never silently dropped.</item>
/// <item>Without one, the key is (date, amount, normalised description, occurrence), where occurrence is the
/// 1-based count of that triple in file row order. Re-importing or overlapping files therefore keep
/// max(count per file) rows per triple, which is idempotent and independent of import order, while
/// genuine same-day/same-amount/same-description rows inside one file stay distinct.</item>
/// </list>
/// </summary>
public static class ImportPlanner
{
    public static string NormalizeText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(normalized.Length);
        var lastWasSpace = false;
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> BuildKeys(IReadOnlyList<ParsedStatementRow> rowsInFileOrder, bool hasSerial)
    {
        var keys = new List<string>(rowsInFileOrder.Count);
        var occurrences = new Dictionary<string, int>();
        foreach (var row in rowsInFileOrder)
        {
            if (hasSerial)
            {
                keys.Add("S|" + NormalizeText(row.Serial!));
                continue;
            }

            var triple = string.Join(
                '|',
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.Amount.Cents.ToString(CultureInfo.InvariantCulture),
                NormalizeText(row.Description));
            occurrences[triple] = occurrences.GetValueOrDefault(triple) + 1;
            keys.Add($"N|{triple}|{occurrences[triple]}");
        }

        return keys;
    }

    /// <param name="existing">Stored transactions and pending items of the account, by dedup key; it must contain
    /// every key of the file that is already stored, whatever its date.</param>
    public static ImportPlan Plan(
        IReadOnlyList<ParsedStatementRow> rowsInFileOrder,
        bool hasSerial,
        IReadOnlyDictionary<string, ExistingRow> existing,
        IReadOnlySet<YearMonth> closedPeriods)
    {
        var keys = BuildKeys(rowsInFileOrder, hasSerial);
        var firstInFile = new Dictionary<string, ParsedStatementRow>();
        var insert = new List<PlannedRow>();
        var pending = new List<PlannedRow>();
        var duplicates = new List<PlannedRow>();
        var conflicts = new List<RowError>();

        for (var i = 0; i < rowsInFileOrder.Count; i++)
        {
            var planned = new PlannedRow(rowsInFileOrder[i], keys[i]);
            var row = planned.Row;

            // Keys without a serial contain date, amount and description, so a match is always the same transaction.
            if (firstInFile.TryGetValue(planned.DedupKey, out var earlier))
            {
                if (!hasSerial || SameTransaction(row, earlier.Date, earlier.Amount, earlier.Description))
                {
                    duplicates.Add(planned);
                }
                else
                {
                    conflicts.Add(new RowError(row.LineNumber, $"交易序號「{row.Serial}」在本檔第 {earlier.LineNumber} 列已用於另一筆交易；序號在帳戶內必須唯一。"));
                }

                continue;
            }

            firstInFile[planned.DedupKey] = row;
            if (existing.TryGetValue(planned.DedupKey, out var stored))
            {
                if (!hasSerial || SameTransaction(row, stored.Date, stored.Amount, stored.Description))
                {
                    duplicates.Add(planned);
                }
                else
                {
                    conflicts.Add(new RowError(
                        row.LineNumber,
                        $"交易序號「{row.Serial}」已用於 {stored.Date:yyyy-MM-dd} 的另一筆交易（{stored.Amount}，{stored.Description}）；" +
                        "序號在帳戶內必須唯一，請確認對帳單或映射設定檔的序號欄。"));
                }
            }
            else if (closedPeriods.Contains(YearMonth.Of(row.Date)))
            {
                pending.Add(planned);
            }
            else
            {
                insert.Add(planned);
            }
        }

        return new ImportPlan(insert, pending, duplicates, conflicts);
    }

    private static bool SameTransaction(ParsedStatementRow row, DateOnly date, Money amount, string description) =>
        row.Date == date && row.Amount == amount && NormalizeText(row.Description) == NormalizeText(description);
}
