namespace LedgerMatch.Core.Matching;

/// <summary>Engine parameters. Defaults and allowed ranges are documented in README "配對參數".</summary>
public sealed record MatchingSettings
{
    public const int DefaultNodeBudget = 2_000_000;

    /// <summary>Stage 1: |deposit date − receipt date| must be ≤ this many days (inclusive).</summary>
    public int ExactDateToleranceDays { get; init; } = 3;

    /// <summary>Stages 2–3: receipts issued on the deposit date or up to this many days before it.</summary>
    public int LookbackDays { get; init; } = 7;

    /// <summary>Stages 2–3: at most this many nearest receipts are considered per deposit.</summary>
    public int MaxCandidates { get; init; } = 20;

    /// <summary>Stages 2–3: largest number of receipts in one combination.</summary>
    public int MaxComboSize { get; init; } = 6;

    /// <summary>Stages 2–3: search steps allowed per deposit before giving up on it (no match is produced).</summary>
    public int NodeBudget { get; init; } = DefaultNodeBudget;

    /// <summary>Stage 3: fixed fee cap; null disables this criterion.</summary>
    public Money? FeeFixedCap { get; init; } = Money.FromCents(3000);

    /// <summary>Stage 3: fee cap as a fraction of the receipts total (0.03 = 3%); null disables this criterion.</summary>
    public decimal? FeeRateCap { get; init; } = 0.03m;

    public static MatchingSettings Default { get; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        Range(errors, ExactDateToleranceDays, 0, 31, "精確配對日期容許天數");
        Range(errors, LookbackDays, 0, 31, "候選回溯天數");
        Range(errors, MaxCandidates, 2, 30, "單次候選收款單上限");
        Range(errors, MaxComboSize, 2, 8, "組合大小上限");
        if (NodeBudget < 1_000)
        {
            errors.Add("搜尋節點預算至少 1,000。");
        }

        if (FeeFixedCap is { } cap && (cap.IsNegative || cap > Money.FromCents(100_000)))
        {
            errors.Add("手續費固定上限必須介於 0 到 1,000 元。");
        }

        if (FeeRateCap is { } rate && (rate < 0m || rate > 0.10m))
        {
            errors.Add("手續費比例上限必須介於 0% 到 10%。");
        }

        return errors;
    }

    public bool FeeMatchingEnabled => FeeFixedCap is not null || FeeRateCap is not null;

    /// <summary>Largest fee accepted for a receipts total: max(fixed cap, floor(total × rate)) over the enabled criteria.</summary>
    public Money AllowedFee(Money receiptsTotal)
    {
        var allowed = 0L;
        if (FeeFixedCap is { } cap)
        {
            allowed = cap.Cents;
        }

        if (FeeRateCap is { } rate)
        {
            allowed = Math.Max(allowed, (long)decimal.Floor(receiptsTotal.Cents * rate));
        }

        return Money.FromCents(allowed);
    }

    private static void Range(List<string> errors, int value, int min, int max, string label)
    {
        if (value < min || value > max)
        {
            errors.Add($"{label}必須介於 {min} 到 {max}。");
        }
    }
}
