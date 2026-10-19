namespace LedgerMatch.Core.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("12.3", 1230)]
    [InlineData("-1234.56", -123456)]
    [InlineData("0.01", 1)]
    public void FromDecimal_ConvertsToCents(string value, long cents)
    {
        Assert.Equal(cents, Money.FromDecimal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).Cents);
    }

    [Fact]
    public void FromDecimal_RejectsMoreThanTwoDecimals()
    {
        Assert.Throws<ArgumentException>(() => Money.FromDecimal(1.005m));
    }

    [Fact]
    public void FromDecimal_AcceptsTrailingZerosBeyondTwoDecimals()
    {
        Assert.Equal(100, Money.FromDecimal(1.0000m).Cents);
    }

    [Fact]
    public void Arithmetic_IsExact()
    {
        var a = Money.FromCents(10);
        var b = Money.FromCents(20);
        Assert.Equal(Money.FromCents(30), a + b);
        Assert.Equal(Money.FromCents(-10), a - b);
        Assert.Equal(Money.FromCents(-10), -a);
        Assert.True(a < b);
    }

    [Fact]
    public void Addition_ThrowsOnOverflow()
    {
        Assert.Throws<OverflowException>(() => Money.FromCents(long.MaxValue) + Money.FromCents(1));
    }

    [Theory]
    [InlineData(123456789, "1,234,567.89")]
    [InlineData(-5, "-0.05")]
    [InlineData(0, "0.00")]
    public void ToString_UsesInvariantGroupedFormat(long cents, string expected)
    {
        Assert.Equal(expected, Money.FromCents(cents).ToString());
    }

    [Fact]
    public void ToPlainString_HasNoGrouping()
    {
        Assert.Equal("-1234567.89", Money.FromCents(-123456789).ToPlainString());
    }

    [Fact]
    public void Sum_AddsAll()
    {
        Assert.Equal(Money.FromCents(6), new[] { 1L, 2L, 3L }.Select(Money.FromCents).Sum());
    }
}
