using System.Text;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Presentation.ViewModels;

namespace LedgerMatch.Presentation.Tests;

public class RulesPeriodsReportsTests : IDisposable
{
    private const string Header = "交易日期,摘要,金額,交易序號,備註\n";
    private readonly TestApp _app = new();
    private readonly Account _account;

    public RulesPeriodsReportsTests()
    {
        _account = _app.SetUpBankA(Header +
            "2026/03/01,台電電費,-1500,T1,\n" +
            "2026/03/02,自來水費,-300,T2,\n" +
            "2026/03/03,現金存入,1000,T3,\n" +
            "2026/03/04,房租,-20000,T4,\n");
        _app.Session.Receipts.Add(_account.Id, new DateOnly(2026, 3, 3), "王", "R-1", Money.FromCents(100_000), null);
        _app.Session.History.Clear();
    }

    public void Dispose() => _app.Dispose();

    private Transaction Txn(string serial) => new(_app.Session.Queries.GetTransactions(_account.Id).Single(t => t.Serial == serial));

    private sealed record Transaction(BankTransaction Value);

    // ---------- rules ----------

    [Fact]
    public async Task Rules_PreviewDoesNotWrite_ApplyIsUndoable()
    {
        var vm = new RulesViewModel(_app.Session, _app.Dialogs);
        vm.CategoryName = "水電";
        await vm.AddCategoryCommand.ExecuteAsync(null);
        vm.NewRuleCommand.Execute(null);
        vm.RuleName = "水電";
        vm.Regex = "^(台電|自來水)";
        vm.RuleCategory = vm.Categories.Single(c => c.Name == "水電");
        await vm.SaveRuleCommand.ExecuteAsync(null);
        var auditBefore = _app.Store.GetAuditEntries(0, 10000).Count;

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.PreviewHits.Count);
        Assert.All(vm.PreviewHits, h => Assert.Equal(("（未歸類）", "水電"), (h.OldCategory, h.NewCategory)));
        Assert.Equal(auditBefore, _app.Store.GetAuditEntries(0, 10000).Count);
        Assert.Null(Txn("T1").Value.CategoryId);

        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.NotNull(Txn("T1").Value.CategoryId);
        Assert.Empty(vm.PreviewHits);

