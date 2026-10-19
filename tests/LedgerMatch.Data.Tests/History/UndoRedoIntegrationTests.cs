using LedgerMatch.Core;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Core.Tests.Importing;
using LedgerMatch.Data.Tests.Samples;

namespace LedgerMatch.Data.Tests.History;

/// <summary>Undo/redo across every kind of operation, checking that statistics and balances follow each step.</summary>
public class UndoRedoIntegrationTests : IDisposable
{
    private static readonly YearMonth March = new(2026, 3);
    private readonly TestLedger _ledger = new();
    private readonly Account _account;
    private readonly ReconciliationService _reports;

    public UndoRedoIntegrationTests()
    {
        _account = _ledger.CreateAccount(TestProfiles.BankA, "甲銀行帳戶", 5_000_000);
        foreach (var file in new[] { "bank-a-2026-03-part1.csv", "bank-a-2026-03-part2.csv" })
        {
            using var stream = File.OpenRead(SampleDataTests.SamplePath(file));
            _ledger.Statements.Import(_account.Id, stream, file);
        }

        var profile = _ledger.Setup.SaveProfile(SampleDataTests.ReceiptProfile);
        using (var receipts = File.OpenRead(SampleDataTests.SamplePath("receipts-2026-03.csv")))
        {
            _ledger.Receipts.Import(_account.Id, profile.Id, receipts, "receipts.csv");
        }

        _reports = new ReconciliationService(_ledger.Store, _ledger.Time);
        _ledger.History.Clear();
    }

    public void Dispose() => _ledger.Dispose();

    /// <summary>Everything the user would see change: list statistics and the reconciliation figures.</summary>
    private string State()
    {
        var march = new TransactionFilter { AccountId = _account.Id, Period = March };
        var r = _reports.Generate(_account.Id, March);
        var split = _ledger.Store.GetTransactions(_account.Id).Sum(t => _ledger.Store.GetSplitLines(t.Id).Count);
        return string.Join(
            "|",
            _ledger.Store.CountTransactions(march with { Status = TransactionStatusFilter.UnmatchedDeposits }),
            _ledger.Store.CountTransactions(march with { Status = TransactionStatusFilter.SuggestedMatches }),
            _ledger.Store.CountTransactions(march with { Status = TransactionStatusFilter.ConfirmedMatches }),
            _ledger.Store.CountTransactions(march with { Status = TransactionStatusFilter.Unclassified }),
            split,
            r.BookBalance.Cents,
            r.DepositsInTransit.Total.Cents,
            r.BankOnlyDeposits.Total.Cents,
            r.AdjustedBookBalance.Cents);
    }

    private BankTransaction Txn(string serial) => _ledger.Store.GetTransactions(_account.Id).Single(t => t.Serial == serial);

    [Fact]
    public void EveryOperationKind_UndoesAndRedoesWithStatisticsAndBalances()
    {
        var states = new List<string> { State() };
        var stationery = _ledger.Categories.Add("文具"); states.Add(State());
        var utilities = _ledger.Categories.Add("水電"); states.Add(State());

        _ledger.Matching.Run(_account.Id, March); states.Add(State());                                    // 自動配對（批次）
        var suggested = _ledger.Store.GetMatches(_account.Id, March).Where(m => m.Confidence == MatchConfidence.Suggested).ToList();
        _ledger.Matching.Confirm(suggested.Select(m => m.Id).ToList()); states.Add(State());           // 確認（批次）
        _ledger.Matching.Unmatch([suggested[0].Id]); states.Add(State());                               // 解除配對
        _ledger.Classification.SplitByWeights(Txn("A0305001").Id, [new(stationery.Id, 1), new(utilities.Id, 2)]); states.Add(State()); // 拆分
        _ledger.Rules.Save(new Rule(0, "水電", 0, "水電", null, null, null, null, utilities.Id, true)); states.Add(State());
        _ledger.Rules.Apply(new RuleScope(_account.Id, March)); states.Add(State());                    // 套用規則
        _ledger.Classification.SetCategory([Txn("A0328001").Id], stationery.Id); states.Add(State());   // 手動編輯

        Assert.NotEqual(states[0], states[^1]);

        for (var i = states.Count - 1; i > 0; i--)
        {
            _ledger.History.Undo();
            Assert.Equal(states[i - 1], State());
        }

        for (var i = 1; i < states.Count; i++)
        {
            _ledger.History.Redo();
            Assert.Equal(states[i], State());
        }
    }

    [Fact]
    public void ThirtyFiveSteps_CanAllBeUndone()
    {
        var a = _ledger.Categories.Add("雜支");
        var b = _ledger.Categories.Add("其他");
        var initial = State();
        var txn = Txn("A0328001").Id;
        for (var step = 0; step < 35; step++)
        {
            _ledger.Classification.SetCategory([txn], step % 2 == 0 ? a.Id : b.Id);
        }

        Assert.Equal(37, _ledger.History.UndoCount);
        for (var step = 0; step < 35; step++)
        {
            _ledger.History.Undo();
        }

        Assert.Equal(initial, State());
        Assert.Null(_ledger.Store.GetTransaction(txn)!.CategoryId);
    }
}
