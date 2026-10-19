using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed record ReceiptItem(Receipt Receipt, bool IsMatched)
{
    public string Date => Formatting.Date(Receipt.IssueDate);

    public string Amount => Formatting.Amount(Receipt.Amount);

    public string Status => IsMatched ? "已配對" : "未配對";
}

/// <summary>Receipts of the selected account: add, edit, delete (matched ones must be unmatched first).</summary>
public sealed partial class ReceiptsViewModel : PageViewModel
{
    public ReceiptsViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
        session.DataChanged += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<ReceiptItem> Items { get; } = [];

    public bool IsEditable => !Session.IsCurrentMonthClosed;

    [ObservableProperty]
    private bool onlyUnmatched;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private ReceiptItem? selected;

    [ObservableProperty]
    private string issueDate = string.Empty;

    [ObservableProperty]
    private string customer = string.Empty;

    [ObservableProperty]
    private string receiptNo = string.Empty;

    [ObservableProperty]
    private string amount = string.Empty;

    [ObservableProperty]
    private string note = string.Empty;

    partial void OnOnlyUnmatchedChanged(bool value) => Refresh();

    partial void OnSelectedChanged(ReceiptItem? value)
    {
        IssueDate = value is null ? string.Empty : value.Date;
        Customer = value?.Receipt.Customer ?? string.Empty;
        ReceiptNo = value?.Receipt.ReceiptNo ?? string.Empty;
        Amount = value?.Receipt.Amount.ToPlainString() ?? string.Empty;
        Note = value?.Receipt.Note ?? string.Empty;
    }

    public void Refresh()
    {
        var selectedId = Selected?.Receipt.Id;
        OnPropertyChanged(nameof(IsEditable));
        Items.Clear();
        if (Session.CurrentAccount is not { } account)
        {
            return;
        }

        var month = Session.CurrentMonth;
        foreach (var r in Session.Queries.GetReceipts(account.Id).Where(r => YearMonth.Of(r.IssueDate) == month))
        {
            var matched = Session.Queries.GetMatchForReceipt(r.Id) is not null;
            if (!OnlyUnmatched || !matched)
            {
                Items.Add(new ReceiptItem(r, matched));
            }
        }

        Selected = Items.FirstOrDefault(i => i.Receipt.Id == selectedId);
        StatusMessage = $"{Items.Count} 張收款單";
    }

    [RelayCommand]
    private void New() => Selected = null;

    [RelayCommand]
    private Task Save() => Run(() =>
    {
        var account = Session.CurrentAccount ?? throw new LedgerException("請先選擇帳戶。");
        if (!Formatting.TryParseDate(IssueDate, out var date))
        {
            throw new LedgerException("開立日期格式應為 yyyy-MM-dd。");
        }

        if (!Formatting.TryParseMoney(Amount, out var money, out var error))
        {
            throw new LedgerException($"金額：{error}");
        }

        var saved = Selected is null
            ? Session.Receipts.Add(account.Id, date, Customer, ReceiptNo, money, Note)
            : Session.Receipts.Update(Selected.Receipt with { IssueDate = date, Customer = Customer, ReceiptNo = ReceiptNo, Amount = money, Note = Note });
        StatusMessage = $"已儲存收款單 {saved.ReceiptNo}。";
    }, "無法儲存收款單");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Delete()
    {
        var receipt = Selected!.Receipt;
        if (await Dialogs.ConfirmAsync("刪除收款單", $"確定刪除收款單 {receipt.ReceiptNo}？"))
        {
            await Run(() => Session.Receipts.Delete(receipt.Id), "無法刪除收款單");
        }
    }

    private bool HasSelection() => Selected is not null;
}
