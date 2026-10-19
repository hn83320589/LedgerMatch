using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed partial class MatchItem : ObservableObject
{
    public MatchItem(Match match, BankTransaction deposit, IReadOnlyList<Receipt> receipts)
    {
        Match = match;
        Deposit = deposit;
        Receipts = receipts;
    }

    public Match Match { get; }

    public BankTransaction Deposit { get; }

    public IReadOnlyList<Receipt> Receipts { get; }

    [ObservableProperty]
    private bool isSelected;

    public string DepositText => $"{Formatting.Date(Deposit.TxnDate)} {Deposit.Description} {Formatting.Amount(Deposit.Amount)}";

    public string ReceiptsText => string.Join("、", Receipts.Select(r => $"{r.ReceiptNo} {Formatting.Amount(r.Amount)}"));

    public string Kind => Formatting.MatchKind(Match.Kind);

    public string Confidence => Formatting.Confidence(Match.Confidence);

    public bool IsSuggestion => Match.Confidence == MatchConfidence.Suggested;

    public string Fee => Match.Fee == Money.Zero ? string.Empty : $"手續費 {Formatting.Amount(Match.Fee)}";

    public string Note => Match.Note ?? string.Empty;
}

public sealed partial class SelectableTransaction(BankTransaction transaction) : ObservableObject
{
    public BankTransaction Transaction { get; } = transaction;

    public string Text => $"{Formatting.Date(Transaction.TxnDate)} {Transaction.Description} {Formatting.Amount(Transaction.Amount)}";

    [ObservableProperty]
    private bool isSelected;
}

public sealed partial class SelectableReceipt(Receipt receipt) : ObservableObject
{
    public Receipt Receipt { get; } = receipt;

    public string Text => $"{Formatting.Date(Receipt.IssueDate)} {Receipt.ReceiptNo} {Receipt.Customer} {Formatting.Amount(Receipt.Amount)}";

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>Matching workbench: run the engine in the background (cancellable), review and confirm, or match by hand.</summary>
public sealed partial class MatchingViewModel : PageViewModel
{
    private CancellationTokenSource? _cancellation;

    public MatchingViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
        session.DataChanged += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<MatchItem> Matches { get; } = [];

    public ObservableCollection<SelectableTransaction> UnmatchedDeposits { get; } = [];

    public ObservableCollection<SelectableReceipt> UnmatchedReceipts { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(RunCommand))]
    private bool isRunning;

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private string progressText = string.Empty;

    [ObservableProperty]
    private bool acceptDifferenceAsFee;

    [ObservableProperty]
    private string manualSummary = string.Empty;

