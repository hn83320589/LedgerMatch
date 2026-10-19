using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Tests.PropertyTesting;

namespace LedgerMatch.Core.Tests.Matching;

public class MatchingEnginePropertyTests
{
    /// <summary>
    /// Property: on random inputs the engine (1) never uses a deposit or receipt twice, (2) only proposes pairings
    /// that satisfy their stage's amount rule, and (3) returns the same result when inputs are shuffled and ids
    /// are renumbered.
    /// </summary>
    [Fact]
    public void Property_ValidAndDeterministicUnderReorderingAndRenumbering()
    {
        var settings = MatchingSettings.Default with { MaxCandidates = 10, MaxComboSize = 4 };

        Prop.ForAll(seed: 4242, cases: 200, random =>
        {
            var (deposits, receipts) = Generate(random);

            var result = MatchingEngine.Run(deposits, receipts, settings);
            AssertValid(result, deposits, receipts, settings);

            // Shuffle and renumber: compare by business keys.
            var depositIdMap = deposits.ToDictionary(d => d.Id, _ => (long)random.Next(1, int.MaxValue));
            var receiptIdMap = receipts.ToDictionary(r => r.Id, _ => (long)random.Next(1, int.MaxValue));
            var shuffledDeposits = random.Shuffle(deposits.Select(d => d with { Id = depositIdMap[d.Id] }));
            var shuffledReceipts = random.Shuffle(receipts.Select(r => r with { Id = receiptIdMap[r.Id] }));

            var again = MatchingEngine.Run(shuffledDeposits, shuffledReceipts, settings);

            Assert.Equal(Canonical(result, deposits, receipts), Canonical(again, shuffledDeposits, shuffledReceipts));
        });
    }

    private static (List<DepositCandidate>, List<ReceiptCandidate>) Generate(Random random)
    {
        var start = new DateOnly(2026, 3, 1);
        var receipts = new List<ReceiptCandidate>();
        var deposits = new List<DepositCandidate>();
        var receiptNo = 0;
        var depositNo = 0;

        for (var i = 0; i < random.Next(3, 12); i++)
        {
            // Build deposits from groups of receipts so stage 2/3 have real work; add noise receipts too.
            var groupSize = random.Next(1, 4);
            var date = start.AddDays(random.Next(0, 20));
            long total = 0;
            for (var g = 0; g < groupSize; g++)
            {
                var amount = random.Next(1, 30) * 100L;
                total += amount;
                receipts.Add(new ReceiptCandidate(1000 + receiptNo, date.AddDays(-random.Next(0, 4)), Money.FromCents(amount), $"R{receiptNo++:D3}"));
            }

            var fee = random.Next(3) == 0 ? random.Next(1, 40) * 10L : 0;
            deposits.Add(new DepositCandidate(1 + depositNo, date, Money.FromCents(total - fee), random.Next(5) == 0 ? $"ref R{receiptNo - 1:D3}" : "存入", $"key{depositNo++}"));
        }

        for (var i = 0; i < random.Next(0, 6); i++)
        {
            receipts.Add(new ReceiptCandidate(1000 + receiptNo, start.AddDays(random.Next(0, 20)), Money.FromCents(random.Next(1, 30) * 100L), $"R{receiptNo++:D3}"));
        }

        return (deposits, receipts);
    }

    private static void AssertValid(MatchingResult result, List<DepositCandidate> deposits, List<ReceiptCandidate> receipts, MatchingSettings settings)
    {
        Assert.Equal(result.Proposals.Count, result.Proposals.Select(p => p.DepositId).Distinct().Count());
        var used = result.Proposals.SelectMany(p => p.ReceiptIds).ToList();
        Assert.Equal(used.Count, used.Distinct().Count());

        foreach (var p in result.Proposals)
        {
            var deposit = deposits.Single(d => d.Id == p.DepositId);
            var total = p.ReceiptIds.Select(id => receipts.Single(r => r.Id == id).Amount).Sum();
            Assert.Equal(total, deposit.Amount + p.Fee);
            switch (p.Kind)
            {
                case MatchKind.Exact:
                    Assert.Single(p.ReceiptIds);
                    Assert.Equal(Money.Zero, p.Fee);
                    Assert.Equal(MatchConfidence.Confirmed, p.Confidence);
                    break;
                case MatchKind.OneToMany:
                    Assert.InRange(p.ReceiptIds.Count, 2, settings.MaxComboSize);
                    Assert.Equal(Money.Zero, p.Fee);
                    Assert.Equal(MatchConfidence.Suggested, p.Confidence);
                    break;
                case MatchKind.WithFee:
                    Assert.True(p.Fee.IsPositive);
                    Assert.True(p.Fee <= settings.AllowedFee(total));
                    Assert.Equal(MatchConfidence.Suggested, p.Confidence);
                    break;
                default:
                    Assert.Fail($"Unexpected kind {p.Kind}");
                    break;
            }
        }
    }

    private static List<string> Canonical(MatchingResult result, IEnumerable<DepositCandidate> deposits, IEnumerable<ReceiptCandidate> receipts)
    {
        var depositKey = deposits.ToDictionary(d => d.Id, d => d.DedupKey);
        var receiptNo = receipts.ToDictionary(r => r.Id, r => r.ReceiptNo);
        return result.Proposals
            .Select(p => $"{depositKey[p.DepositId]}:{p.Kind}:{p.Fee.Cents}:{string.Join(",", p.ReceiptIds.Select(id => receiptNo[id]))}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
