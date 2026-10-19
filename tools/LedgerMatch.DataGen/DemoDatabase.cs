using LedgerMatch.Core;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Receivables;
using LedgerMatch.Core.Setup;
using LedgerMatch.Data;

namespace LedgerMatch.DataGen;

/// <summary>Builds a database from samples/ through the real services, for screenshots and manual testing.</summary>
public static class DemoDatabase
{
    public static void Create(string dbPath, string samplesDir)
    {
        if (File.Exists(dbPath))
        {
            throw new InvalidOperationException($"{dbPath} already exists; choose a new path.");
        }

        var store = new SqliteLedgerStore(LedgerDatabase.Open(dbPath));
        var time = TimeProvider.System;
        var executor = new LedgerCommandExecutor(store, time);
        var history = new UndoRedoService(executor);
        var setup = new SetupService(store, store, history);
        var statements = new StatementImportService(store, store, executor, time);
        var receipts = new ReceiptService(store, store, executor, history, time);
        var matching = new MatchingService(store, store, history, new SettingsService(store, store, history));
        var categories = new CategoryService(store, store, history);
        var rules = new RuleService(store, store, history);

        var a = setup.CreateAccount("甲銀行帳戶", setup.SaveProfile(SampleProfiles.BankA).Id, Money.FromCents(5_000_000), new DateOnly(2026, 1, 1));
        var b = setup.CreateAccount("乙銀行帳戶", setup.SaveProfile(SampleProfiles.BankB).Id, Money.FromCents(2_000_000), new DateOnly(2026, 1, 1));
        var receiptProfile = setup.SaveProfile(SampleProfiles.Receipts);

        foreach (var (account, file) in new[]
        {
            (a, "bank-a-2026-03-part1.csv"), (a, "bank-a-2026-03-part2.csv"), (b, "bank-b-2026-03-part1.csv"), (b, "bank-b-2026-03-part2.csv"),
        })
        {
            using var stream = File.OpenRead(Path.Combine(samplesDir, file));
            var summary = statements.Import(account.Id, stream, file);
            Console.WriteLine($"{file}: +{summary.Added} skipped {summary.SkippedDuplicates} errors {summary.Errors.Count}");
        }

        using (var stream = File.OpenRead(Path.Combine(samplesDir, "receipts-2026-03.csv")))
        {
            var summary = receipts.Import(null, receiptProfile.Id, stream, "receipts-2026-03.csv");
            Console.WriteLine($"receipts: +{summary.Added} errors {summary.Errors.Count}");
        }

        var utilities = categories.Add("水電費");
        var rent = categories.Add("租金");
        categories.Add("文具");
        rules.Save(new Rule(0, "水電", 0, null, "^(水電|電信)", null, Money.FromCents(-1), null, utilities.Id, true));
        rules.Save(new Rule(0, "房租", 0, "房租", null, null, null, null, rent.Id, true));

        var march = new YearMonth(2026, 3);
        foreach (var account in new[] { a, b })
        {
            var result = matching.Run(account.Id, march);
            Console.WriteLine($"{account.Name}: confirmed {result.Confirmed}, suggested {result.Suggested}");
        }
    }
}