    public bool IsEditable => !Session.IsCurrentMonthClosed;

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsEditable));
        Matches.Clear();
        UnmatchedDeposits.Clear();
        UnmatchedReceipts.Clear();
        if (Session.CurrentAccount is not { } account)
        {
            return;
        }

        var month = Session.CurrentMonth;
        foreach (var m in Session.Queries.GetMatches(account.Id, month))
        {
            var deposit = Session.Queries.GetTransaction(m.DepositTxnId)!;
            var receipts = Session.Queries.GetMatchLinks(m.Id).Select(l => Session.Queries.GetReceipt(l.ReceiptId)!).ToList();
            Matches.Add(new MatchItem(m, deposit, receipts));
        }

        foreach (var d in Session.Queries.GetUnmatchedDeposits(account.Id, month))
        {
            var item = new SelectableTransaction(d);
            item.PropertyChanged += (_, _) => UpdateManualSummary();
            UnmatchedDeposits.Add(item);
        }

        foreach (var r in Session.Queries.GetUnmatchedReceipts(account.Id, DateOnly.MinValue, month.LastDay.AddDays(31)))
        {
            var item = new SelectableReceipt(r);
            item.PropertyChanged += (_, _) => UpdateManualSummary();
            UnmatchedReceipts.Add(item);
        }

        var suggested = Matches.Count(m => m.IsSuggestion);
        StatusMessage = $"已配對 {Matches.Count - suggested}、建議 {suggested}、未配對入帳 {UnmatchedDeposits.Count}、未配對收款單 {UnmatchedReceipts.Count}";
        UpdateManualSummary();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        var account = Session.CurrentAccount;
        if (account is null)
        {
            await Dialogs.ShowErrorAsync("無法配對", "請先在上方選擇帳戶。");
            return;
        }

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        Warnings.Clear();
        try
        {
            await RunAsync(async () =>
            {
                var progress = new Progress<MatchingProgress>(p =>
                {
                    ProgressPercent = p.Total == 0 ? 0 : (((int)p.Stage - 1) * 100.0 / 3) + (p.Processed * 100.0 / 3 / p.Total);
                    ProgressText = $"第 {(int)p.Stage} 階段（{StageName(p.Stage)}）{p.Processed}/{p.Total}";
                });
                var summary = await Session.Matching.RunAsync(account.Id, Session.CurrentMonth, progress, _cancellation.Token);
                foreach (var w in summary.Warnings)
                {
                    var deposit = Session.Queries.GetTransaction(w.DepositId);
                    Warnings.Add($"{(deposit is null ? string.Empty : Formatting.Date(deposit.TxnDate) + " " + Formatting.Amount(deposit.Amount) + "：")}{w.Message}");
                }

                StatusMessage = $"自動確認 {summary.Confirmed} 筆，建議 {summary.Suggested} 筆，請檢查後確認。";
            }, "配對失敗");
        }
        finally
        {
            IsRunning = false;
            ProgressPercent = 0;
            ProgressText = string.Empty;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private Task ConfirmSelected() => Run(() => Session.Matching.Confirm(SelectedIds()), "無法確認");

    [RelayCommand]
    private Task ConfirmAllSuggestions() =>
        Run(() => Session.Matching.Confirm(Matches.Where(m => m.IsSuggestion).Select(m => m.Match.Id).ToList()), "無法確認");

    [RelayCommand]
    private Task RejectSelected() => Run(() => Session.Matching.Reject(SelectedIds()), "無法拒絕");

    [RelayCommand]
    private Task UnmatchSelected() => Run(() => Session.Matching.Unmatch(SelectedIds()), "無法解除配對");

    [RelayCommand]
    private Task ManualMatch() => Run(() =>
    {
        var deposits = UnmatchedDeposits.Where(d => d.IsSelected).ToList();
        if (deposits.Count != 1)
        {
            throw new LedgerException("人工配對請選一筆入帳。");
        }

        var receipts = UnmatchedReceipts.Where(r => r.IsSelected).Select(r => r.Receipt.Id).ToList();
        Session.Matching.ManualMatch(deposits[0].Transaction.Id, receipts, AcceptDifferenceAsFee);
        AcceptDifferenceAsFee = false;
    }, "無法人工配對");

    private List<long> SelectedIds()
    {
        var ids = Matches.Where(m => m.IsSelected).Select(m => m.Match.Id).ToList();
        return ids.Count > 0 ? ids : throw new LedgerException("請先勾選配對。");
    }

    private void UpdateManualSummary()
    {
        var deposit = UnmatchedDeposits.Where(d => d.IsSelected).Sum(d => d.Transaction.Amount);
        var receipts = UnmatchedReceipts.Where(r => r.IsSelected).Sum(r => r.Receipt.Amount);
        var diff = receipts - deposit;
        ManualSummary = $"入帳 {Formatting.Amount(deposit)}，收款單合計 {Formatting.Amount(receipts)}，差額 {Formatting.Amount(diff)}";
    }

    private static string StageName(MatchingStage stage) => stage switch
    {
        MatchingStage.Exact => "精確",
        MatchingStage.OneToMany => "一對多",
        _ => "含手續費",
    };
}
