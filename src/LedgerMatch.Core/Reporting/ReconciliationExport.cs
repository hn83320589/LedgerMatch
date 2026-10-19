using System.Globalization;
using System.Net;
using System.Text;

namespace LedgerMatch.Core.Reporting;

/// <summary>One printable line of the reconciliation, shared by the screen, CSV and HTML so all three agree.</summary>
public sealed record ReconciliationLine(ReconciliationLineKind Kind, string Label, ReconciliationItem? Item, Money? Amount);

public enum ReconciliationLineKind
{
    Heading,
    Balance,
    Section,
    Item,
    Total,
    Note,
}

public static class ReconciliationLayout
{
    /// <summary>The reconciliation as an ordered list of lines (bank side, book side, difference).</summary>
    public static IReadOnlyList<ReconciliationLine> Lines(ReconciliationReport report)
    {
        var lines = new List<ReconciliationLine>
        {
            new(ReconciliationLineKind.Heading, "銀行端", null, null),
            new(ReconciliationLineKind.Balance, report.StatementBalance is null ? "銀行期末餘額（依已匯入交易計算）" : "銀行對帳單期末餘額（使用者輸入）", null, report.BankBalance),
        };
        AddSection(lines, "＋", report.DepositsInTransit);
        AddSection(lines, "＋", report.OtherBookItems);
        AddSection(lines, "－", report.OutstandingItems);
        lines.Add(new(ReconciliationLineKind.Total, "調整後銀行餘額", null, report.AdjustedBankBalance));

        lines.Add(new(ReconciliationLineKind.Heading, "帳面端", null, null));
        lines.Add(new(ReconciliationLineKind.Balance, "帳面期末餘額", null, report.BookBalance));
        AddSection(lines, "＋", report.BankOnlyDeposits);
        AddSection(lines, "－", report.BankOnlyWithdrawals);
        lines.Add(new(ReconciliationLineKind.Total, "調整後帳面餘額", null, report.AdjustedBookBalance));

        lines.Add(new(ReconciliationLineKind.Total, report.IsBalanced ? "差異（已平衡）" : "差異（未平衡）", null, report.Difference));
        lines.AddRange(report.DifferenceNotes.Select(n => new ReconciliationLine(ReconciliationLineKind.Note, n, null, null)));
        return lines;
    }

    private static void AddSection(List<ReconciliationLine> lines, string sign, ReconciliationSection section)
    {
        lines.Add(new(ReconciliationLineKind.Section, $"{sign} {section.Title}（{section.Items.Count} 筆）", null, section.Total));
        lines.AddRange(section.Items.Select(i => new ReconciliationLine(ReconciliationLineKind.Item, i.Description, i, i.Amount)));
    }
}

public static class ReconciliationCsv
{
    public static byte[] Write(ReconciliationReport report)
    {
        var csv = new CsvWriter()
            .Row("銀行餘額調節表")
            .Row("帳戶", report.AccountName)
            .Row("月份", report.Month.ToString())
            .Row("產生時間", report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Row()
            .Row("項目", "日期", "參考", "說明", "金額", "備註");

        foreach (var line in ReconciliationLayout.Lines(report))
        {
            switch (line.Kind)
            {
                case ReconciliationLineKind.Item:
                    var item = line.Item!;
                    csv.Row(string.Empty, item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), item.Reference, item.Description, item.Amount.ToPlainString(), item.Note);
                    break;
                case ReconciliationLineKind.Heading or ReconciliationLineKind.Note:
                    csv.Row(line.Label);
                    break;
                default:
                    csv.Row(line.Label, string.Empty, string.Empty, string.Empty, line.Amount?.ToPlainString());
                    break;
            }
        }

        return csv.ToBytes();
    }
}

/// <summary>
/// Self-contained printable HTML sized for one A4 portrait page. Long item lists are cut to fit the page:
/// every non-empty section first gets up to <see cref="MinRowsPerSection"/> rows, the rest of the budget goes to
/// sections in order, and each cut section says how many items are omitted and that the CSV export has them all.
/// </summary>
public static class ReconciliationHtml
{
    public const int DefaultMaxItemRows = 28;

    public const int MinRowsPerSection = 4;

