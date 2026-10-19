using System.Text;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.ViewModels;

namespace LedgerMatch.Presentation.Tests;

public class TransactionsViewModelTests : IDisposable
{
    private readonly TestApp _app = new();

    public TransactionsViewModelTests()
    {
        var csv = new StringBuilder("交易日期,摘要,金額,交易序號,備註\n");
        for (var i = 1; i <= 450; i++)
        {
            var amount = i % 3 == 0 ? $"-{i}" : $"{i}";
            csv.Append($"2026/03/{1 + (i % 28):D2},{(i % 10 == 0 ? "房租" : "現金存入")},{amount},T{i:D4},\n");
        }

        _app.SetUpBankA(csv.ToString());
    }

    public void Dispose() => _app.Dispose();

    private TransactionsViewModel NewViewModel() => new(_app.Session, _app.Dialogs);

    [Fact]
    public async Task Loads_InPages_UntilAllRowsAreShown()
    {
        var vm = NewViewModel();
        await vm.ReloadAsync();

        Assert.Equal(450, vm.TotalCount);
        Assert.Equal(TransactionsViewModel.PageSize, vm.Items.Count);
        Assert.True(vm.HasMore);

        await vm.LoadMoreCommand.ExecuteAsync(null);
        await vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(450, vm.Items.Count);
        Assert.False(vm.HasMore);
        Assert.Equal(450, vm.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task Filters_ByStatusKeywordAmountAndDate()
    {
        var vm = NewViewModel();

        vm.Status = TransactionStatusFilter.Withdrawals;
        await vm.ReloadAsync();
        Assert.Equal(150, vm.TotalCount);
        Assert.All(vm.Items, i => Assert.Equal("支出", i.Status));

        vm.Status = TransactionStatusFilter.All;
        vm.Keyword = "房租";
        await vm.ReloadAsync();
        Assert.Equal(45, vm.TotalCount);

        vm.Keyword = string.Empty;
        vm.MinAmount = "400";
        await vm.ReloadAsync();
        Assert.Equal(34, vm.TotalCount); // 400..450 minus the 17 multiples of 3 (withdrawals)

        vm.MinAmount = string.Empty;
        vm.FromDate = "2026-03-02";
        vm.ToDate = "2026-03-02";
        await vm.ReloadAsync();
        Assert.All(vm.Items, i => Assert.Equal("2026-03-02", i.Date));

        await vm.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(450, vm.TotalCount);
    }

    [Fact]
    public async Task ApplyCategory_ToSelection_IsOneUndoStep()
    {
        var category = _app.Session.Categories.Add("租金");
        var vm = NewViewModel();
        vm.Keyword = "房租";
        await vm.ReloadAsync();
        foreach (var item in vm.Items)
        {
            vm.SelectedItems.Add(item);
        }

        vm.TargetCategory = vm.Categories.Single(c => c.Id == category.Id);
        await vm.ApplyCategoryCommand.ExecuteAsync(null);
        await vm.ReloadAsync();

        Assert.All(vm.Items, i => Assert.Equal("租金", i.Category));
        _app.Session.History.Undo();
        await vm.ReloadAsync();
        Assert.All(vm.Items, i => Assert.Equal(string.Empty, i.Category));
    }

    [Fact]
    public async Task ClosedMonth_IsReadOnly_AndEditsAreRefused()
    {
        var category = _app.Session.Categories.Add("租金");
        _app.Session.Periods.Close(TestApp.March);
        var vm = NewViewModel();
        await vm.ReloadAsync();
        vm.SelectedItems.Add(vm.Items[0]);
        vm.TargetCategory = vm.Categories.Single(c => c.Id == category.Id);

        Assert.False(vm.IsEditable);
        await vm.ApplyCategoryCommand.ExecuteAsync(null);

        Assert.Contains("已結帳", _app.Dialogs.Errors.Single().Message);
    }

    [Fact]
    public async Task Commands_WithoutSelection_ExplainWhy()
    {
        var vm = NewViewModel();
        await vm.ApplyCategoryCommand.ExecuteAsync(null);
        Assert.Contains("選取", _app.Dialogs.Errors.Single().Message);
    }

    [Fact]
    public async Task SplitEditor_PreviewMatchesSavedShares()
    {
        var a = _app.Session.Categories.Add("A");
        var b = _app.Session.Categories.Add("B");
        var vm = NewViewModel();
        vm.Keyword = "T0001";
        await vm.ReloadAsync();
        var txnId = vm.Items.Single().Id;
        vm.SelectedItems.Add(vm.Items.Single());
        await vm.EditSplitCommand.ExecuteAsync(null);
        var split = vm.Split;

        split.Lines[0].Category = split.Categories.Single(c => c.Id == a.Id);
        split.Lines[1].Category = split.Categories.Single(c => c.Id == b.Id);
        split.Lines[0].Value = "1";
        split.Lines[1].Value = "2";

        Assert.Equal(["0.33", "0.67"], split.Lines.Select(l => l.Preview)); // the larger remainder (2/3) gets the cent
        await split.SaveCommand.ExecuteAsync(null);

        var saved = _app.Session.Queries.GetSplitLines(txnId);
        Assert.Equal([33L, 67], saved.Select(l => l.Amount.Cents));
        Assert.False(split.IsOpen);
    }

    [Fact]
    public async Task SplitEditor_AmountModeShowsRemaining()
    {
        var a = _app.Session.Categories.Add("A");
        var vm = NewViewModel();
        vm.Keyword = "T0002";
        await vm.ReloadAsync();
        var txnId = vm.Items.Single().Id;
        vm.SelectedItems.Add(vm.Items.Single());
        await vm.EditSplitCommand.ExecuteAsync(null);
        var split = vm.Split;
        split.ByRatio = false;
        foreach (var line in split.Lines)
        {
            line.Category = split.Categories.Single(c => c.Id == a.Id);
        }

        split.Lines[0].Value = "1.5";
        split.Lines[1].Value = "0.4";
        Assert.Equal("尚差 0.10", split.Remaining);

        await split.SaveCommand.ExecuteAsync(null);
        Assert.Contains("合計", _app.Dialogs.Errors.Single().Message);

        split.Lines[1].Value = "0.5";
        Assert.Equal("合計等於交易金額", split.Remaining);
        await split.SaveCommand.ExecuteAsync(null);
        Assert.Equal(2, _app.Session.Queries.GetSplitLines(txnId).Count);
    }
}
