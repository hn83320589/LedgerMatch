using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Core.Tests.PropertyTesting;

namespace LedgerMatch.Core.Tests.Reporting;

public class ReconciliationCalculatorTests
{
    private static readonly YearMonth March = new(2026, 3);
    private static readonly Account Account = new(1, "甲銀行帳戶", 1, Money.FromCents(1_000_000), new DateOnly(2026, 1, 1));
    private static readonly DateTimeOffset Now = new(2026, 4, 5, 10, 0, 0, TimeSpan.FromHours(8));

    private static BankTransaction Txn(long id, int day, long cents, bool bookConfirmed = false, int month = 3) =>
        new(id, 1, new DateOnly(2026, month, day), new YearMonth(2026, month), Money.FromCents(cents), $"txn{id}", $"T{id}", null, $"k{id}",
            null, null, null, ClassificationSource.None, bookConfirmed);

    private static Receipt Rec(long id, int day, long cents, int month = 3) =>
        new(id, 1, new DateOnly(2026, month, day), $"客戶{id}", $"R{id}", Money.FromCents(cents), null, null);

    private static MatchWithLinks M(long id, long deposit, MatchConfidence confidence, long feeCents, params long[] receipts) =>
        new(new Match(id, deposit, March, MatchKind.Manual, confidence, Money.FromCents(feeCents), null), receipts);

    /// <summary>The hand-calculated example documented in samples/expected-results.md (section 甲銀行 2026-03 計算範例).</summary>
    internal static ReconciliationData HandExample(MatchConfidence t2Confidence = MatchConfidence.Suggested) => new(
        Account,
        March,
        [
            Txn(1, 2, 120_000),
            Txn(2, 10, 196_000),
            Txn(3, 15, 498_500),
            Txn(4, 20, 75),
            Txn(5, 21, -2_000_000, bookConfirmed: true),
            Txn(6, 25, -300_000, bookConfirmed: true),
            Txn(7, 31, 80_000, bookConfirmed: true),
            Txn(8, 2, 55_500, month: 4),
        ],
        [Rec(11, 1, 120_000), Rec(12, 8, 100_000), Rec(13, 9, 96_000), Rec(14, 14, 500_000), Rec(15, 30, 250_000), Rec(16, 1, 99_900, month: 4)],
        [M(21, 1, MatchConfidence.Confirmed, 0, 11), M(22, 2, t2Confidence, 0, 12, 13), M(23, 3, MatchConfidence.Confirmed, 1_500, 14)],
        [
            new BookEntry(31, 1, new DateOnly(2026, 3, 18), Money.FromCents(-300_000), "支票 #001", 6),
            new BookEntry(32, 1, new DateOnly(2026, 3, 28), Money.FromCents(-150_000), "支票 #002", null),
            new BookEntry(33, 1, new DateOnly(2026, 3, 29), Money.FromCents(10_000), "其他調整", null),
        ],
        null);

    [Fact]
    public void HandCalculatedExample_MatchesEveryFigure()
    {
        var report = ReconciliationCalculator.Compute(HandExample(), Now);

        Assert.Equal(-405_425, report.ComputedBankBalance.Cents);
        Assert.Equal(-295_500, report.BookBalance.Cents);
        Assert.Equal(["R12", "R13", "R15"], report.DepositsInTransit.Items.Select(i => i.Reference));
        Assert.Equal(446_000, report.DepositsInTransit.Total.Cents);
        Assert.Equal(10_000, report.OtherBookItems.Total.Cents);
        Assert.Equal(-150_000, report.OutstandingItems.Total.Cents);
        Assert.Equal(["T2", "T4"], report.BankOnlyDeposits.Items.Select(i => i.Reference));
        Assert.Equal(196_075, report.BankOnlyDeposits.Total.Cents);
        Assert.Equal("有建議配對，尚未確認", report.BankOnlyDeposits.Items[0].Note);
        Assert.Empty(report.BankOnlyWithdrawals.Items);
        Assert.Equal(-99_425, report.AdjustedBankBalance.Cents);
        Assert.Equal(-99_425, report.AdjustedBookBalance.Cents);
        Assert.True(report.IsBalanced);
        Assert.Empty(report.DifferenceNotes);
    }

    [Fact]
    public void ConfirmingASuggestion_LeavesBookBalanceUnchanged_AndClearsBothItems()
    {
        var suggested = ReconciliationCalculator.Compute(HandExample(MatchConfidence.Suggested), Now);
        var confirmed = ReconciliationCalculator.Compute(HandExample(MatchConfidence.Confirmed), Now);

        Assert.Equal(suggested.BookBalance, confirmed.BookBalance);
        Assert.Equal(suggested.ComputedBankBalance, confirmed.ComputedBankBalance);
        Assert.Equal(["R15"], confirmed.DepositsInTransit.Items.Select(i => i.Reference));
        Assert.Equal(["T4"], confirmed.BankOnlyDeposits.Items.Select(i => i.Reference));
        Assert.True(confirmed.IsBalanced);
    }

