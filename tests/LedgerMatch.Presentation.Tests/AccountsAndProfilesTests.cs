using System.Text;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Tests.Importing;
using LedgerMatch.Presentation.ViewModels;

namespace LedgerMatch.Presentation.Tests;

public class AccountsAndProfilesTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private AccountsViewModel NewViewModel() => new(_app.Session, _app.Dialogs, _app.Files, _app.Files);

    [Fact]
    public async Task ProfileEditor_RoundTripsEveryField()
    {
        var vm = NewViewModel();
        vm.Editor.Load(TestProfiles.BankB);
        vm.Editor.Name = "乙銀行 Big5";

        await vm.Editor.SaveCommand.ExecuteAsync(null);

        var saved = _app.Session.Queries.GetProfiles().Single();
        Assert.Equal(TextEncodingKind.Big5, saved.Encoding);
        Assert.Equal(2, saved.HeaderRowCount);
        Assert.True(saved.RocYear);
        Assert.Equal(AmountMode.DebitCredit, saved.AmountMode);
        Assert.Equal(ColumnRef.ByIndex(5), saved.Note);
        Assert.Equal(ColumnRef.ByName("提款"), saved.Debit);
        Assert.Single(vm.Profiles);
    }

    [Fact]
    public async Task ProfileEditor_ShowsValidationErrorsWithoutSaving()
    {
        var vm = NewViewModel();
        vm.Editor.Name = "壞設定";
        vm.Editor.DateColumn = "日期";
        vm.Editor.DescriptionColumn = "摘要";
        vm.Editor.Delimiter = ";;";

        await vm.Editor.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Editor.ValidationErrors);
        Assert.Empty(_app.Session.Queries.GetProfiles());
    }

    [Fact]
    public async Task ProfileEditor_PreviewShowsRowsAndErrorLines()
    {
        var vm = NewViewModel();
        vm.Editor.Load(TestProfiles.BankA);
        vm.Editor.DateFormats = "yyyy-MM-dd";
        _app.Files.Files["x.csv"] = Encoding.UTF8.GetBytes("交易日期,摘要,金額,交易序號,備註\n2026-03-01,a,1,A,\n2026/03/02,b,2,B,\n");
        _app.Files.NextOpenPath = "x.csv";

        await vm.Editor.PreviewFileCommand.ExecuteAsync(null);

        Assert.Single(vm.Editor.PreviewRows);
        Assert.Equal(3, Assert.Single(vm.Editor.PreviewErrors).LineNumber);
        Assert.Contains("錯誤 1 列", vm.Editor.StatusMessage);
    }

    [Theory]
    [InlineData("交易日期", "交易日期", null)]
    [InlineData("#3", null, 3)]
    [InlineData("  ", null, null)]
    public void ColumnText_ParsesNamesAndIndexes(string text, string? name, int? index)
    {
        var column = Formatting.ParseColumn(text);
        Assert.Equal(name, column?.Name);
        Assert.Equal(index, column?.Index);
    }

    [Fact]
    public async Task Accounts_CreateAndEdit()
    {
        var vm = NewViewModel();
        vm.Editor.Load(TestProfiles.BankA);
        await vm.Editor.SaveCommand.ExecuteAsync(null);

        vm.NewAccountCommand.Execute(null);
        vm.AccountName = "甲銀行帳戶";
        vm.AccountProfile = vm.StatementProfiles.Single();
        vm.OpeningBalance = "50,000";
        vm.OpeningDate = "2026-01-01";
        await vm.SaveAccountCommand.ExecuteAsync(null);

        var account = Assert.Single(_app.Session.Queries.GetAccounts());
        Assert.Equal(5_000_000, account.OpeningBalance.Cents);

        vm.SelectedAccount = vm.Accounts.Single();
        vm.OpeningBalance = "abc";
        await vm.SaveAccountCommand.ExecuteAsync(null);
        Assert.Contains("期初餘額", _app.Dialogs.Errors.Single().Message);
    }

    [Fact]
    public async Task DeleteProfile_InUse_ShowsError()
    {
        _app.SetUpBankA();
        var vm = NewViewModel();
        vm.SelectedProfile = vm.Profiles.Single();

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        Assert.Contains("仍被帳戶使用", _app.Dialogs.Errors.Single().Message);
    }
}
