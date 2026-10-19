using LedgerMatch.Core;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Tests.Importing;

namespace LedgerMatch.Data.Tests.Setup;

public class SetupServiceTests : IDisposable
{
    private readonly TestLedger _ledger = new();

    public void Dispose() => _ledger.Dispose();

    [Fact]
    public void Profiles_CanBeCreatedEditedAndSurviveRestart()
    {
        var created = _ledger.Setup.SaveProfile(TestProfiles.BankB with { Id = 0 });
        var edited = _ledger.Setup.SaveProfile(created with { Delimiter = ';', DateFormats = ["yyyy/MM/dd", "yyyy.MM.dd"], Note = ColumnRef.ByName("附註") });

        _ledger.Reopen();
        var reloaded = _ledger.Store.GetProfile(created.Id)!;

        Assert.Equal(';', reloaded.Delimiter);
        Assert.Equal(["yyyy/MM/dd", "yyyy.MM.dd"], reloaded.DateFormats);
        Assert.Equal(ColumnRef.ByName("附註"), reloaded.Note);
        Assert.Equal(TextEncodingKind.Big5, reloaded.Encoding);
        Assert.True(reloaded.RocYear);
        Assert.Equal(AmountMode.DebitCredit, reloaded.AmountMode);
        Assert.Equal(edited.Name, reloaded.Name);
    }

    [Fact]
    public void InvalidProfile_IsRejected()
    {
        var ex = Assert.Throws<LedgerException>(() => _ledger.Setup.SaveProfile(TestProfiles.BankA with { Id = 0, Amount = null }));
        Assert.Contains("金額欄", ex.Message);
    }

    [Fact]
    public void DuplicateNames_AreRejected()
    {
        _ledger.Setup.SaveProfile(TestProfiles.BankA with { Id = 0 });
        Assert.Throws<LedgerException>(() => _ledger.Setup.SaveProfile(TestProfiles.BankA with { Id = 0 }));
    }

    [Fact]
    public void ProfileInUse_CannotBeDeleted()
    {
        var account = _ledger.BankA();
        var ex = Assert.Throws<LedgerException>(() => _ledger.Setup.DeleteProfile(account.ProfileId));
        Assert.Contains("甲銀行帳戶", ex.Message);
    }

    [Fact]
    public void Account_MustBindStatementProfile()
    {
        var receipts = _ledger.Setup.SaveProfile(TestProfiles.Receipts with { Id = 0 });
        Assert.Throws<LedgerException>(() => _ledger.Setup.CreateAccount("x", receipts.Id, Money.Zero, new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void ProfileEdit_IsUndoable()
    {
        var created = _ledger.Setup.SaveProfile(TestProfiles.BankA with { Id = 0 });
        _ledger.Setup.SaveProfile(created with { HeaderRowCount = 3 });

        _ledger.History.Undo();

        Assert.Equal(1, _ledger.Store.GetProfile(created.Id)!.HeaderRowCount);
    }

    [Fact]
    public void EditedProfile_IsUsedByNextImport()
    {
        var account = _ledger.BankA();
        var profile = _ledger.Store.GetProfile(account.ProfileId)!;
        _ledger.Setup.SaveProfile(profile with { DateFormats = ["yyyy-MM-dd"] });

        var summary = _ledger.ImportUtf8(account, "交易日期,摘要,金額,交易序號,備註\n2026-03-01,x,1,A,\n2026/03/02,y,1,B,\n");

        Assert.Equal(1, summary.Added);
        Assert.Equal(3, Assert.Single(summary.Errors).LineNumber);
    }
}
