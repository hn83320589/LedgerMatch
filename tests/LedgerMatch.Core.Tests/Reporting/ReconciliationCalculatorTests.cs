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

    /// <summary>The hand-calculated example documented in samples/expected-results.md, section 4 (計算範例).</summary>
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
    public void ConfirmedMatch_IsBookedInTheDepositMonth_EvenWhenAReceiptIsIssuedLater()
    {
        // T1 (3/2, 1,200.00) paid R11 (3/1, 999.00) and R16 (4/1, 201.00): the cash arrived in March, so R11 must not
        // be shown as still in transit, and the whole deposit is in the March book.
        var data = HandExample() with
        {
            Receipts = [Rec(11, 1, 99_900), Rec(16, 1, 20_100, month: 4)],
            Matches = [M(21, 1, MatchConfidence.Confirmed, 0, 11, 16)],
        };
        var report = ReconciliationCalculator.Compute(data, Now);

        Assert.DoesNotContain(report.BankOnlyDeposits.Items, i => i.Reference == "T1");
        Assert.DoesNotContain(report.DepositsInTransit.Items, i => i.Reference is "R11" or "R16");
        Assert.Equal(Account.OpeningBalance + Money.FromCents(120_000 - 2_000_000 - 300_000 + 80_000 - 150_000 + 10_000), report.BookBalance);
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

    /// <summary>
    /// Property with an independent oracle. The two adjusted balances agree by construction, so instead random
    /// economic events are generated first (customer payments, cheques, manual book deposits, bank charges, ...),
    /// each knowing how much cash it really moved by month end. The calculator's adjusted balance must equal
    /// opening + that cash; an item put in the wrong section or counted on both sides breaks the equality.
    /// </summary>
    [Fact]
    public void Property_AdjustedBalanceEqualsCashMovedByMonthEnd()
    {
        Prop.ForAll(seed: 2026, cases: 1000, random =>
        {
            var world = new CashWorld(random);
            var report = ReconciliationCalculator.Compute(world.Data, Now);

            var expected = Account.OpeningBalance + Money.FromCents(world.CashCents);
            var events = string.Join("; ", world.Log);
            Assert.True(Account.OpeningBalance + Money.FromCents(world.BankCents) == report.ComputedBankBalance, $"bank {report.ComputedBankBalance}: {events}");
            Assert.True(expected == report.AdjustedBankBalance, $"expected {expected}, adjusted bank {report.AdjustedBankBalance}: {events}");
            Assert.True(expected == report.AdjustedBookBalance, $"expected {expected}, adjusted book {report.AdjustedBookBalance}: {events}");
        });
    }

    /// <summary>Random events in Feb–Apr 2026, reconciled for March; only events up to March 31 have moved cash.</summary>
    private sealed class CashWorld
    {
        private readonly Random _random;
        private readonly List<BankTransaction> _txns = [];
        private readonly List<Receipt> _receipts = [];
        private readonly List<MatchWithLinks> _matches = [];
        private readonly List<BookEntry> _entries = [];
        private long _id = 100;

        public CashWorld(Random random)
        {
            _random = random;
            for (var i = random.Next(0, 20); i > 0; i--)
            {
                switch (random.Next(7))
                {
                    case 0: case 1: CustomerPayment(); break;
                    case 2: Cheque(); break;
                    case 3: ManualBookDeposit(); break;
                    case 4: BankLine(random.Next(-90_000, 0), bookConfirmed: true); break;    // bank charge, booked on import
                    case 5: BankLine(random.Next(1, 90_000), bookConfirmed: random.Next(2) == 0); break; // stray or directly booked deposit
                    default: StrayReceipt(); break;
                }
            }
        }

        /// <summary>One line per event with the cash it contributed, printed when the property fails.</summary>
        public List<string> Log { get; } = [];

        /// <summary>Bank movements up to March 31.</summary>
        public long BankCents { get; private set; }

        /// <summary>Cash that really moved up to March 31 according to either record.</summary>
        public long CashCents { get; private set; }

        public ReconciliationData Data => new(Account, March, _txns, _receipts, _matches, _entries, null);

        private static bool ByMarch(int month) => month <= 3;

        private int Month() => _random.Next(2, 5);

        /// <summary>
        /// Receipts paid by one deposit (possibly net of a fee). When the user confirmed the match and the deposit
        /// has arrived, the cash is the deposit; otherwise the book and the bank records stand on their own.
        /// </summary>
        private void CustomerPayment()
        {
            var parts = Enumerable.Range(0, _random.Next(1, 4)).Select(_ => (Cents: (long)_random.Next(1, 50_000), Month: Month())).ToList();
            var total = parts.Sum(p => p.Cents);
            var fee = _random.Next(3) == 0 ? _random.NextInt64(1, Math.Min(500, total)) : 0;
            var links = parts.Select(p => Add(Rec(_id++, _random.Next(1, 28), p.Cents, p.Month))).Select(r => r.Id).ToList();
            var receiptsByMarch = parts.Where(p => ByMarch(p.Month)).Sum(p => p.Cents);

            if (_random.Next(5) == 0)
            {
                CashCents += receiptsByMarch; // not paid yet
                Log.Add($"unpaid receipts {Describe(parts)} → {receiptsByMarch}");
                return;
            }

            var depositMonth = Month();
            var deposit = Add(Txn(_id++, _random.Next(1, 28), total - fee, month: depositMonth));
            var confidence = _random.Next(4) switch
            {
                0 => (MatchConfidence?)null,
                1 => MatchConfidence.Suggested,
                _ => MatchConfidence.Confirmed,
            };
            if (confidence is { } c)
            {
                _matches.Add(new MatchWithLinks(new Match(_id++, deposit.Id, deposit.PostingPeriod, MatchKind.Manual, c, Money.FromCents(fee), null), links));
            }

            var depositByMarch = ByMarch(depositMonth) ? deposit.Amount.Cents : 0;
            var cash = confidence == MatchConfidence.Confirmed && ByMarch(depositMonth)
                ? deposit.Amount.Cents
                : confidence == MatchConfidence.Confirmed ? receiptsByMarch : receiptsByMarch + depositByMarch;
            CashCents += cash;
            Log.Add($"payment {Describe(parts)} fee {fee} deposit {deposit.Amount.Cents}@{depositMonth} {confidence?.ToString() ?? "unmatched"} → {cash}");
        }

        private static string Describe(IEnumerable<(long Cents, int Month)> parts) => "[" + string.Join(",", parts.Select(p => $"{p.Cents}@{p.Month}")) + "]";

        /// <summary>A cheque written in the book, possibly cashed by the bank (in any order of the two dates).</summary>
        private void Cheque()
        {
            var cents = _random.Next(-90_000, 0);
            ClearedBookEntry(cents, bankLineBookConfirmed: true);
        }

        /// <summary>A deposit the user recorded by hand, possibly later seen on the statement.</summary>
        private void ManualBookDeposit()
        {
            var cents = _random.Next(1, 90_000);
            ClearedBookEntry(cents, bankLineBookConfirmed: false);
        }

        private void ClearedBookEntry(long cents, bool bankLineBookConfirmed)
        {
            var entryMonth = Month();
            long? clearedBy = null;
            var bankMonth = 0;
            if (_random.Next(3) != 0)
            {
                bankMonth = Month();
                clearedBy = Add(Txn(_id++, _random.Next(1, 28), cents, bankLineBookConfirmed, bankMonth)).Id;
            }

            _entries.Add(new BookEntry(_id++, 1, new DateOnly(2026, entryMonth, _random.Next(1, 28)), Money.FromCents(cents), "entry", clearedBy));
            var moved = ByMarch(entryMonth) || (clearedBy is not null && ByMarch(bankMonth));
            if (moved)
            {
                CashCents += cents;
            }

            Log.Add($"book entry {cents}@{entryMonth} bank {(clearedBy is null ? "-" : bankMonth.ToString(System.Globalization.CultureInfo.InvariantCulture))} → {(moved ? cents : 0)}");
        }

        private void BankLine(long cents, bool bookConfirmed)
        {
            var month = Month();
            Add(Txn(_id++, _random.Next(1, 28), cents, bookConfirmed, month));
            if (ByMarch(month))
            {
                CashCents += cents;
            }

            Log.Add($"bank line {cents}@{month} booked={bookConfirmed}");
        }

        private void StrayReceipt()
        {
            var month = Month();
            var r = Add(Rec(_id++, _random.Next(1, 28), _random.Next(1, 50_000), month));
            if (ByMarch(month))
            {
                CashCents += r.Amount.Cents;
            }

            Log.Add($"stray receipt {r.Amount.Cents}@{month}");
        }

        private BankTransaction Add(BankTransaction txn)
        {
            _txns.Add(txn);
            if (ByMarch(txn.PostingPeriod.Month))
            {
                BankCents += txn.Amount.Cents;
            }

            return txn;
        }

        private Receipt Add(Receipt receipt)
        {
            _receipts.Add(receipt);
            return receipt;
        }
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
