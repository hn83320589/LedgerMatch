using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Tests.Importing;

public class CsvReaderTests
{
    private static List<CsvRecord> Read(string text, char delimiter = ',') =>
        CsvReader.Read(new StringReader(text), delimiter).ToList();

    [Fact]
    public void SplitsSimpleFieldsAndTracksLineNumbers()
    {
        var records = Read("a,b,c\r\n1,2,3\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(["a", "b", "c"], records[0].Fields);
        Assert.Equal(1, records[0].LineNumber);
        Assert.Equal(["1", "2", "3"], records[1].Fields);
        Assert.Equal(2, records[1].LineNumber);
    }

    [Fact]
    public void HandlesQuotedDelimiterEscapedQuoteAndEmbeddedNewline()
    {
        var records = Read("\"x,y\",\"say \"\"hi\"\"\",\"line1\nline2\"\nnext,row,here");

        Assert.Equal(["x,y", "say \"hi\"", "line1\nline2"], records[0].Fields);
        Assert.Equal(1, records[0].LineNumber);
        Assert.Equal(3, records[1].LineNumber);
    }

    [Fact]
    public void KeepsEmptyFields()
    {
        var records = Read("a,,c,");
        Assert.Equal(["a", "", "c", ""], records[0].Fields);
    }

    [Fact]
    public void SkipsBlankLinesButCountsThem()
    {
        var records = Read("a\n\n\r\nb\n");
        Assert.Equal(2, records.Count);
        Assert.Equal(4, records[1].LineNumber);
    }

    [Fact]
    public void SupportsOtherDelimiters()
    {
        Assert.Equal(["1", "2"], Read("1;2", ';')[0].Fields);
        Assert.Equal(["1", "2"], Read("1\t2", '\t')[0].Fields);
    }

    [Fact]
    public void ReportsUnterminatedQuoteAsRecordError()
    {
        var records = Read("ok,1\n\"broken,2\n");

        Assert.Null(records[0].Error);
        Assert.NotNull(records[1].Error);
        Assert.Equal(2, records[1].LineNumber);
    }

    [Fact]
    public void HandlesLoneCarriageReturnLineEnding()
    {
        var records = Read("a\rb");
        Assert.Equal(2, records.Count);
        Assert.Equal(2, records[1].LineNumber);
    }
}
