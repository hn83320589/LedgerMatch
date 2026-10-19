using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Matching;

namespace LedgerMatch.Core.Tests.Matching;

public class MatchingEngineTests
{
    private static readonly DateOnly Day0 = new(2026, 3, 10);
    private static long _nextId = 1;

    private static DepositCandidate Dep(long cents, int day = 0, string desc = "存入", long? id = null) =>
        new(id ?? Interlocked.Increment(ref _nextId), Day0.AddDays(day), Money.FromCents(cents), desc, $"k{day}|{cents}|{desc}");

    private static ReceiptCandidate Rec(string no, long cents, int day = 0, long? id = null) =>
        new(id ?? Interlocked.Increment(ref _nextId), Day0.AddDays(day), Money.FromCents(cents), no);

    private static MatchingResult Run(IEnumerable<DepositCandidate> d, IEnumerable<ReceiptCandidate> r, MatchingSettings? s = null) =>
        MatchingEngine.Run(d, r, s ?? MatchingSettings.Default);

    private static MatchProposal Only(MatchingResult result) => Assert.Single(result.Proposals);

    // ---------- Stage 1: exact ----------

    [Theory]
    [InlineData(3, true)]
    [InlineData(-3, true)]
    [InlineData(4, false)]
    [InlineData(-4, false)]
    public void Exact_DateDifferenceBoundaryIsInclusive(int receiptDay, bool matches)
    {
        var deposit = Dep(100000);
        var receipt = Rec("R-001", 100000, receiptDay);

        var result = Run([deposit], [receipt]);

        if (matches)
        {
            var p = Only(result);
            Assert.Equal((MatchKind.Exact, MatchConfidence.Confirmed), (p.Kind, p.Confidence));
            Assert.Equal([receipt.Id], p.ReceiptIds);
        }
        else
        {
            Assert.Empty(result.Proposals);
        }
    }

    [Fact]
    public void Exact_ZeroToleranceRequiresSameDay()
    {
        var settings = MatchingSettings.Default with { ExactDateToleranceDays = 0 };
        Assert.Single(Run([Dep(500)], [Rec("R1", 500)], settings).Proposals);
        Assert.Empty(Run([Dep(500)], [Rec("R1", 500, 1)], settings).Proposals);
    }

