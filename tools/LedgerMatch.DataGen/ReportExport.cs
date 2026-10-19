using LedgerMatch.Core;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Data;

namespace LedgerMatch.DataGen;

/// <summary>Exports a reconciliation without the UI, e.g. to check print layout in a browser.</summary>
public static class ReportExport
{
    public static void Write(string dbPath, string accountName, YearMonth month, string outDir)
    {
        var store = new SqliteLedgerStore(LedgerDatabase.Open(dbPath));
        var account = store.GetAccounts().SingleOrDefault(a => a.Name == accountName)
            ?? throw new InvalidOperationException($"no account named {accountName}");
        var report = new ReconciliationService(store, TimeProvider.System).Generate(account.Id, month);

        Directory.CreateDirectory(outDir);
        var name = $"reconciliation-{month}";
        File.WriteAllBytes(Path.Combine(outDir, name + ".csv"), ReconciliationCsv.Write(report));
        File.WriteAllText(Path.Combine(outDir, name + ".html"), ReconciliationHtml.Render(report));
        var items = report.DepositsInTransit.Items.Count + report.BankOnlyDeposits.Items.Count + report.BankOnlyWithdrawals.Items.Count
            + report.OutstandingItems.Items.Count + report.OtherBookItems.Items.Count;
        Console.WriteLine($"{account.Name} {month}: {items} items, balanced={report.IsBalanced} -> {outDir}");
    }
}
