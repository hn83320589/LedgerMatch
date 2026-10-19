using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Tests.Importing;

public class DateParserTests
{
    [Theory]
    [InlineData("2026/03/01", "yyyy/MM/dd")]
    [InlineData("2026-03-01", "yyyy-MM-dd")]
    [InlineData("20260301", "yyyyMMdd")]
    [InlineData("2026/3/1", "yyyy/M/d")]
    public void ParsesGregorianFormats(string raw, string format)
    {
        Assert.True(DateParser.TryParse(raw, [format], rocYear: false, out var date, out var error), error);
        Assert.Equal(new DateOnly(2026, 3, 1), date);
    }

    [Theory]
    [InlineData("115/03/01", "yyyy/MM/dd")]
    [InlineData("115.03.01", "yyyy.MM.dd")]
    [InlineData("1150301", "yyyyMMdd")]
    [InlineData("99/03/01", "yyyy/MM/dd")]
    public void ParsesRocYear(string raw, string format)
    {
        Assert.True(DateParser.TryParse(raw, [format], rocYear: true, out var date, out var error), error);
        Assert.Equal(raw.StartsWith("99", StringComparison.Ordinal) ? new DateOnly(2010, 3, 1) : new DateOnly(2026, 3, 1), date);
    }

    [Fact]
    public void TriesFormatsInOrder()
    {
        Assert.True(DateParser.TryParse("2026-03-01", ["yyyy/MM/dd", "yyyy-MM-dd"], false, out var date, out _));
        Assert.Equal(new DateOnly(2026, 3, 1), date);
    }

    [Theory]
    [InlineData("2026/02/30")]
    [InlineData("03/01/2026")]
    [InlineData("")]
    public void RejectsMismatchedOrInvalidDates(string raw)
    {
        Assert.False(DateParser.TryParse(raw, ["yyyy/MM/dd"], false, out _, out var error));
        Assert.NotEmpty(error);
    }
}
