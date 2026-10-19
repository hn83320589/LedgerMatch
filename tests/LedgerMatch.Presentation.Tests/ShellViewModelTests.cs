using LedgerMatch.Core;
using LedgerMatch.Core.Tests.Importing;
using LedgerMatch.Presentation.ViewModels;

namespace LedgerMatch.Presentation.Tests;

public class ShellViewModelTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void ListsAccountsAndSelectsTheFirst()
    {
        _app.SetUpBankA();
        var shell = new ShellViewModel(_app.Session, _app.Dialogs);

        Assert.Single(shell.Accounts);
        Assert.Equal("甲銀行帳戶", shell.SelectedAccount!.Name);
        Assert.Contains(TestApp.March, shell.Months);
    }

    [Fact]
    public void ChangingMonth_UpdatesSessionAndClosedBanner()
    {
        _app.SetUpBankA();
        _app.Session.Periods.Close(new YearMonth(2026, 2));
        var shell = new ShellViewModel(_app.Session, _app.Dialogs);
        shell.Refresh();

        shell.SelectedMonth = new YearMonth(2026, 2);

        Assert.Equal(new YearMonth(2026, 2), _app.Session.CurrentMonth);
        Assert.True(shell.IsCurrentMonthClosed);
        Assert.Contains("已結帳", shell.MonthStatus);
    }

    [Fact]
    public async Task UndoRedoCommands_FollowHistory()
    {
        _app.SetUpBankA();
        var shell = new ShellViewModel(_app.Session, _app.Dialogs);
        Assert.False(shell.UndoCommand.CanExecute(null));

        _app.Session.Categories.Add("雜支");

        Assert.True(shell.UndoCommand.CanExecute(null));
        Assert.Contains("新增科目", shell.UndoDescription);
        await shell.UndoCommand.ExecuteAsync(null);
        Assert.DoesNotContain(_app.Session.Queries.GetCategories(), c => c.Name == "雜支");
        Assert.True(shell.RedoCommand.CanExecute(null));
        await shell.RedoCommand.ExecuteAsync(null);
        Assert.Single(_app.Session.Queries.GetCategories(), c => c.Name == "雜支");
    }

    [Fact]
    public async Task UndoIntoClosedMonth_ShowsMessageInsteadOfCrashing()
    {
        _app.SetUpBankA("交易日期,摘要,金額,交易序號,備註\n2026/03/02,文具,-5,T1,\n");
        var category = _app.Session.Categories.Add("文具");
        var txn = _app.Session.Queries.GetTransactions(_app.Session.CurrentAccount!.Id).Single();
        _app.Session.Classification.SetCategory([txn.Id], category.Id);
        _app.Session.Periods.Close(TestApp.March);
        var shell = new ShellViewModel(_app.Session, _app.Dialogs);

        await shell.UndoCommand.ExecuteAsync(null);

        Assert.Contains("已結帳", Assert.Single(_app.Dialogs.Errors).Message);
    }

    [Fact]
    public void NewAccount_AppearsAfterDataChanged()
    {
        var shell = new ShellViewModel(_app.Session, _app.Dialogs);
        Assert.Empty(shell.Accounts);

        var profile = _app.Session.Setup.SaveProfile(TestProfiles.BankA);
        _app.Session.Setup.CreateAccount("新帳戶", profile.Id, Money.Zero, new DateOnly(2026, 1, 1));

        Assert.Single(shell.Accounts);
    }
}
