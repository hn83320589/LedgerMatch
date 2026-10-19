using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed record MonthItem(YearMonth Month, bool IsClosed, DateTimeOffset? ClosedAt)
{
    public string Status => IsClosed ? $"已結帳（{ClosedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}）" : "未結帳";
}

public sealed partial class PendingItemViewModel(PendingItem item, string accountName) : ObservableObject
{
    public PendingItem Item { get; } = item;

    public string Text => $"{accountName}　{Formatting.Date(Item.TxnDate)} {Item.Description} {Formatting.Amount(Item.Amount)}";

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>Month closing/reopening (reason required) and the pending queue of late transactions.</summary>
public sealed partial class PeriodsViewModel : PageViewModel
{
    public PeriodsViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
        session.DataChanged += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<MonthItem> Months { get; } = [];

    public ObservableCollection<PendingItemViewModel> Pending { get; } = [];

    public ObservableCollection<string> Events { get; } = [];

    [ObservableProperty]
    private MonthItem? selectedMonth;

    public void Refresh()
    {
        var selected = SelectedMonth?.Month;
        var periods = Session.Queries.GetPeriods().ToDictionary(p => p.Month);
        Months.Clear();
        foreach (var month in Session.SelectableMonths())
        {
            periods.TryGetValue(month, out var p);
            Months.Add(new MonthItem(month, p?.IsClosed ?? false, p?.ClosedAt));
        }

        SelectedMonth = Months.FirstOrDefault(m => m.Month == selected) ?? Months.FirstOrDefault(m => m.Month == Session.CurrentMonth);

        var accounts = Session.Queries.GetAccounts().ToDictionary(a => a.Id, a => a.Name);
        Pending.Clear();
        foreach (var item in Session.Queries.GetPendingItems(PendingStatus.Pending))
        {
            Pending.Add(new PendingItemViewModel(item, accounts.GetValueOrDefault(item.AccountId, "?")));
        }

        Events.Clear();
        foreach (var e in Session.Queries.GetPeriodEvents().Reverse())
        {
            var at = e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Events.Add(e.Action == PeriodAction.Close ? $"{at} 結帳 {e.Month}" : $"{at} 重新開帳 {e.Month}：{e.Reason}");
        }
    }

    [RelayCommand]
    private async Task Close()
    {
        var month = SelectedMonth?.Month ?? Session.CurrentMonth;
        if (await Dialogs.ConfirmAsync("結帳", $"結帳後 {month} 的交易、配對、拆分與歸類都不能再修改，確定結帳？"))
        {
            await Run(() =>
            {
                Session.Periods.Close(month);
                Session.NotifyDataChanged();
                StatusMessage = $"{month} 已結帳。";
            }, "無法結帳");
        }
    }

    [RelayCommand]
    private async Task Reopen()
    {
        var month = SelectedMonth?.Month ?? Session.CurrentMonth;
        var reason = await Dialogs.PromptAsync("重新開帳", $"請輸入重新開帳 {month} 的理由（會記入稽核紀錄）：", "例如：會計師要求更正歸類");
        if (reason is null)
        {
            return;
        }

        await Run(() =>
        {
            Session.Periods.Reopen(month, reason);
            Session.NotifyDataChanged();
            StatusMessage = $"{month} 已重新開帳。";
        }, "無法重新開帳");
    }

    [RelayCommand]
    private Task PostSelected() => Run(() =>
    {
        var posted = Session.Periods.PostToNextOpenMonth(SelectedPending());
        StatusMessage = $"已將 {posted.Count} 筆改列入下一個未結帳月份（保留原日期）。";
    }, "無法處理待處理項目");

    [RelayCommand]
    private Task DiscardSelected() => Run(() =>
    {
        var ids = SelectedPending();
        Session.Periods.Discard(ids);
        StatusMessage = $"已捨棄 {ids.Count} 筆。";
    }, "無法處理待處理項目");

    private List<long> SelectedPending()
    {
        var ids = Pending.Where(p => p.IsSelected).Select(p => p.Item.Id).ToList();
        return ids.Count > 0 ? ids : throw new LedgerException("請先勾選待處理項目。");
    }
}