    [Theory]
    [InlineData(100001)]
    [InlineData(99999)]
    public void Exact_RequiresZeroAmountDifference(long receiptCents)
    {
        var result = Run([Dep(100000)], [Rec("R-001", receiptCents)], MatchingSettings.Default with { FeeFixedCap = null, FeeRateCap = null });
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void Exact_ReceiptNumberInDescription_MatchesOutsideDateTolerance()
    {
        var deposit = Dep(250000, desc: "匯款 INV-2026-0042 王小姐");
        var receipt = Rec("inv-2026-0042", 250000, -20);

        var p = Only(Run([deposit], [receipt]));

        Assert.Equal((MatchKind.Exact, MatchConfidence.Confirmed), (p.Kind, p.Confidence));
    }

    [Fact]
    public void Exact_ReceiptNumberStillRequiresEqualAmount()
    {
        var result = Run([Dep(250000, desc: "INV-0042")], [Rec("INV-0042", 260000, 0)], MatchingSettings.Default with { FeeFixedCap = null, FeeRateCap = null });
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void Exact_PrefersNumberHitThenNearestDateThenEarliestReceipt()
    {
        var deposit = Dep(1000, desc: "轉帳 R-9");
        var near = Rec("R-1", 1000, 0);
        var numbered = Rec("R-9", 1000, 2);
        Assert.Equal([numbered.Id], Only(Run([deposit], [near, numbered])).ReceiptIds);

        var plain = Dep(1000, desc: "現金");
        var a = Rec("B", 1000, -1);
        var b = Rec("A", 1000, 1);
        var c = Rec("C", 1000, 3);
        Assert.Equal([a.Id], Only(Run([plain], [c, b, a])).ReceiptIds);
    }

    [Fact]
    public void Exact_ReceiptIsUsedOnlyOnce()
    {
        var result = Run([Dep(500, 0), Dep(500, 1)], [Rec("R1", 500, 0)]);
        Assert.Single(result.Proposals);
    }

    [Fact]
    public void ShortReceiptNumbers_DoNotMatchByText()
    {
        Assert.False(MatchingEngine.NumberAppearsIn("12", "存入 12345"));
        Assert.True(MatchingEngine.NumberAppearsIn("ＡＢ１２", "x ab12 y"));
    }

    [Fact]
    public void WithdrawalsAreIgnored()
    {
        Assert.Empty(Run([Dep(-500)], [Rec("R1", 500)]).Proposals);
    }

    // ---------- Stage 2: one-to-many ----------

    [Fact]
    public void OneToMany_FindsCombinationAsSuggestion()
    {
        var deposit = Dep(196000, 0, "刷卡撥款");
        var r1 = Rec("C-1", 50000, -2);
        var r2 = Rec("C-2", 120000, -1);
        var r3 = Rec("C-3", 26000, -1);
        var noise = Rec("C-4", 33300, -1);

        var p = Only(Run([deposit], [r1, r2, r3, noise]));

        Assert.Equal((MatchKind.OneToMany, MatchConfidence.Suggested), (p.Kind, p.Confidence));
        Assert.Equal(new[] { r1.Id, r2.Id, r3.Id }.Order(), p.ReceiptIds.Order());
        Assert.Equal(Money.Zero, p.Fee);
    }

    [Fact]
    public void OneToMany_PrefersFewerReceipts()
    {
        var deposit = Dep(1000);
        var pairA = Rec("P1", 600, -1);
        var pairB = Rec("P2", 400, -1);
        var tri = new[] { Rec("T1", 300), Rec("T2", 300), Rec("T3", 400) };

        var p = Only(Run([deposit], [.. tri, pairA, pairB]));

        // {T3(400), P1(600)} is also size 2 with smaller date diff (0 + 1) than {P1, P2} (1 + 1).
        Assert.Equal(2, p.ReceiptIds.Count);
        Assert.Equal(new[] { tri[2].Id, pairA.Id }.Order(), p.ReceiptIds.Order());
        Assert.True(p.FeasibleCombinations >= 2);
    }

    [Fact]
    public void OneToMany_TieOnSizeAndDates_PicksLexicographicallySmallestReceipts()
    {
        var deposit = Dep(1000);
        var receipts = new[] { Rec("D", 500), Rec("A", 500), Rec("C", 500), Rec("B", 500) };

        var p = Only(Run([deposit], receipts));

        Assert.Equal([receipts[1].Id, receipts[3].Id], p.ReceiptIds); // A, B
        Assert.Equal(6, p.FeasibleCombinations);
    }

    [Fact]
    public void OneToMany_IgnoresReceiptsAfterDepositOrBeyondLookback()
    {
        var deposit = Dep(1000);
        var settings = MatchingSettings.Default with { LookbackDays = 5, FeeFixedCap = null, FeeRateCap = null };

        Assert.Empty(Run([deposit], [Rec("A", 500, 0), Rec("B", 500, 1)], settings).Proposals);
        Assert.Empty(Run([deposit], [Rec("A", 500, 0), Rec("B", 500, -6)], settings).Proposals);
        Assert.Single(Run([deposit], [Rec("A", 500, 0), Rec("B", 500, -5)], settings).Proposals);
    }

    [Fact]
    public void OneToMany_RespectsMaxComboSize()
    {
        var receipts = Enumerable.Range(1, 7).Select(i => Rec($"R{i}", 100)).ToArray();
        var settings = MatchingSettings.Default with { FeeFixedCap = null, FeeRateCap = null };

        Assert.Empty(Run([Dep(700)], receipts, settings with { MaxComboSize = 6 }).Proposals);
        Assert.Equal(7, Only(Run([Dep(700)], receipts, settings with { MaxComboSize = 7 })).ReceiptIds.Count);
    }

    [Fact]
    public void StagesOnlySeeLeftovers()
    {
        var exactDeposit = Dep(500, 0);
        var comboDeposit = Dep(1000, 1);
        var r500 = Rec("A", 500, 0);
        var r500b = Rec("B", 500, 0);
        var r500c = Rec("C", 500, 1);

        var result = Run([comboDeposit, exactDeposit], [r500, r500b, r500c]);

        var exact = result.Proposals.Single(p => p.DepositId == exactDeposit.Id);
        var combo = result.Proposals.Single(p => p.DepositId == comboDeposit.Id);
        Assert.Equal(MatchKind.Exact, exact.Kind);
        Assert.Empty(exact.ReceiptIds.Intersect(combo.ReceiptIds));
    }

    // ---------- Limits ----------

    [Fact]
    public void TooManyCandidates_SearchesOnlyNearestAndWarns()
    {
        var settings = MatchingSettings.Default with { MaxCandidates = 5, FeeFixedCap = null, FeeRateCap = null };
        var deposit = Dep(1500);
        var near = Enumerable.Range(0, 5).Select(i => Rec($"N{i}", 100, 0)).ToList();
        var far = new[] { Rec("F1", 700, -3), Rec("F2", 800, -3) };

        var result = Run([deposit], [.. near, .. far], settings);

        Assert.Empty(result.Proposals); // the only exact combo {F1, F2} lies outside the 5 nearest candidates
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(MatchingWarningKind.CandidatesTruncated, warning.Kind);
        Assert.Equal(deposit.Id, warning.DepositId);
    }

    [Fact]
    public void TruncatedSearch_StillMatchesWithinNearestAndFlagsProposal()
    {
        var settings = MatchingSettings.Default with { MaxCandidates = 3, FeeFixedCap = null, FeeRateCap = null };
        var receipts = new[] { Rec("A", 300, 0), Rec("B", 700, 0), Rec("C", 999, -1), Rec("D", 1, -2) };

        var p = Only(Run([Dep(1000)], receipts, settings));

        Assert.True(p.CandidatesTruncated);
        Assert.Equal([receipts[0].Id, receipts[1].Id], p.ReceiptIds);
    }

    [Fact]
    public void SearchBudgetExceeded_ProducesNoMatchAndWarns()
    {
        var settings = MatchingSettings.Default with { NodeBudget = 1_000, MaxCandidates = 30, MaxComboSize = 8, FeeFixedCap = null, FeeRateCap = null };
        // Equal-ish amounts and a mid-range size-8 target: bounds cannot prune, so the full search needs millions of steps.
        var receipts = Enumerable.Range(0, 30).Select(i => Rec($"R{i:D2}", 1000 + (i * 2))).ToArray();
        var deposit = Dep(new[] { 0, 1, 2, 3, 26, 27, 28, 29 }.Sum(i => receipts[i].Amount.Cents));

        var result = Run([deposit], receipts, settings);

        Assert.Empty(result.Proposals);
        Assert.Contains(result.Warnings, w => w.Kind == MatchingWarningKind.SearchBudgetExceeded);
    }

    [Fact]
    public void WorstCaseAtMaximumLimits_FinishesWithoutError()
    {
        var settings = MatchingSettings.Default with { MaxCandidates = 30, MaxComboSize = 8 };
        var receipts = Enumerable.Range(0, 30).Select(i => Rec($"R{i:D2}", 1000 + (i * 13), -(i % 7))).ToArray();
        // Odd totals are unreachable with even amounts, so every deposit forces a search to the budget limit.
        var deposits = Enumerable.Range(0, 10).Select(i => Dep(8 * 1100 + 1 + (i * 2), i)).ToArray();
        receipts = receipts.Select(r => r with { Amount = Money.FromCents(r.Amount.Cents * 2) }).ToArray();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Run(deposits, receipts, settings with { FeeFixedCap = null, FeeRateCap = null });

        Assert.Empty(result.Proposals);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    // ---------- Stage 3: fee ----------

    [Fact]
    public void Fee_WithinFixedCap_MatchesWithSeparateFeeAndConservesAmounts()
    {
        var settings = MatchingSettings.Default with { FeeFixedCap = Money.FromCents(3000), FeeRateCap = null };
        var deposit = Dep(98500);
        var receipt = Rec("R1", 100000, -1);

        var p = Only(Run([deposit], [receipt], settings));

        Assert.Equal((MatchKind.WithFee, MatchConfidence.Suggested), (p.Kind, p.Confidence));
        Assert.Equal(Money.FromCents(1500), p.Fee);
        Assert.Equal(receipt.Amount, deposit.Amount + p.Fee);
    }

    [Theory]
    [InlineData(3000, true)]
    [InlineData(3001, false)]
    public void Fee_FixedCapBoundary(long feeCents, bool matches)
    {
        var settings = MatchingSettings.Default with { FeeFixedCap = Money.FromCents(3000), FeeRateCap = null };
        var result = Run([Dep(100000 - feeCents)], [Rec("R1", 100000)], settings);
        Assert.Equal(matches, result.Proposals.Count == 1);
    }

    [Theory]
    [InlineData(30000, true)]
    [InlineData(30001, false)]
    public void Fee_RateCapBoundary(long feeCents, bool matches)
    {
        var settings = MatchingSettings.Default with { FeeFixedCap = null, FeeRateCap = 0.03m };
        var result = Run([Dep(1000000 - feeCents)], [Rec("R1", 600000, -1), Rec("R2", 400000, -2)], settings);
        Assert.Equal(matches, result.Proposals.Count == 1);
        if (matches)
        {
            Assert.Equal(Money.FromCents(feeCents), result.Proposals[0].Fee);
        }
    }

    [Fact]
    public void Fee_CombinedCriteria_UseTheLargerAllowance()
    {
        var settings = MatchingSettings.Default with { FeeFixedCap = Money.FromCents(3000), FeeRateCap = 0.01m };
        Assert.Equal(Money.FromCents(3000), settings.AllowedFee(Money.FromCents(100000)));
        Assert.Equal(Money.FromCents(10000), settings.AllowedFee(Money.FromCents(1000000)));
    }

    [Fact]
    public void Fee_DepositAboveReceipts_IsNeverAFee()
    {
        Assert.Empty(Run([Dep(100100)], [Rec("R1", 100000)]).Proposals);
    }

    [Fact]
    public void Fee_Disabled_SkipsStageThree()
    {
        Assert.Empty(Run([Dep(99000)], [Rec("R1", 100000)], MatchingSettings.Default with { FeeFixedCap = null, FeeRateCap = null }).Proposals);
    }

    [Fact]
    public void Fee_PrefersSmallestFee()
    {
        var settings = MatchingSettings.Default with { FeeFixedCap = Money.FromCents(5000), FeeRateCap = null };
        var bigFee = Rec("A", 103000, 0);
        var smallFee = Rec("B", 60000, -1);
        var smallFee2 = Rec("C", 40500, -1);

        var p = Only(Run([Dep(100000)], [bigFee, smallFee, smallFee2], settings));

        Assert.Equal(Money.FromCents(500), p.Fee);
        Assert.Equal(2, p.ReceiptIds.Count);
    }

    [Fact]
    public void InvalidSettings_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => Run([], [], MatchingSettings.Default with { MaxComboSize = 9 }));
        Assert.Throws<ArgumentException>(() => Run([], [], MatchingSettings.Default with { FeeRateCap = 0.2m }));
    }

    // ---------- Progress & cancellation ----------

    [Fact]
    public void ReportsProgressForAllStages()
    {
        var stages = new HashSet<MatchingStage>();
        MatchingEngine.Run([Dep(1)], [], MatchingSettings.Default, new SyncProgress<MatchingProgress>(p => stages.Add(p.Stage)));
        Assert.Equal([MatchingStage.Exact, MatchingStage.OneToMany, MatchingStage.WithFee], stages.Order());
    }

    [Fact]
    public void Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MatchingEngine.Run([Dep(1)], [], MatchingSettings.Default, null, cts.Token));
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
