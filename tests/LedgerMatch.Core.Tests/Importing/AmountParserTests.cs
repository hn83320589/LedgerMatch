using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Tests.Importing;

public class AmountParserTests
{
    [Theory]
    [InlineData("1234", 123400)]
    [InlineData("1,234.5", 123450)]
    [InlineData("1,234,567.89", 123456789)]
    [InlineData("-1,200.00", -120000)]
    [InlineData("+15", 1500)]
    [InlineData("(1,200.50)", -120050)]
    [InlineData("300-", -30000)]
    [InlineData("NT$ 99", 9900)]
    [InlineData(" 0.07 ", 7)]
    [InlineData("$1,000", 100000)]
    public void ParsesCommonBankFormats(string raw, long cents)
    {
        Assert.True(AmountParser.TryParse(raw, ',', parenthesesNegative: true, out var amount, out var error), error);
        Assert.Equal(cents, amount.Cents);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("1.234")]
    [InlineData("--5")]
    public void RejectsInvalidText(string raw)
    {
        Assert.False(AmountParser.TryParse(raw, ',', true, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RejectsParenthesesWhenDisabled()
    {
        Assert.False(AmountParser.TryParse("(5)", ',', parenthesesNegative: false, out _, out var error));
        Assert.Contains("括號", error);
    }

    [Fact]
    public void RejectsGroupingWhenNoThousandsSeparatorConfigured()
    {
        Assert.False(AmountParser.TryParse("1,000", null, true, out _, out _));
    }

    [Fact]
    public void ReportsTooManyDecimals()
    {
        Assert.False(AmountParser.TryParse("1.005", ',', true, out _, out var error));
        Assert.Contains("兩位", error);
    }
}
