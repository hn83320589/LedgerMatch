using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed record ReconciliationLineItem(ReconciliationLine Line)
{
    public ReconciliationLineKind Kind => Line.Kind;

    public string Date => Line.Item is { } i ? Formatting.Date(i.Date) : string.Empty;

    public string Reference => Line.Item?.Reference ?? string.Empty;

    public string Label => Line.Item is { Note: { } note } ? $"{Line.Label}（{note}）" : Line.Label;

    public string Amount => Line.Amount is { } a ? Formatting.Amount(a) : string.Empty;

    public bool IsItem => Kind == ReconciliationLineKind.Item;
}

public sealed record BookEntryItem(BookEntry Entry)
{
    public string Text =>
        $"{Formatting.Date(Entry.EntryDate)} {Entry.Description} {Formatting.Amount(Entry.Amount)}" + (Entry.ClearedTxnId is null ? "（未兌現）" : "（已兌現）");
}

/// <summary>
/// The reconciliation for the selected account and month. Screen, CSV and HTML all come from the same
/// <see cref="ReconciliationLayout"/> lines.
/// </summary>
public sealed partial class ReconciliationViewModel : PageViewModel
{
    private readonly IFilePickerService _files;
    private readonly IFileService _fileSystem;

    public ReconciliationViewModel(LedgerSession session, IDialogService dialogs, IFilePickerService files, IFileService fileSystem)
        : base(session, dialogs)
    {
        _files = files;
        _fileSystem = fileSystem;
        session.DataChanged += (_, _) => Generate();
    }

    public ObservableCollection<ReconciliationLineItem> Lines { get; } = [];

    public ObservableCollection<BookEntryItem> BookEntries { get; } = [];

    [ObservableProperty]
    private ReconciliationReport? report;

    [ObservableProperty]
    private string statementBalance = string.Empty;

    [ObservableProperty]
    private string entryDate = string.Empty;

    [ObservableProperty]
    private string entryAmount = string.Empty;

    [ObservableProperty]
    private string entryDescription = string.Empty;

    [ObservableProperty]
    private BookEntryItem? selectedEntry;

    /// <summary>Bank line id typed or picked by the user to clear the selected entry.</summary>
    [ObservableProperty]
    private string clearingReference = string.Empty;

    public string Summary => Report is null
        ? "請選擇帳戶與月份"
        : Report.IsBalanced
            ? $"已平衡：調整後銀行餘額 = 調整後帳面餘額 = {Formatting.Amount(Report.AdjustedBankBalance)}"
            : $"未平衡：差異 {Formatting.Amount(Report.Difference)}";

    partial void OnReportChanged(ReconciliationReport? value) => OnPropertyChanged(nameof(Summary));

    [RelayCommand]
    public void Generate()
    {
        Lines.Clear();
        BookEntries.Clear();
        if (Session.CurrentAccount is not { } account)
        {
            Report = null;
            return;
        }

        _ = Run(() =>
        {
            Report = Session.Reconciliation.Generate(account.Id, Session.CurrentMonth);
            foreach (var line in ReconciliationLayout.Lines(Report))
            {
                Lines.Add(new ReconciliationLineItem(line));
            }

            foreach (var entry in Session.Queries.GetBookEntries(account.Id).Where(e => YearMonth.Of(e.EntryDate) <= Session.CurrentMonth))
            {
                BookEntries.Add(new BookEntryItem(entry));
            }

            StatementBalance = Report.StatementBalance?.ToPlainString() ?? string.Empty;
        }, "無法產生調節表");
    }

    [RelayCommand]
    private Task SaveStatementBalance() => Run(() =>
    {
        var account = Session.CurrentAccount ?? throw new LedgerException("請先選擇帳戶。");
        Money? value = null;
        if (!string.IsNullOrWhiteSpace(StatementBalance))
        {
            value = Formatting.TryParseMoney(StatementBalance, out var money, out var error) ? money : throw new LedgerException($"對帳單期末餘額：{error}");
        }

        Session.Books.SetStatementBalance(account.Id, Session.CurrentMonth, value);
    }, "無法儲存對帳單期末餘額");

    [RelayCommand]
    private Task AddBookEntry() => Run(() =>
    {
        var account = Session.CurrentAccount ?? throw new LedgerException("請先選擇帳戶。");
        if (!Formatting.TryParseDate(EntryDate, out var date))
        {
            throw new LedgerException("日期格式應為 yyyy-MM-dd。");
        }

        if (!Formatting.TryParseMoney(EntryAmount, out var amount, out var error))
        {
            throw new LedgerException($"金額：{error}（支出請輸入負數）");
        }

        Session.Books.AddEntry(account.Id, date, amount, EntryDescription);
        EntryAmount = EntryDescription = string.Empty;
    }, "無法新增帳面分錄");

    [RelayCommand]
    private Task ClearEntry() => Run(() =>
    {
        var entry = SelectedEntry?.Entry ?? throw new LedgerException("請先選擇帳面分錄。");
        var reference = ClearingReference.Trim();
        var txn = Session.Queries.GetTransactions(entry.AccountId)
            .FirstOrDefault(t => t.Serial == reference || t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == reference)
            ?? throw new LedgerException($"找不到交易序號或編號為「{reference}」的銀行交易。");
        Session.Books.ClearEntry(entry.Id, txn.Id);
    }, "無法兌現");

    [RelayCommand]
    private Task DeleteEntry() => Run(() =>
        Session.Books.DeleteEntry((SelectedEntry ?? throw new LedgerException("請先選擇帳面分錄。")).Entry.Id), "無法刪除帳面分錄");

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (Report is not { } report)
        {
            return;
        }

        var path = await _files.PickSaveFileAsync($"調節表_{report.AccountName}_{report.Month}.csv", new FileTypeFilter("CSV（UTF-8）", ".csv"));
        if (path is not null)
        {
            await Run(() =>
            {
                _fileSystem.WriteAllBytes(path, ReconciliationCsv.Write(report));
                StatusMessage = $"已匯出 {path}";
            }, "無法匯出 CSV");
        }
    }

    [RelayCommand]
    private async Task ExportHtml()
    {
        if (Report is not { } report)
        {
            return;
        }

        var path = await _files.PickSaveFileAsync($"調節表_{report.AccountName}_{report.Month}.html", new FileTypeFilter("網頁（可列印）", ".html"));
        if (path is not null)
        {
            await Run(() =>
            {
                _fileSystem.WriteAllText(path, ReconciliationHtml.Render(report));
                _fileSystem.OpenWithShell(path);
                StatusMessage = $"已匯出 {path}，可在瀏覽器列印為 A4。";
            }, "無法匯出 HTML");
        }
    }
}
