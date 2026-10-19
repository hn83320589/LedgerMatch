using System.Numerics;

namespace LedgerMatch.Core.Classification;

/// <summary>
/// Splits an amount into cent-exact shares by weight using the largest remainder (Hamilton) method
/// (README "拆分分配"):
/// <list type="number">
/// <item>Exact quota qᵢ = |T| × wᵢ / Σw, computed with integer (BigInteger) arithmetic — no rounding.</item>
/// <item>Every share first gets ⌊qᵢ⌋ cents.</item>
/// <item>The R = |T| − Σ⌊qᵢ⌋ leftover cents go one each to the shares with the largest remainders;
/// ties go to the earlier share.</item>
/// <item>The sign of T is applied to every share.</item>
/// </list>
/// Shares always sum to T, each differs from its exact quota by less than one cent, zero weights get zero,
/// and the same input always gives the same output.
/// </summary>
public static class Allocation
{
    public static IReadOnlyList<Money> ByWeights(Money total, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0)
        {
            throw new ArgumentException("至少需要一個比例。", nameof(weights));
        }

        if (weights.Any(w => w < 0m))
        {
            throw new ArgumentException("比例不可為負數。", nameof(weights));
        }

        var scaled = ToCommonScale(weights);
        var weightSum = scaled.Aggregate(BigInteger.Zero, (a, b) => a + b);
        if (weightSum.IsZero)
        {
            throw new ArgumentException("比例總和必須大於零。", nameof(weights));
        }

        var magnitude = new BigInteger(Math.Abs(total.Cents));
        var floors = new long[scaled.Length];
        var remainders = new BigInteger[scaled.Length];
        var assigned = BigInteger.Zero;
        for (var i = 0; i < scaled.Length; i++)
        {
            var quotient = BigInteger.DivRem(magnitude * scaled[i], weightSum, out var remainder);
            floors[i] = (long)quotient;
            remainders[i] = remainder;
            assigned += quotient;
        }

        var leftover = (int)(magnitude - assigned);
        var order = Enumerable.Range(0, scaled.Length)
            .OrderByDescending(i => remainders[i])
            .ThenBy(i => i)
            .Take(leftover);
        foreach (var i in order)
        {
            floors[i]++;
        }

        var sign = Math.Sign(total.Cents);
        return floors.Select(c => Money.FromCents(sign * c)).ToList();
    }

    /// <summary>Turns decimals into integers sharing one power-of-ten denominator, exactly.</summary>
    private static BigInteger[] ToCommonScale(IReadOnlyList<decimal> weights)
    {
        var parts = weights.Select(w =>
        {
            var bits = decimal.GetBits(w);
            var mantissa = (new BigInteger((uint)bits[2]) << 64) | (new BigInteger((uint)bits[1]) << 32) | new BigInteger((uint)bits[0]);
            var scale = (bits[3] >> 16) & 0xFF;
            return (Mantissa: mantissa, Scale: scale);
        }).ToList();

        var maxScale = parts.Max(p => p.Scale);
        return parts.Select(p => p.Mantissa * BigInteger.Pow(10, maxScale - p.Scale)).ToArray();
    }
}