    public static string Render(ReconciliationReport report, int maxItemRows = DefaultMaxItemRows)
    {
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html>
            <html lang="zh-Hant">
            <head>
            <meta charset="utf-8">
            <title>
            """);
        html.Append(E($"銀行餘額調節表 {report.AccountName} {report.Month}"));
        html.Append("""
            </title>
            <style>
            @page { size: A4 portrait; margin: 12mm; }
            * { box-sizing: border-box; }
            html, body { margin: 0; padding: 0; }
            body { font-family: "Microsoft JhengHei", "Noto Sans TC", "PingFang TC", sans-serif; font-size: 9.5pt; color: #000; }
            .page { width: 186mm; max-height: 272mm; overflow: hidden; }
            h1 { font-size: 15pt; margin: 0 0 2mm; }
            .meta { margin: 0 0 3mm; color: #333; }
            table { width: 100%; border-collapse: collapse; table-layout: fixed; }
            td { padding: 0.6mm 1.5mm; vertical-align: top; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            col.date { width: 26mm; } col.ref { width: 26mm; } col.amount { width: 32mm; }
            td.amount { text-align: right; font-variant-numeric: tabular-nums; }
            tr.heading td { font-weight: bold; font-size: 11pt; padding-top: 2.5mm; border-bottom: 0.4mm solid #000; }
            tr.section td { font-weight: bold; padding-top: 1.2mm; }
            tr.item td { color: #222; font-size: 8.5pt; }
            tr.item td:first-child { padding-left: 5mm; }
            tr.total td { font-weight: bold; border-top: 0.3mm solid #000; }
            tr.balance td { font-weight: bold; }
            tr.note td, tr.omitted td { font-size: 8.5pt; color: #444; white-space: normal; }
            .unbalanced { color: #b00020; }
            </style>
            </head>
            <body>
            <div class="page">
            <h1>銀行餘額調節表</h1>
            """);
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"meta\">帳戶：{E(report.AccountName)}　月份：{E(report.Month.ToString())}　產生時間：{E(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}</p>\n");
        html.Append("<table>\n<colgroup><col class=\"date\"><col class=\"ref\"><col><col class=\"amount\"></colgroup>\n");

        var lines = ReconciliationLayout.Lines(report);
        var quotas = Quotas(SectionSizes(lines), maxItemRows);
        var section = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Kind == ReconciliationLineKind.Item)
            {
                var sectionItems = new List<ReconciliationLine>();
                while (i < lines.Count && lines[i].Kind == ReconciliationLineKind.Item)
                {
                    sectionItems.Add(lines[i++]);
                }

                i--;
                var shown = sectionItems.Take(quotas[section++]).ToList();
                foreach (var row in shown)
                {
                    var item = row.Item!;
                    html.Append(CultureInfo.InvariantCulture, $"<tr class=\"item\"><td>{E(item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}</td><td>{E(item.Reference)}</td><td>{E(item.Description)}{(item.Note is null ? string.Empty : "（" + E(item.Note) + "）")}</td><td class=\"amount\">{E(item.Amount.ToString())}</td></tr>\n");
                }

                var omitted = sectionItems.Skip(shown.Count).ToList();
                if (omitted.Count > 0)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<tr class=\"omitted\"><td colspan=\"3\">…其餘 {omitted.Count} 筆（合計 {E(omitted.Sum(o => o.Amount!.Value).ToString())}）因版面限制未列出，完整明細請見 CSV 匯出。</td><td></td></tr>\n");
                }

                continue;
            }

            var css = line.Kind.ToString().ToLowerInvariant();
            var amount = line.Amount is { } a ? E(a.ToString()) : string.Empty;
            var unbalanced = line.Label.StartsWith("差異", StringComparison.Ordinal) && !report.IsBalanced ? " unbalanced" : string.Empty;
            html.Append(CultureInfo.InvariantCulture, $"<tr class=\"{css}{unbalanced}\"><td colspan=\"3\">{E(line.Label)}</td><td class=\"amount\">{amount}</td></tr>\n");
        }

        html.Append("</table>\n</div>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Row budget per item group: a fair minimum for each, then the remainder in order.</summary>
    internal static int[] Quotas(IReadOnlyList<int> sizes, int budget)
    {
        var quotas = new int[sizes.Count];
        for (var i = 0; i < sizes.Count && budget > 0; i++)
        {
            quotas[i] = Math.Min(Math.Min(sizes[i], MinRowsPerSection), budget);
            budget -= quotas[i];
        }

        for (var i = 0; i < sizes.Count && budget > 0; i++)
        {
            var extra = Math.Min(sizes[i] - quotas[i], budget);
            quotas[i] += extra;
            budget -= extra;
        }

        return quotas;
    }

    private static List<int> SectionSizes(IReadOnlyList<ReconciliationLine> lines)
    {
        var sizes = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == ReconciliationLineKind.Item && (i == 0 || lines[i - 1].Kind != ReconciliationLineKind.Item))
            {
                sizes.Add(0);
            }

            if (lines[i].Kind == ReconciliationLineKind.Item)
            {
                sizes[^1]++;
            }
        }

        return sizes;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
