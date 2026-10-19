using System.Globalization;

namespace LedgerMatch.Core;

/// <summary>An exact NTD amount stored as integer cents. Never use floating point for money.</summary>
public readonly record struct Money(long Cents) : IComparable<Money>
{
    public static readonly Money Zero = new(0);

    public static Money FromCents(long cents) => new(cents);

    public static Money FromDecimal(decimal value)
    {
        var scaled = value * 100m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException($"金額 {value} 超過小數點後兩位。", nameof(value));
        }

        return new Money(decimal.ToInt64(scaled));
    }

    public decimal ToDecimal() => Cents / 100m;

    public bool IsPositive => Cents > 0;

    public bool IsNegative => Cents < 0;

    public Money Abs() => new(Math.Abs(Cents));

    public static Money operator +(Money a, Money b) => new(checked(a.Cents + b.Cents));

    public static Money operator -(Money a, Money b) => new(checked(a.Cents - b.Cents));

    public static Money operator -(Money a) => new(checked(-a.Cents));

    public static bool operator <(Money a, Money b) => a.Cents < b.Cents;

    public static bool operator >(Money a, Money b) => a.Cents > b.Cents;

    public static bool operator <=(Money a, Money b) => a.Cents <= b.Cents;

    public static bool operator >=(Money a, Money b) => a.Cents >= b.Cents;

    public int CompareTo(Money other) => Cents.CompareTo(other.Cents);

    /// <summary>Grouped invariant format, e.g. "1,234.56".</summary>
    public override string ToString() => ToDecimal().ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>Ungrouped invariant format for machine-readable output, e.g. "1234.56".</summary>
    public string ToPlainString() => ToDecimal().ToString("0.00", CultureInfo.InvariantCulture);
}

public static class MoneyExtensions
{
    public static Money Sum(this IEnumerable<Money> source)
    {
        var total = Money.Zero;
        foreach (var m in source)
        {
            total += m;
        }

        return total;
    }

    public static Money Sum<T>(this IEnumerable<T> source, Func<T, Money> selector) => source.Select(selector).Sum();
}
