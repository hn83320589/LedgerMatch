using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Tests.Importing;

public class MappingProfileTests
{
    [Fact]
    public void SampleProfiles_AreValid()
    {
        Assert.Empty(TestProfiles.BankA.Validate());
        Assert.Empty(TestProfiles.BankB.Validate());
        Assert.Empty(TestProfiles.Receipts.Validate());
    }

    [Fact]
    public void DebitCreditMode_RequiresBothColumns()
    {
        var profile = TestProfiles.BankB with { Credit = null };
        Assert.Contains(profile.Validate(), e => e.Contains("貸方"));
    }

    [Fact]
    public void ColumnNames_RequireHeaderRow()
    {
        var profile = TestProfiles.BankA with { HeaderRowCount = 0 };
        Assert.Contains(profile.Validate(), e => e.Contains("標題列數"));
    }

    [Fact]
    public void ColumnIndex_MustBePositive()
    {
        var profile = TestProfiles.BankA with { Note = ColumnRef.ByIndex(0) };
        Assert.Contains(profile.Validate(), e => e.Contains("欄號"));
    }

    [Fact]
    public void Receipts_RequireCustomerAndNumber()
    {
        var profile = TestProfiles.Receipts with { Customer = null, ReceiptNo = null };
        var errors = profile.Validate();
        Assert.Contains(errors, e => e.Contains("客戶"));
        Assert.Contains(errors, e => e.Contains("單據編號"));
    }

    [Fact]
    public void EmptyDateFormats_AreInvalid()
    {
        Assert.Contains((TestProfiles.BankA with { DateFormats = [] }).Validate(), e => e.Contains("日期格式"));
    }
}
