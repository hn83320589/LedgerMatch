using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Presentation.ViewModels;

namespace LedgerMatch.Presentation.Tests;

public class MatchingAndReceiptsTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly Account _account;

    public MatchingAndReceiptsTests()
    {
        _account = _app.SetUpBankA(
            "交易日期,摘要,金額,交易序號,備註\n" +
            "2026/03/02,現金存入,1200,T1,\n" +
            "2026/03/10,刷卡撥款,1960,T2,\n" +
            "2026/03/15,匯款,4985,T3,\n" +
            "2026/03/20,利息,0.75,T4,\n");
        AddReceipt("R-1", 120000, 2);
        AddReceipt("R-2", 100000, 8);
        AddReceipt("R-3", 96000, 9);
        AddReceipt("R-4", 500000, 14);
        AddReceipt("R-5", 7500, 19);
        _app.Session.History.Clear();
    }

    public void Dispose() => _app.Dispose();

    private void AddReceipt(string no, long cents, int day) =>
        _app.Session.Receipts.Add(_account.Id, new DateOnly(2026, 3, day), "客戶", no, Money.FromCents(cents), null);

    private MatchingViewModel NewMatching() => new(_app.Session, _app.Dialogs);

    [Fact]
    public async Task Run_ShowsConfirmedAndSuggestedMatches()
    {
        var vm = NewMatching();

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Matches.Count);
        Assert.Equal(["已確認", "建議", "建議"], vm.Matches.OrderBy(m => m.Deposit.TxnDate).Select(m => m.Confidence));
        Assert.Contains("手續費 15.00", vm.Matches.Select(m => m.Fee));
        Assert.Single(vm.UnmatchedDeposits);
        Assert.Single(vm.UnmatchedReceipts);
        Assert.Contains("自動確認 1 筆，建議 2 筆", vm.StatusMessage);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task ConfirmAll_ThenUnmatchSelected()
    {
        var vm = NewMatching();
        await vm.RunCommand.ExecuteAsync(null);

        await vm.ConfirmAllSuggestionsCommand.ExecuteAsync(null);
        Assert.All(vm.Matches, m => Assert.Equal("已確認", m.Confidence));

        vm.Matches[0].IsSelected = true;
        await vm.UnmatchSelectedCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Matches.Count);
    }

    [Fact]
    public async Task RejectSelected_KeepsConfirmedMatches()
    {
        var vm = NewMatching();
        await vm.RunCommand.ExecuteAsync(null);
        foreach (var m in vm.Matches)
        {
            m.IsSelected = true;
        }

        await vm.RejectSelectedCommand.ExecuteAsync(null);

        Assert.Equal("已確認", Assert.Single(vm.Matches).Confidence);
    }

    [Fact]
    public async Task ManualMatch_WithFee_RequiresOptIn()
    {
        var vm = NewMatching();
        vm.UnmatchedDeposits.Single(d => d.Transaction.Serial == "T3").IsSelected = true;
        vm.UnmatchedReceipts.Single(r => r.Receipt.ReceiptNo == "R-4").IsSelected = true;
        Assert.Contains("差額 15.00", vm.ManualSummary);

        await vm.ManualMatchCommand.ExecuteAsync(null);
        Assert.Contains("手續費", _app.Dialogs.Errors.Single().Message);

        vm.UnmatchedDeposits.Single(d => d.Transaction.Serial == "T3").IsSelected = true;
        vm.UnmatchedReceipts.Single(r => r.Receipt.ReceiptNo == "R-4").IsSelected = true;
        vm.AcceptDifferenceAsFee = true;
        await vm.ManualMatchCommand.ExecuteAsync(null);

        var match = Assert.Single(vm.Matches);
        Assert.Equal(("人工", "已確認"), (match.Kind, match.Confidence));
    }

    [Fact]
    public async Task ActionsWithoutSelection_ShowMessage()
    {
        var vm = NewMatching();
        await vm.ConfirmSelectedCommand.ExecuteAsync(null);
        Assert.Contains("勾選", _app.Dialogs.Errors.Single().Message);
    }

    [Fact]
    public async Task Run_InClosedMonth_ShowsMessageAndLeavesDataAlone()
    {
        _app.Session.Periods.Close(TestApp.March);
        var vm = NewMatching();

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Contains("已結帳", _app.Dialogs.Errors.Single().Message);
        Assert.Empty(vm.Matches);
        Assert.False(vm.IsEditable);
    }

    [Fact]
    public async Task Receipts_AddEditDelete()
    {
        var vm = new ReceiptsViewModel(_app.Session, _app.Dialogs);
        Assert.Equal(5, vm.Items.Count);

        vm.NewCommand.Execute(null);
        vm.IssueDate = "2026-03-21";
        vm.Customer = "新客戶";
        vm.ReceiptNo = "R-9";
        vm.Amount = "1,000";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(6, vm.Items.Count);

        vm.Selected = vm.Items.Single(i => i.Receipt.ReceiptNo == "R-9");
        vm.Note = "改備註";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("改備註", _app.Session.Queries.GetReceiptByNumber("R-9")!.Note);

        vm.Selected = vm.Items.Single(i => i.Receipt.ReceiptNo == "R-9");
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.Items.Count);
    }

    [Fact]
    public async Task Receipts_DeletingMatchedReceipt_ExplainsUnmatchFirst()
    {
        await NewMatching().RunCommand.ExecuteAsync(null);
        var vm = new ReceiptsViewModel(_app.Session, _app.Dialogs) { OnlyUnmatched = false };
        vm.Selected = vm.Items.Single(i => i.Receipt.ReceiptNo == "R-1");

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Contains("解除配對", _app.Dialogs.Errors.Single().Message);
        vm.OnlyUnmatched = true;
        Assert.DoesNotContain(vm.Items, i => i.Receipt.ReceiptNo == "R-1");
    }

    [Fact]
    public async Task Receipts_InvalidInput_IsReported()
    {
        var vm = new ReceiptsViewModel(_app.Session, _app.Dialogs);
        vm.NewCommand.Execute(null);
        vm.IssueDate = "3/21";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("日期", _app.Dialogs.Errors.Single().Message);
    }
}