    [Fact]
    public void SuggestedFeeMatch_DoesNotBookTheFee()
    {
        var data = HandExample() with { Matches = [M(23, 3, MatchConfidence.Suggested, 1_500, 14)] };
        var report = ReconciliationCalculator.Compute(data, Now);

        // Without the confirmed fee match, R14 is in transit and T3 is bank-only; book loses neither 15.00 fee nor anything else.
        Assert.Equal(-295_500 + 1_500, report.BookBalance.Cents);
        Assert.Contains(report.DepositsInTransit.Items, i => i.Reference == "R14");
        Assert.Contains(report.BankOnlyDeposits.Items, i => i.Reference == "T3");
        Assert.True(report.IsBalanced);
    }

    [Fact]
    public void ReceiptIssuedAfterMonthEnd_MakesMatchIneffectiveForThatMonth()
    {
        var data = HandExample() with { Matches = [M(21, 1, MatchConfidence.Confirmed, 0, 16)] };
        var report = ReconciliationCalculator.Compute(data, Now);

        Assert.Contains(report.BankOnlyDeposits.Items, i => i.Reference == "T1");
        Assert.DoesNotContain(report.DepositsInTransit.Items, i => i.Reference == "R16");
        Assert.True(report.IsBalanced);
    }

    [Fact]
    public void StatementBalanceMismatch_IsReportedAsDifference()
    {
        var data = HandExample() with { Statement = new StatementBalance(1, 1, March, Money.FromCents(-405_000)) };
        var report = ReconciliationCalculator.Compute(data, Now);

        Assert.Equal(Money.FromCents(425), report.Difference);
        Assert.False(report.IsBalanced);
        Assert.Contains("4.25", Assert.Single(report.DifferenceNotes));
    }

    [Fact]
    public void LaterMonthsAreExcluded()
    {
        var report = ReconciliationCalculator.Compute(HandExample() with { Month = new YearMonth(2026, 2) }, Now);
        Assert.Equal(Account.OpeningBalance, report.ComputedBankBalance);
        Assert.Equal(Account.OpeningBalance, report.BookBalance);
        Assert.True(report.IsBalanced);
    }

    /// <summary>
    /// Property: whatever mix of deposits, withdrawals, receipts, matches (confirmed or not, with fees), and
    /// cleared/uncleared book entries, the two adjusted balances agree when no statement figure is entered.
    /// </summary>
    [Fact]
    public void Property_AdjustedBalancesAlwaysAgree()
    {
        Prop.ForAll(seed: 99, cases: 500, random =>
        {
            var data = RandomData(random);
            var report = ReconciliationCalculator.Compute(data, Now);
            Assert.Equal(report.AdjustedBankBalance, report.AdjustedBookBalance);
        });
    }

    private static ReconciliationData RandomData(Random random)
    {
        var txns = new List<BankTransaction>();
        var receipts = new List<Receipt>();
        var matches = new List<MatchWithLinks>();
        var entries = new List<BookEntry>();
        long id = 100;

        for (var i = 0; i < random.Next(0, 25); i++)
        {
            var month = random.Next(2, 5);
            var cents = random.Next(1, 100_000) * (random.Next(3) == 0 ? -1 : 1);
            var txn = Txn(id++, random.Next(1, 28), cents, bookConfirmed: cents < 0 || random.Next(6) == 0, month: month);
            txns.Add(txn);

            if (txn.IsDeposit && !txn.BookConfirmed && random.Next(2) == 0)
            {
                var fee = random.Next(3) == 0 ? random.Next(1, 500) : 0;
                var parts = random.Next(1, 4);
                var total = txn.Amount.Cents + fee;
                var links = new List<long>();
                for (var p = 0; p < parts; p++)
                {
                    var amount = p == parts - 1 ? total : random.NextInt64(1, Math.Max(2, total - (parts - p - 1)));
                    if (amount <= 0)
                    {
                        break;
                    }

                    total -= amount;
                    var r = Rec(id++, random.Next(1, 28), amount, month: random.Next(2, 5));
                    receipts.Add(r);
                    links.Add(r.Id);
                }

                if (total == 0)
                {
                    var confidence = random.Next(3) == 0 ? MatchConfidence.Suggested : MatchConfidence.Confirmed;
                    matches.Add(new MatchWithLinks(new Match(id++, txn.Id, txn.PostingPeriod, MatchKind.Manual, confidence, Money.FromCents(fee), null), links));
                }
            }
            else if (!txn.IsDeposit && random.Next(4) == 0)
            {
                entries.Add(new BookEntry(id++, 1, new DateOnly(2026, random.Next(2, 5), random.Next(1, 28)), txn.Amount, "cleared", txn.Id));
            }
        }

        for (var i = 0; i < random.Next(0, 5); i++)
        {
            receipts.Add(Rec(id++, random.Next(1, 28), random.Next(1, 50_000), month: random.Next(2, 5)));
            entries.Add(new BookEntry(id++, 1, new DateOnly(2026, random.Next(2, 5), random.Next(1, 28)), Money.FromCents(random.Next(1, 9_000) * (random.Next(2) == 0 ? -1 : 1)), "open", null));
        }

        return new ReconciliationData(Account, March, txns, receipts, matches, entries, null);
    }
}
