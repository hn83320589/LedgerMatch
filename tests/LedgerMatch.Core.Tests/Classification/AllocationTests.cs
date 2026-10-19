using System.Numerics;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Tests.PropertyTesting;

namespace LedgerMatch.Core.Tests.Classification;

public class AllocationTests
{
    private static long[] Cents(long total, params decimal[] weights) =>
        Allocation.ByWeights(Money.FromCents(total), weights).Select(m => m.Cents).ToArray();

    [Fact]
    public void EqualThirds_GiveLeftoverCentToFirstShare()
    {
        Assert.Equal([3334, 3333, 3333], Cents(10000, 1, 1, 1));
    }

    [Fact]
    public void LargestRemainderGetsTheCent()
    {
        // 100 × (0.25, 0.35, 0.40) = 25, 35, 40 exactly; 1 cent × (1/3, 2/3) → (0, 1): remainder 2/3 wins.
        Assert.Equal([25, 35, 40], Cents(100, 0.25m, 0.35m, 0.40m));
        Assert.Equal([0, 1], Cents(1, 1, 2));
    }

    [Fact]
    public void NegativeTotal_SharesKeepSign()
    {
        Assert.Equal([-3334, -3333, -3333], Cents(-10000, 1, 1, 1));
    }

    [Fact]
    public void ZeroWeight_GetsNothing()
    {
        Assert.Equal([0, 500, 500], Cents(1000, 0, 1, 1));
    }

    [Fact]
    public void MixedDecimalScales_AreExact()
    {
        Assert.Equal([3333, 6667], Cents(10000, 0.1m, 0.2000m));
    }

    [Fact]
    public void InvalidWeights_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => Cents(100));
        Assert.Throws<ArgumentException>(() => Cents(100, 0, 0));
        Assert.Throws<ArgumentException>(() => Cents(100, 1, -1));
    }

    [Fact]
    public void HugeValues_DoNotOverflow()
    {
        var shares = Cents(long.MaxValue / 2, 79228162514264337593543950335m, 1m, 3.3333333333333333333333333333m);
        Assert.Equal(new BigInteger(long.MaxValue / 2), shares.Aggregate(BigInteger.Zero, (sum, s) => sum + s));
    }

    /// <summary>
    /// Property (≥ 1000 random cases, fixed seed): for any amount and any non-negative weights,
    /// shares sum exactly to the amount, each is within one cent of its exact quota, zero weights get zero,
    /// and repeating the call gives identical shares.
    /// </summary>
    [Fact]
    public void Property_SharesAreCentExactStableAndFair()
    {
        Prop.ForAll(seed: 12, cases: 2000, random =>
        {
            var total = random.Next(4) switch
            {
                0 => random.NextInt64(-100, 100),
                1 => random.NextInt64(-10_000_000_000, 10_000_000_000),
                _ => random.NextInt64(-1_000_000, 1_000_000),
            };
            var weights = Enumerable.Range(0, random.Next(1, 12)).Select(_ => RandomWeight(random)).ToList();
            if (weights.All(w => w == 0m))
            {
                weights[0] = 1m;
            }

            var shares = Allocation.ByWeights(Money.FromCents(total), weights);
            var again = Allocation.ByWeights(Money.FromCents(total), weights);

            Assert.Equal(total, shares.Sum(s => s.Cents));
            Assert.Equal(shares, again);

            var weightSum = weights.Sum();
            for (var i = 0; i < weights.Count; i++)
            {
                if (weights[i] == 0m)
                {
                    Assert.Equal(0, shares[i].Cents);
                    continue;
                }

                var exact = Math.Abs(total) * weights[i] / weightSum;
                var share = Math.Abs(shares[i].Cents);
                Assert.True(Math.Abs(share - exact) < 1m, $"share {share} vs exact {exact}");
                Assert.True(shares[i].Cents == 0 || Math.Sign(shares[i].Cents) == Math.Sign(total));
            }
        });
    }

    private static decimal RandomWeight(Random random) => random.Next(5) switch
    {
        0 => 0m,
        1 => random.Next(1, 100),
        2 => Math.Round((decimal)random.Next(1, 1_000_000) / 997m, random.Next(0, 10)),
        3 => random.Next(1, 4) / 3m,
        _ => random.Next(1, 10_000) / 100m,
    };
}
