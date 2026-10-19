using System.Globalization;
using System.Text;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Importing;

public sealed record PlannedRow(ParsedStatementRow Row, string DedupKey);

public sealed record ImportPlan(
    IReadOnlyList<PlannedRow> ToInsert,
    IReadOnlyList<PlannedRow> ToPending,
    IReadOnlyList<PlannedRow> Duplicates);

/// <summary>
/// Idempotency rules (see README "冪等判斷"):
/// <list type="bullet">
/// <item>With a serial column the key is the serial.</item>
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

    public static ImportPlan Plan(
        IReadOnlyList<ParsedStatementRow> rowsInFileOrder,
        bool hasSerial,
        IReadOnlySet<string> existingTransactionKeys,
        IReadOnlySet<string> existingPendingKeys,
        IReadOnlySet<YearMonth> closedPeriods)
    {
        var keys = BuildKeys(rowsInFileOrder, hasSerial);
        var seen = new HashSet<string>();
        var insert = new List<PlannedRow>();
        var pending = new List<PlannedRow>();
        var duplicates = new List<PlannedRow>();

        for (var i = 0; i < rowsInFileOrder.Count; i++)
        {
            var planned = new PlannedRow(rowsInFileOrder[i], keys[i]);
            if (!seen.Add(planned.DedupKey)
                || existingTransactionKeys.Contains(planned.DedupKey)
                || existingPendingKeys.Contains(planned.DedupKey))
            {
                duplicates.Add(planned);
            }
            else if (closedPeriods.Contains(YearMonth.Of(planned.Row.Date)))
            {
                pending.Add(planned);
            }
            else
            {
                insert.Add(planned);
            }
        }

        return new ImportPlan(insert, pending, duplicates);
    }
}
