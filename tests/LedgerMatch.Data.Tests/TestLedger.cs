using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Setup;
using LedgerMatch.Core.Tests.Importing;
using Microsoft.Extensions.Time.Testing;

namespace LedgerMatch.Data.Tests;

/// <summary>Real SQLite store plus the application services, wired the way the app wires them.</summary>
public sealed class TestLedger : IDisposable
{
    private readonly TestDatabase _db = new();

    public TestLedger()
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.FromHours(8)));
        Reopen();
    }

    public FakeTimeProvider Time { get; }

    public SqliteLedgerStore Store { get; private set; } = null!;

    public LedgerCommandExecutor Executor { get; private set; } = null!;

    public UndoRedoService History { get; private set; } = null!;

    public SetupService Setup { get; private set; } = null!;

    public StatementImportService Statements { get; private set; } = null!;

    /// <summary>Simulates closing and restarting the app on the same file.</summary>
    public void Reopen()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Store = _db.OpenStore();
        Executor = new LedgerCommandExecutor(Store, Time);
        History = new UndoRedoService(Executor);
        Setup = new SetupService(Store, Store, History);
        Statements = new StatementImportService(Store, Store, Executor, Time);
    }

    public Account CreateAccount(MappingProfile profile, string name = "測試帳戶", long openingCents = 0)
    {
        var saved = Setup.SaveProfile(profile with { Id = 0 });
        return Setup.CreateAccount(name, saved.Id, Money.FromCents(openingCents), new DateOnly(2026, 1, 1));
    }

    public Account BankA() => CreateAccount(TestProfiles.BankA, "甲銀行帳戶");

    public Account BankB() => CreateAccount(TestProfiles.BankB, "乙銀行帳戶");

    public ImportSummary ImportUtf8(Account account, string csv, string name = "a.csv") =>
        Statements.Import(account.Id, TestProfiles.Utf8(csv), name);

    public ImportSummary ImportBig5(Account account, string csv, string name = "b.csv") =>
        Statements.Import(account.Id, TestProfiles.Big5(csv), name);

    public void Dispose() => _db.Dispose();
}
