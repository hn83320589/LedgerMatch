using System.Text;
using LedgerMatch.Core.Tests.Importing;
using LedgerMatch.Presentation.ViewModels;

namespace LedgerMatch.Presentation.Tests;

public class ImportViewModelTests : IDisposable
{
    private const string Csv = "交易日期,摘要,金額,交易序號,備註\n2026/03/01,a,1,A,\n2026/3/2,bad,2,B,\n2026/03/03,c,3,C,\n";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private ImportViewModel NewViewModel() => new(_app.Session, _app.Dialogs, _app.Files, _app.Files);

    [Fact]
    public async Task ImportStatement_ShowsSummaryAndErrorLines_AndIsIdempotent()
    {
        _app.SetUpBankA();
        _app.Files.Files["a.csv"] = Encoding.UTF8.GetBytes(Csv);
        _app.Files.NextOpenPath = "a.csv";
        var vm = NewViewModel();
        var refreshed = 0;
        _app.Session.DataChanged += (_, _) => refreshed++;

        await vm.ImportStatementCommand.ExecuteAsync(null);

        Assert.Equal("新增 2 筆，判定為重複而略過 0 筆，錯誤 1 列。", vm.Summary);
        Assert.Equal(3, Assert.Single(vm.Errors).LineNumber);
        Assert.True(refreshed > 0);
        Assert.False(vm.IsImporting);

        await vm.ImportStatementCommand.ExecuteAsync(null);
        Assert.StartsWith("新增 0 筆，判定為重複而略過 2 筆", vm.Summary);
    }

    [Fact]
    public async Task ImportStatement_WithoutAccount_ShowsError()
    {
        _app.Files.Files["a.csv"] = Encoding.UTF8.GetBytes(Csv);
        var vm = NewViewModel();

        await vm.ImportStatementFileAsync("a.csv");

        Assert.Contains("選擇帳戶", _app.Dialogs.Errors.Single().Message);
    }

    [Fact]
    public async Task ImportStatement_WrongEncoding_ShowsFileError()
    {
        _app.SetUpBankA();
        _app.Files.Files["b.csv"] = TestProfiles.Big5("日期,摘要\n").ToArray();
        var vm = NewViewModel();

        await vm.ImportStatementFileAsync("b.csv");

        Assert.Contains("UTF-8", _app.Dialogs.Errors.Single().Message);
    }

    [Fact]
    public async Task ImportReceipts_UsesSelectedProfile()
    {
        _app.SetUpBankA();
        _app.Session.Setup.SaveProfile(TestProfiles.Receipts);
        _app.Files.Files["r.csv"] = Encoding.UTF8.GetBytes("開立日期,客戶名稱,單據編號,金額,備註\n2026-03-01,王,R-1,100,\n");
        var vm = NewViewModel();

        Assert.True(await vm.ImportReceiptsFileAsync("r.csv"));

        Assert.StartsWith("新增 1 筆", vm.Summary);
        Assert.Single(_app.Session.Queries.GetReceipts(_app.Session.CurrentAccount!.Id));
    }

    [Fact]
    public void Cancel_IsOnlyAvailableWhileImporting()
    {
        var vm = NewViewModel();
        Assert.False(vm.CancelCommand.CanExecute(null));
    }
}
