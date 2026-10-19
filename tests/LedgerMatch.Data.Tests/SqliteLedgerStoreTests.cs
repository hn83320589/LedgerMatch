using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace LedgerMatch.Data.Tests;

public class SqliteLedgerStoreTests : IDisposable
{
    private static readonly YearMonth March = new(2026, 3);
    private readonly TestDatabase _db = new();
    private readonly SqliteLedgerStore _store;
    private readonly LedgerCommandExecutor _executor;

    public SqliteLedgerStoreTests()
    {
        _store = _db.OpenStore();
        _executor = new LedgerCommandExecutor(_store, new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.FromHours(8))));
    }

    public void Dispose() => _db.Dispose();

    internal static MappingProfile Profile(long id) => new()
    {
        Id = id,
        Name = $"甲銀行{id}",
        Date = ColumnRef.ByName("交易日期"),
        DateFormats = ["yyyy/MM/dd"],
        Amount = ColumnRef.ByName("金額"),
        Description = ColumnRef.ByIndex(2),
        Serial = ColumnRef.ByName("序號"),
        Encoding = TextEncodingKind.Big5,
        ThousandsSeparator = null,
    };

    private (Account Account, BankTransaction Txn) Seed()
    {
        var id = _store.ReserveIds(3);
        var profile = Profile(id);
        var account = new Account(id + 1, "甲銀行帳戶", profile.Id, Money.FromCents(100000), new DateOnly(2026, 1, 1));
        var txn = new BankTransaction(
            id + 2, account.Id, new DateOnly(2026, 3, 5), March, Money.FromCents(-12345), "房租", "S1", null, "S|S1", null, 7, null,
            ClassificationSource.None, true);
        _executor.Execute(new ChangeSetBuilder("建立").Insert(profile).Insert(account).Insert(txn).Build());
        return (account, txn);
    }

    [Fact]
    public void Entities_RoundTripExactly()
    {
        var (account, txn) = Seed();

        Assert.Equal(account, _store.Find<Account>(account.Id));
        Assert.Equal(txn, _store.Find<BankTransaction>(txn.Id));
        var profile = _store.Find<MappingProfile>(account.ProfileId)!;
        Assert.Equal(TextEncodingKind.Big5, profile.Encoding);
        Assert.Equal(ColumnRef.ByIndex(2), profile.Description);
        Assert.Null(profile.ThousandsSeparator);
        Assert.Equal(["yyyy/MM/dd"], profile.DateFormats);

        var period = new Period(_store.ReserveIds(1), March, true, new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.FromHours(8)));
        _executor.Execute(new ChangeSetBuilder("p").Insert(period).Build());
        Assert.Equal(period, _store.Find<Period>(period.Id));
    }

    [Fact]
    public void UpdateAndDelete_RequireUnchangedBeforeImage()
    {
        var (_, txn) = Seed();
        var edited = txn with { Note = "已修改" };
        _executor.Execute(new ChangeSetBuilder("改").Update(txn, edited).Build());

        var stale = new ChangeSetBuilder("舊資料").Update(txn, txn with { Note = "x" }).Build();
        Assert.Throws<ConcurrencyConflictException>(() => _executor.Execute(stale));
        Assert.Throws<ConcurrencyConflictException>(() => _executor.Execute(new ChangeSetBuilder("刪").Delete(txn).Build()));

        _executor.Execute(new ChangeSetBuilder("刪").Delete(edited).Build());
        Assert.Null(_store.Find<BankTransaction>(txn.Id));
    }

    [Fact]
    public void FailedCommit_WritesNothing_IncludingAudit()
    {
        var (account, txn) = Seed();
        var auditBefore = _store.GetAuditEntries(0, 1000).Count;
        var newTxn = txn with { Id = _store.ReserveIds(1), DedupKey = "S|S2" };

        var cs = new ChangeSetBuilder("混合").Insert(newTxn).Update(txn with { Note = "stale" }, txn).Build();

        Assert.Throws<ConcurrencyConflictException>(() => _executor.Execute(cs));
        Assert.Null(_store.Find<BankTransaction>(newTxn.Id));
        Assert.Equal(auditBefore, _store.GetAuditEntries(0, 1000).Count);
        Assert.Equal(account, _store.Find<Account>(account.Id));
    }

    [Fact]
    public void DuplicateDedupKey_IsRejectedByUniqueIndex()
    {
        var (_, txn) = Seed();
        var dup = txn with { Id = _store.ReserveIds(1) };

        Assert.Throws<ConcurrencyConflictException>(() => _executor.Execute(new ChangeSetBuilder("重複").Insert(dup).Build()));
    }

    [Fact]
    public void Audit_IsChainedAndContainsBeforeAfter()
    {
        var (_, txn) = Seed();
        _executor.Execute(new ChangeSetBuilder("改備註").Update(txn, txn with { Note = "新\"備註\"" }).Build());

        var audit = _store.GetAuditEntries(0, 100);
        Assert.Equal(4, audit.Count);
        Assert.Equal([1L, 2, 3, 4], audit.Select(a => a.Sequence));
        Assert.Null(AuditHasher.FindFirstBrokenLink(audit));
        var last = audit[^1];
        Assert.Equal(("改備註", "Update", "BankTransaction", txn.Id), (last.Operation, last.Action, last.EntityType, last.EntityId));
        Assert.Contains("\"Note\":null", last.BeforeJson);
        Assert.Contains("新\\\"備註\\\"", last.AfterJson);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.FromHours(8)), last.At);
    }

    [Theory]
    [InlineData("UPDATE audit_log SET operation = 'x';")]
    [InlineData("DELETE FROM audit_log;")]
    [InlineData("UPDATE audit_log SET after_json = NULL WHERE seq = 1;")]
    public void AuditLog_RejectsModificationEvenThroughRawSql(string sql)
    {
        Seed();
        using var connection = _store.Database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("AUDIT_APPEND_ONLY", ex.Message);
        Assert.Equal(3, _store.GetAuditEntries(0, 100).Count);
    }

    [Fact]
    public void ClosedPeriodTrigger_BlocksWritesThatBypassTheApplication()
    {
        var (_, txn) = Seed();
        _executor.Execute(new ChangeSetBuilder("結帳").Insert(new Period(_store.ReserveIds(1), March, true, null)).Build());

        var store = _store;
        var cs = new ChangeSet("繞過", [EntityChange.Update(txn, txn with { Note = "x" })]);
        var ex = Assert.Throws<LedgerException>(() => store.Commit(cs, []));
        Assert.Contains("已結帳", ex.Message);
        Assert.Equal(txn, _store.Find<BankTransaction>(txn.Id));
    }

    [Fact]
    public void ReserveIds_ReturnsDisjointIncreasingBlocks()
    {
        var a = _store.ReserveIds(10);
        var b = _store.ReserveIds(1);
        Assert.Equal(a + 10, b);
    }

    [Fact]
    public void ClosedPeriods_AreReported()
    {
        _executor.Execute(new ChangeSetBuilder("p")
            .Insert(new Period(_store.ReserveIds(1), March, true, null))
            .Insert(new Period(_store.ReserveIds(1), new YearMonth(2026, 4), false, null))
            .Build());

        Assert.Equal([March], _store.GetClosedPeriods());
    }
}
