namespace LedgerMatch.Core.Tests;

public class YearMonthTests
{
    [Fact]
    public void Of_TakesMonthOfDate()
    {
        Assert.Equal(new YearMonth(2026, 2), YearMonth.Of(new DateOnly(2026, 2, 28)));
    }

    [Fact]
    public void FirstAndLastDay_CoverWholeMonth()
    {
        var ym = new YearMonth(2024, 2);
        Assert.Equal(new DateOnly(2024, 2, 1), ym.FirstDay);
        Assert.Equal(new DateOnly(2024, 2, 29), ym.LastDay);
    }

    [Fact]
    public void Next_RollsOverYear()
    {
        Assert.Equal(new YearMonth(2027, 1), new YearMonth(2026, 12).Next());
    }

    [Fact]
    public void ToString_And_Parse_RoundTrip()
    {
        var ym = new YearMonth(2026, 3);
        Assert.Equal("2026-03", ym.ToString());
        Assert.Equal(ym, YearMonth.Parse("2026-03"));
    }

    [Fact]
    public void Constructor_RejectsInvalidMonth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YearMonth(2026, 13));
    }

    [Fact]
    public void Comparison_OrdersChronologically()
    {
        Assert.True(new YearMonth(2025, 12) < new YearMonth(2026, 1));
    }
}
