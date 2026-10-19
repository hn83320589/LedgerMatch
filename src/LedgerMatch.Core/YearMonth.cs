using System.Globalization;

namespace LedgerMatch.Core;

/// <summary>A calendar month; the unit of period closing.</summary>
public readonly record struct YearMonth : IComparable<YearMonth>
{
    public YearMonth(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        Year = year;
        Month = month;
    }

    public int Year { get; }

    public int Month { get; }

    public static YearMonth Of(DateOnly date) => new(date.Year, date.Month);

    public DateOnly FirstDay => new(Year, Month, 1);

    public DateOnly LastDay => new(Year, Month, DateTime.DaysInMonth(Year, Month));

    public bool Contains(DateOnly date) => date.Year == Year && date.Month == Month;

    public YearMonth Next() => Month == 12 ? new(Year + 1, 1) : new(Year, Month + 1);

    public YearMonth Previous() => Month == 1 ? new(Year - 1, 12) : new(Year, Month - 1);

    public static YearMonth Parse(string text)
    {
        var date = DateOnly.ParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Of(date);
    }

    public int CompareTo(YearMonth other) => (Year * 12 + Month).CompareTo(other.Year * 12 + other.Month);

    public static bool operator <(YearMonth a, YearMonth b) => a.CompareTo(b) < 0;

    public static bool operator >(YearMonth a, YearMonth b) => a.CompareTo(b) > 0;

    public static bool operator <=(YearMonth a, YearMonth b) => a.CompareTo(b) <= 0;

    public static bool operator >=(YearMonth a, YearMonth b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}
