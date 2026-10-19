using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace LedgerMatch.Core.Tests.Persistence;

public class LedgerCommandExecutorTests
{
    private readonly InMemoryLedgerStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly LedgerCommandExecutor _executor;

    public LedgerCommandExecutorTests()
    {
        _executor = new LedgerCommandExecutor(_store, _time);
    }

    internal static BankTransaction Txn(long id, YearMonth period, long cents = 100) => new(
        id, 1, period.FirstDay, period, Money.FromCents(cents), "desc", null, null, $"k{id}", null, null, null, ClassificationSource.None, false);

    [Fact]
    public void Execute_WritesAuditWithBeforeAndAfterAndInjectedTime()
    {
        var march = new YearMonth(2026, 3);
        var t = Txn(1, march);
        _executor.Execute(new ChangeSetBuilder("新增").Insert(t).Build());
        _executor.Execute(new ChangeSetBuilder("改摘要").Update(t, t with { Description = "新摘要" }).Build());

        var audit = _store.GetAuditEntries(0, 10);
        Assert.Equal(2, audit.Count);
        Assert.Equal(("新增", "Insert", "BankTransaction", 1L), (audit[0].Operation, audit[0].Action, audit[0].EntityType, audit[0].EntityId));
        Assert.Null(audit[0].BeforeJson);
        Assert.Contains("\"desc\"", audit[1].BeforeJson);
        Assert.Contains("新摘要", audit[1].AfterJson);
        Assert.Contains("\"1.00\"", audit[1].AfterJson);
        Assert.Equal(_time.GetUtcNow(), audit[1].At);
        Assert.Null(AuditHasher.FindFirstBrokenLink(audit));
    }

    [Fact]
    public void Execute_RejectsChangesInClosedPeriod_AndWritesNothing()
    {
        var march = new YearMonth(2026, 3);
        var april = new YearMonth(2026, 4);
        _store.Closed.Add(march);

        var cs = new ChangeSetBuilder("批次").Insert(Txn(1, april)).Insert(Txn(2, march)).Build();

        var ex = Assert.Throws<PeriodClosedException>(() => _executor.Execute(cs));
        Assert.Equal(march, ex.Month);
        Assert.Empty(_store.Rows);
        Assert.Empty(_store.GetAuditEntries(0, 10));
    }

    [Fact]
    public void Execute_RejectsMovingEntityOutOfClosedPeriod()
    {
        var march = new YearMonth(2026, 3);
        var t = Txn(1, march);
        _executor.Execute(new ChangeSetBuilder("新增").Insert(t).Build());
        _store.Closed.Add(march);

        Assert.Throws<PeriodClosedException>(() =>
            _executor.Execute(new ChangeSetBuilder("改期").Update(t, t with { PostingPeriod = new YearMonth(2026, 4) }).Build()));
    }

    [Fact]
    public void EmptyChangeSet_IsNoOp()
    {
        _executor.Execute(new ChangeSetBuilder("無").Build());
        Assert.Empty(_store.GetAuditEntries(0, 10));
    }

    [Fact]
    public void AuditChain_DetectsTampering()
    {
        _executor.Execute(new ChangeSetBuilder("a").Insert(Txn(1, new YearMonth(2026, 3))).Insert(Txn(2, new YearMonth(2026, 3))).Build());
        var entries = _store.GetAuditEntries(0, 10).ToList();
        entries[0] = entries[0] with { AfterJson = "{}" };

        Assert.Equal(1, AuditHasher.FindFirstBrokenLink(entries));
    }

    [Fact]
    public void ChangeSetBuilder_SkipsNoOpUpdates()
    {
        var t = Txn(1, new YearMonth(2026, 3));
        Assert.True(new ChangeSetBuilder("x").Update(t, t).Build().IsEmpty);
    }

    [Fact]
    public void EntityChange_RejectsMismatchedImages()
    {
        var march = new YearMonth(2026, 3);
        Assert.Throws<ArgumentException>(() => EntityChange.Update(Txn(1, march), Txn(2, march)));
    }
}
