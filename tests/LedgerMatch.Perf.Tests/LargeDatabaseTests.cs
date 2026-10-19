using System.Diagnostics;
using LedgerMatch.Core;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Setup;
using LedgerMatch.Data;
using LedgerMatch.DataGen;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace LedgerMatch.Perf.Tests;

/// <summary>
/// Builds a 100,000-transaction database once (fixed seed) and measures the operations README promises.
/// Run with: dotnet test tests/LedgerMatch.Perf.Tests --filter Category=Perf
/// </summary>
public sealed class LargeDatabaseFixture : IDisposable
{
    public LargeDatabaseFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "LedgerMatchPerf", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        var watch = Stopwatch.StartNew();
        Result = BulkGenerator.CreateDatabase(Path.Combine(Directory, "big.db"), 100_000, seed: 42);
        GenerationTime = watch.Elapsed;
    }

    public string Directory { get; }

    public BulkGenerator.Result Result { get; }

    public TimeSpan GenerationTime { get; }

    public void Dispose()
    {
        LedgerDatabase.ReleasePool(Result.Path);
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder; the OS cleans it eventually.
        }
    }
}

[Trait("Category", "Perf")]
public class LargeDatabaseTests(LargeDatabaseFixture fixture, ITestOutputHelper output) : IClassFixture<LargeDatabaseFixture>
{
    /// <summary>Limits stated in README "效能"; measured values are far below them on a typical laptop.</summary>
    private static readonly TimeSpan ImportLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MatchingLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PageLimit = TimeSpan.FromMilliseconds(500);

    private (SqliteLedgerStore Store, StatementImportService Import, MatchingService Matching) Services()
    {
        var store = new SqliteLedgerStore(LedgerDatabase.Open(fixture.Result.Path));
        var executor = new LedgerCommandExecutor(store, TimeProvider.System);
        var history = new UndoRedoService(executor);
        return (store, new StatementImportService(store, store, executor, TimeProvider.System), new MatchingService(store, store, history, new SettingsService(store, store, history)));
    }

    [Fact]
    public void Database_Has100kTransactions()
    {
        var (store, _, _) = Services();
        Assert.Equal(100_000, fixture.Result.Transactions);
        Assert.Equal(100_000, store.CountTransactions(new TransactionFilter { To = new DateOnly(2026, 8, 31) }));
        output.WriteLine($"generated {fixture.Result} in {fixture.GenerationTime.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task ImportMonthOf5000Lines_IntoLargeDatabase_IsFastAndIdempotent()
    {
        var (store, import, _) = Services();
        var csv = BulkGenerator.MonthStatementCsv(new YearMonth(2026, 9), 5_000, seed: 7);
        var before = store.CountTransactions(new TransactionFilter());

        var watch = Stopwatch.StartNew();
        var first = await import.ImportAsync(fixture.Result.AccountId, new MemoryStream(csv), "2026-09.csv");
        var firstTime = watch.Elapsed;
        watch.Restart();
        var again = await import.ImportAsync(fixture.Result.AccountId, new MemoryStream(csv), "2026-09.csv");
        var againTime = watch.Elapsed;

        output.WriteLine($"import 5,000 lines into {fixture.Result.Transactions:N0}: {firstTime.TotalMilliseconds:F0} ms; re-import (all duplicates): {againTime.TotalMilliseconds:F0} ms");
        Assert.Equal((5_000, 0), (first.Added, first.Errors.Count));
        Assert.Equal((0, 5_000), (again.Added, again.SkippedDuplicates));
        Assert.True(firstTime < ImportLimit, $"import took {firstTime}");
        Assert.True(againTime < ImportLimit, $"re-import took {againTime}");
        Assert.Equal(before + 5_000, store.CountTransactions(new TransactionFilter()));
    }

    [Fact]
    public async Task MatchingABusyMonth_IsFast_AndListStaysQueryableMeanwhile()
    {
        var (store, _, matching) = Services();
        var month = new YearMonth(2026, 8);
        var deposits = store.GetUnmatchedDeposits(fixture.Result.AccountId, month).Count;

        var watch = Stopwatch.StartNew();
        var run = matching.RunAsync(fixture.Result.AccountId, month);

        // While the engine works in the background, the UI's list queries keep answering.
        var pageTimes = new List<TimeSpan>();
        while (!run.IsCompleted)
        {
            var page = Stopwatch.StartNew();
            var rows = store.FindTransactions(new TransactionFilter { AccountId = fixture.Result.AccountId, Period = month }, null, 200);
            pageTimes.Add(page.Elapsed);
            Assert.Equal(200, rows.Count);
            await Task.Delay(20);
        }

        var summary = await run;
        var elapsed = watch.Elapsed;
        output.WriteLine($"matching {deposits:N0} deposits in {month}: {elapsed.TotalMilliseconds:F0} ms, confirmed {summary.Confirmed:N0}, suggested {summary.Suggested:N0}; " +
            $"{pageTimes.Count} list queries during the run, slowest {pageTimes.DefaultIfEmpty().Max().TotalMilliseconds:F0} ms");
        Assert.True(summary.Confirmed > 1_000);
        Assert.True(elapsed < MatchingLimit, $"matching took {elapsed}");
        Assert.All(pageTimes, t => Assert.True(t < PageLimit, $"page query took {t}"));
    }

    [Fact]
    public void ListPaging_OnLargeDatabase_IsFast()
    {
        var (store, _, _) = Services();
        var filter = new TransactionFilter { AccountId = fixture.Result.AccountId, Keyword = "房租" };

        var watch = Stopwatch.StartNew();
        var count = store.CountTransactions(filter);
        var page = store.FindTransactions(filter, null, 200);
        var next = store.FindTransactions(filter, new TransactionCursor(page[^1].Transaction.TxnDate, page[^1].Transaction.Id), 200);
        var elapsed = watch.Elapsed;

        output.WriteLine($"keyword filter over all months: {count:N0} rows, count + 2 pages in {elapsed.TotalMilliseconds:F0} ms");
        Assert.True(count > 1_000);
        Assert.Equal(200, next.Count);
        Assert.True(elapsed < PageLimit * 2, $"paging took {elapsed}");
    }
}
