using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Tests.Persistence;

namespace LedgerMatch.Core.Tests.History;

public class UndoRedoServiceTests
{
    private static readonly YearMonth March = new(2026, 3);
    private readonly InMemoryLedgerStore _store = new();
    private readonly UndoRedoService _history;

    public UndoRedoServiceTests()
    {
        _history = new UndoRedoService(new LedgerCommandExecutor(_store, TimeProvider.System));
    }

    private static BankTransaction Txn(long id) => LedgerCommandExecutorTests.Txn(id, March);

    [Fact]
    public void UndoAndRedo_RestoreExactState()
    {
        var t = Txn(1);
        _history.Execute(new ChangeSetBuilder("新增").Insert(t).Build());
        _history.Execute(new ChangeSetBuilder("改").Update(t, t with { Description = "b" }).Build());

        _history.Undo();
        Assert.Equal("desc", _store.Get<BankTransaction>(1).Description);
        _history.Undo();
        Assert.False(_store.Exists<BankTransaction>(1));

        _history.Redo();
        _history.Redo();
        Assert.Equal("b", _store.Get<BankTransaction>(1).Description);
        Assert.False(_history.CanRedo);
    }

    [Fact]
    public void SupportsAtLeastThirtyLevels()
    {
        var current = Txn(1);
        _history.Execute(new ChangeSetBuilder("新增").Insert(current).Build());
        for (var i = 1; i <= 40; i++)
        {
            var next = current with { Description = $"v{i}" };
            _history.Execute(new ChangeSetBuilder($"改{i}").Update(current, next).Build());
            current = next;
        }

        for (var i = 0; i < 40; i++)
        {
            _history.Undo();
        }

        Assert.Equal("desc", _store.Get<BankTransaction>(1).Description);
    }

    [Fact]
    public void Capacity_DropsOldestStep()
    {
        var history = new UndoRedoService(new LedgerCommandExecutor(_store, TimeProvider.System), capacity: 30);
        for (var i = 1; i <= 35; i++)
        {
            history.Execute(new ChangeSetBuilder($"新增{i}").Insert(Txn(i)).Build());
        }

        Assert.Equal(30, history.UndoCount);
        Assert.Equal("新增35", history.NextUndoOperation);
    }

    [Fact]
    public void Capacity_BelowThirty_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UndoRedoService(new LedgerCommandExecutor(_store, TimeProvider.System), 29));
    }

    [Fact]
    public void BatchOperation_IsOneStep()
    {
        _history.Execute(new ChangeSetBuilder("批次").Insert(Txn(1)).Insert(Txn(2)).Insert(Txn(3)).Build());
        _history.Undo();

        Assert.Empty(_store.Rows);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void NewOperation_ClearsRedo()
    {
        _history.Execute(new ChangeSetBuilder("a").Insert(Txn(1)).Build());
        _history.Undo();
        _history.Execute(new ChangeSetBuilder("b").Insert(Txn(2)).Build());

        Assert.False(_history.CanRedo);
    }

    [Fact]
    public void Undo_IntoClosedPeriod_IsRefusedAndStepIsKept()
    {
        _history.Execute(new ChangeSetBuilder("a").Insert(Txn(1)).Build());
        _store.Closed.Add(March);

        Assert.Throws<PeriodClosedException>(() => _history.Undo());
        Assert.True(_history.CanUndo);
        Assert.True(_store.Exists<BankTransaction>(1));
    }

    [Fact]
    public void Undo_AfterRowChangedOutsideHistory_IsConflict()
    {
        var t = Txn(1);
        _history.Execute(new ChangeSetBuilder("a").Insert(t).Build());
        new LedgerCommandExecutor(_store, TimeProvider.System).Execute(new ChangeSetBuilder("外部").Update(t, t with { Note = "x" }).Build());

        Assert.Throws<ConcurrencyConflictException>(() => _history.Undo());
    }

    [Fact]
    public void UndoAndRedo_AreAudited()
    {
        _history.Execute(new ChangeSetBuilder("新增").Insert(Txn(1)).Build());
        _history.Undo();
        _history.Redo();

        Assert.Equal(["新增", "復原：新增", "重做：新增"], _store.GetAuditEntries(0, 10).Select(a => a.Operation));
    }
}