        _app.Session.History.Undo();
        Assert.Null(Txn("T1").Value.CategoryId);
        Assert.Null(Txn("T2").Value.CategoryId);
    }

    [Fact]
    public async Task Rules_MoveUpChangesPriority()
    {
        var vm = new RulesViewModel(_app.Session, _app.Dialogs);
        vm.CategoryName = "費用";
        await vm.AddCategoryCommand.ExecuteAsync(null);
        foreach (var name in new[] { "第一", "第二" })
        {
            vm.NewRuleCommand.Execute(null);
            vm.RuleName = name;
            vm.Contains = "費";
            vm.RuleCategory = vm.Categories.Single(c => c.Name == "費用");
            await vm.SaveRuleCommand.ExecuteAsync(null);
        }

        vm.SelectedRule = vm.Rules.Single(r => r.Name == "第二");
        await vm.MoveUpCommand.ExecuteAsync(null);

        Assert.Equal(["第二", "第一"], vm.Rules.Select(r => r.Name));
    }

    [Fact]
    public async Task Rules_InvalidInputIsReported()
    {
        var vm = new RulesViewModel(_app.Session, _app.Dialogs);
        vm.NewRuleCommand.Execute(null);
        vm.RuleName = "壞";
        vm.Regex = "([";
        vm.RuleCategory = vm.Categories.First();
        await vm.SaveRuleCommand.ExecuteAsync(null);
        Assert.Contains("正規表達式", _app.Dialogs.Errors.Single().Message);
    }

    [Fact]
    public async Task Categories_SystemCategoryCannotBeDeleted()
    {
        var vm = new RulesViewModel(_app.Session, _app.Dialogs);
        vm.SelectedCategory = vm.Categories.Single(c => c.IsSystem);
        await vm.DeleteCategoryCommand.ExecuteAsync(null);
        Assert.Contains("系統科目", _app.Dialogs.Errors.Single().Message);
    }

    // ---------- periods ----------

    [Fact]
    public async Task Close_ThenReopenRequiresReason()
    {
        var vm = new PeriodsViewModel(_app.Session, _app.Dialogs);
        vm.SelectedMonth = vm.Months.Single(m => m.Month == TestApp.March);

        await vm.CloseCommand.ExecuteAsync(null);
        Assert.True(_app.Session.IsCurrentMonthClosed);
        Assert.True(vm.Months.Single(m => m.Month == TestApp.March).IsClosed);

        _app.Dialogs.PromptAnswer = "   ";
        await vm.ReopenCommand.ExecuteAsync(null);
        Assert.Contains("理由", _app.Dialogs.Errors.Single().Message);
        Assert.True(_app.Session.IsCurrentMonthClosed);

        _app.Dialogs.PromptAnswer = null; // cancelled
        await vm.ReopenCommand.ExecuteAsync(null);
        Assert.True(_app.Session.IsCurrentMonthClosed);

        _app.Dialogs.PromptAnswer = "更正房租歸類";
        await vm.ReopenCommand.ExecuteAsync(null);
        Assert.False(_app.Session.IsCurrentMonthClosed);
        Assert.Contains(vm.Events, e => e.Contains("重新開帳 2026-03：更正房租歸類"));
    }

    [Fact]
    public async Task Close_RequiresConfirmation()
    {
        _app.Dialogs.ConfirmAnswer = false;
        var vm = new PeriodsViewModel(_app.Session, _app.Dialogs);
        await vm.CloseCommand.ExecuteAsync(null);
        Assert.False(_app.Session.IsCurrentMonthClosed);
    }

    [Fact]
    public async Task PendingQueue_PostSelected()
    {
        _app.Session.Periods.Close(new YearMonth(2026, 2));
        _app.Session.Statements.Import(_account.Id, Core.Tests.Importing.TestProfiles.Utf8(Header + "2026/02/27,晚到,500,F1,\n"), "late.csv");
        var vm = new PeriodsViewModel(_app.Session, _app.Dialogs);
        var item = Assert.Single(vm.Pending);
        Assert.Contains("晚到", item.Text);

        item.IsSelected = true;
        await vm.PostSelectedCommand.ExecuteAsync(null);

        Assert.Empty(vm.Pending);
        var posted = Txn("F1").Value;
        Assert.Equal((new DateOnly(2026, 2, 27), TestApp.March), (posted.TxnDate, posted.PostingPeriod));
    }

    [Fact]
    public async Task PendingQueue_NothingSelected_Explains()
    {
        var vm = new PeriodsViewModel(_app.Session, _app.Dialogs);
        await vm.DiscardSelectedCommand.ExecuteAsync(null);
        Assert.Contains("勾選", _app.Dialogs.Errors.Single().Message);
    }

    // ---------- reconciliation ----------

    [Fact]
    public void Reconciliation_ScreenShowsTheSameLinesAsExports()
    {
        var vm = new ReconciliationViewModel(_app.Session, _app.Dialogs, _app.Files, _app.Files);
        vm.Generate();

        Assert.NotNull(vm.Report);
        Assert.Equal(ReconciliationLayout.Lines(vm.Report!).Count, vm.Lines.Count);
        Assert.StartsWith("已平衡", vm.Summary);
        Assert.Contains(vm.Lines, l => l.Reference == "R-1");
    }

    [Fact]
    public async Task Reconciliation_ExportsCsvWithBomAndHtml()
    {
        var vm = new ReconciliationViewModel(_app.Session, _app.Dialogs, _app.Files, _app.Files);
        vm.Generate();
        _app.Files.NextSavePath = "out.csv";
        await vm.ExportCsvCommand.ExecuteAsync(null);
        _app.Files.NextSavePath = "out.html";
        await vm.ExportHtmlCommand.ExecuteAsync(null);

        Assert.Equal([0xEF, 0xBB, 0xBF], _app.Files.Files["out.csv"].Take(3));
        var html = Encoding.UTF8.GetString(_app.Files.Files["out.html"]);
        Assert.Contains("A4 portrait", html);
        Assert.Equal(["out.html"], _app.Files.Opened);
        foreach (var line in vm.Lines.Where(l => l.Amount.Length > 0))
        {
            Assert.Contains(line.Amount, html);
        }
    }

    [Fact]
    public async Task Reconciliation_StatementBalanceMismatchShowsDifference()
    {
        var vm = new ReconciliationViewModel(_app.Session, _app.Dialogs, _app.Files, _app.Files);
        vm.Generate();
        vm.StatementBalance = "1";
        await vm.SaveStatementBalanceCommand.ExecuteAsync(null);

        Assert.StartsWith("未平衡", vm.Summary);
        Assert.Contains(vm.Lines, l => l.Kind == ReconciliationLineKind.Note);
    }

    [Fact]
    public async Task Reconciliation_BookEntry_AddAndClearByReference()
    {
        var vm = new ReconciliationViewModel(_app.Session, _app.Dialogs, _app.Files, _app.Files);
        vm.Generate();
        vm.EntryDate = "2026-03-03";
        vm.EntryAmount = "-20000";
        vm.EntryDescription = "支票 #9 房租";
        await vm.AddBookEntryCommand.ExecuteAsync(null);
        Assert.Contains(vm.Lines, l => l.Label.Contains("支票 #9"));

        vm.SelectedEntry = vm.BookEntries.Single();
        vm.ClearingReference = "T4";
        await vm.ClearEntryCommand.ExecuteAsync(null);

        Assert.Contains("已兌現", vm.BookEntries.Single().Text);
        Assert.StartsWith("已平衡", vm.Summary);
    }

    // ---------- audit & settings ----------

    [Fact]
    public async Task AuditLog_LoadsAndVerifies_AndOffersNoEditing()
    {
        var vm = new AuditLogViewModel(_app.Session, _app.Dialogs);
        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Items);

        await vm.VerifyCommand.ExecuteAsync(null);
        Assert.Contains("驗證通過", vm.StatusMessage);

        var commands = typeof(AuditLogViewModel).GetProperties().Where(p => p.Name.EndsWith("Command", StringComparison.Ordinal)).Select(p => p.Name).Order();
        Assert.Equal(["LoadMoreCommand", "ReloadCommand", "VerifyCommand"], commands);
    }

    [Fact]
    public async Task Settings_SaveAndDisableFeeCaps()
    {
        var vm = new SettingsViewModel(_app.Session, _app.Dialogs);
        Assert.Equal("30.00", vm.FixedFeeCap);
        Assert.Equal("3", vm.RateFeeCapPercent);

        vm.ExactDateToleranceDays = 5;
        vm.UseFixedFeeCap = false;
        vm.RateFeeCapPercent = "2.5";
        await vm.SaveCommand.ExecuteAsync(null);

        var saved = _app.Session.Settings.GetMatchingSettings();
        Assert.Equal((5, (Money?)null, (decimal?)0.025m), (saved.ExactDateToleranceDays, saved.FeeFixedCap, saved.FeeRateCap));

        vm.MaxComboSize = 99;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("組合大小", _app.Dialogs.Errors.Single().Message);

        vm.ResetToDefaultsCommand.Execute(null);
        Assert.Equal(MatchingSettings.Default.MaxComboSize, vm.MaxComboSize);
    }
}
