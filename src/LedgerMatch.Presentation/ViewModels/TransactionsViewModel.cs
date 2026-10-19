using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed class TransactionItem(TransactionRow row)
{
    public TransactionRow Row { get; } = row;

    public long Id => Row.Transaction.Id;

    public string Date => Formatting.Date(Row.Transaction.TxnDate);

    public string Description => Row.Transaction.Description;

    public string Amount => Formatting.Amount(Row.Transaction.Amount);

    public bool IsDeposit => Row.Transaction.IsDeposit;

    public string Reference => Row.Transaction.Serial ?? string.Empty;

    public string Category => Row.IsSplit ? "（已拆分）" : Row.CategoryName ?? string.Empty;

    public string Status => Row switch
    {
        { MatchConfidence: MatchConfidence.Confirmed } => "已配對",
        { MatchConfidence: MatchConfidence.Suggested } => "建議配對",
        { Transaction.BookConfirmed: true, Transaction.IsDeposit: true } => "直接入帳",
        { Transaction.IsDeposit: true } => "未配對",
        _ => "支出",
    };

    /// <summary>Shown when the original date lies in an earlier (closed) month than the posting month.</summary>
    public string PostingNote =>
        YearMonth.Of(Row.Transaction.TxnDate) == Row.Transaction.PostingPeriod ? string.Empty : $"列入 {Row.Transaction.PostingPeriod}";
}

/// <summary>
/// Transaction list for the selected account and month. Rows are loaded in keyset pages (<see cref="PageSize"/>)
/// as the virtualised list scrolls, so large ledgers stay responsive.
/// </summary>
public sealed partial class TransactionsViewModel : PageViewModel
{
    public const int PageSize = 200;

    private TransactionCursor? _cursor;
    private TransactionFilter _activeFilter = new();

    // Reloads can overlap (a write raises DataChanged while a reload is in flight); only the newest may fill the list.
    private int _loadVersion;

    public TransactionsViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
        Split = new SplitEditorViewModel(session, dialogs);
        session.DataChanged += (_, _) => _ = ReloadAsync();
        RefreshCategories();
    }

    public SplitEditorViewModel Split { get; }

    public ObservableCollection<TransactionItem> Items { get; } = [];

    public ObservableCollection<Category> Categories { get; } = [];

    /// <summary>Kept in sync by the view's multi-selection.</summary>
    public ObservableCollection<TransactionItem> SelectedItems { get; } = [];

    public IReadOnlyList<(TransactionStatusFilter Value, string Label)> StatusOptions { get; } =
    [
        (TransactionStatusFilter.All, "全部"),
        (TransactionStatusFilter.UnmatchedDeposits, "未配對入帳"),
        (TransactionStatusFilter.SuggestedMatches, "建議配對"),
        (TransactionStatusFilter.ConfirmedMatches, "已配對"),
        (TransactionStatusFilter.Withdrawals, "支出"),
        (TransactionStatusFilter.Unclassified, "未歸類"),
    ];

    [ObservableProperty]
    private TransactionStatusFilter status;

    [ObservableProperty]
    private string fromDate = string.Empty;

    [ObservableProperty]
    private string toDate = string.Empty;

    [ObservableProperty]
    private string minAmount = string.Empty;

    [ObservableProperty]
    private string maxAmount = string.Empty;

    [ObservableProperty]
    private string keyword = string.Empty;

    [ObservableProperty]
    private int totalCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool hasMore;

    [ObservableProperty]
    private Category? targetCategory;

    public bool IsEditable => !Session.IsCurrentMonthClosed;

    /// <summary>Index into <see cref="StatusOptions"/> for the status ComboBox.</summary>
    public int StatusIndex
    {
        get => (int)Status;
        set => Status = (TransactionStatusFilter)Math.Max(0, value);
    }

    partial void OnStatusChanged(TransactionStatusFilter value) => OnPropertyChanged(nameof(StatusIndex));

    [RelayCommand]
    public Task ReloadAsync() => RunAsync(async () =>
    {
        var version = ++_loadVersion;
        RefreshCategories();
        OnPropertyChanged(nameof(IsEditable));
        Items.Clear();
        SelectedItems.Clear();
        _cursor = null;
        HasMore = false;
        if (Session.CurrentAccount is null)
        {
            TotalCount = 0;
            return;
        }

        var filter = BuildFilter();
        var count = await Task.Run(() => Session.Queries.CountTransactions(filter));
        if (version != _loadVersion)
        {
            return;
        }

        _activeFilter = filter;
        TotalCount = count;
        await LoadPageAsync();
    }, "無法載入交易");

    [RelayCommand(CanExecute = nameof(HasMore))]
    private Task LoadMore() => RunAsync(LoadPageAsync, "無法載入交易");

    private async Task LoadPageAsync()
    {
        var version = _loadVersion;
        var filter = _activeFilter;
        var cursor = _cursor;
        var page = await Task.Run(() => Session.Queries.FindTransactions(filter, cursor, PageSize));
        if (version != _loadVersion)
        {
            return;
        }

        foreach (var row in page)
        {
            Items.Add(new TransactionItem(row));
        }

        if (page.Count > 0)
        {
            _cursor = new TransactionCursor(page[^1].Transaction.TxnDate, page[^1].Transaction.Id);
        }

        HasMore = page.Count == PageSize;
        StatusMessage = $"顯示 {Items.Count} / {TotalCount} 筆";
    }

    private TransactionFilter BuildFilter()
    {
        var filter = new TransactionFilter
        {
            AccountId = Session.CurrentAccount?.Id,
            Period = Session.CurrentMonth,
            Status = Status,
            Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword,
        };

        if (Formatting.TryParseDate(FromDate, out var from))
        {
            filter = filter with { From = from };
        }

        if (Formatting.TryParseDate(ToDate, out var to))
        {
            filter = filter with { To = to };
        }

        if (!string.IsNullOrWhiteSpace(MinAmount) && Formatting.TryParseMoney(MinAmount, out var min, out _))
        {
            filter = filter with { MinAmount = min };
        }

        if (!string.IsNullOrWhiteSpace(MaxAmount) && Formatting.TryParseMoney(MaxAmount, out var max, out _))
        {
            filter = filter with { MaxAmount = max };
        }

        return filter;
    }

    [RelayCommand]
    private Task ClearFilters()
    {
        Status = TransactionStatusFilter.All;
        FromDate = ToDate = MinAmount = MaxAmount = Keyword = string.Empty;
        return ReloadAsync();
    }

    [RelayCommand]
    private Task ApplyCategory() => Run(() =>
    {
        RequireSelection();
        var count = Session.Classification.SetCategory(SelectedItems.Select(i => i.Id).ToList(), TargetCategory?.Id);
        StatusMessage = $"已歸類 {count} 筆。";
    }, "無法歸類");

    [RelayCommand]
    private Task BookDeposits() => Run(() =>
    {
        RequireSelection();
        var count = Session.Books.SetDepositsBooked(SelectedItems.Select(i => i.Id).ToList(), booked: true);
        StatusMessage = $"已將 {count} 筆入帳直接列入帳面。";
    }, "無法列入帳面");

    [RelayCommand]
    private Task EditSplit() => Run(() =>
    {
        RequireSelection();
        if (SelectedItems.Count != 1)
        {
            throw new LedgerException("拆分一次只能選一筆交易。");
        }

        Split.Load(SelectedItems[0].Row.Transaction);
    }, "無法拆分");

    private void RequireSelection()
    {
        if (SelectedItems.Count == 0)
        {
            throw new LedgerException("請先選取交易。");
        }
    }

    private void RefreshCategories()
    {
        var selected = TargetCategory?.Id;
        Categories.Clear();
        foreach (var c in Session.Queries.GetCategories())
        {
            Categories.Add(c);
        }

        TargetCategory = Categories.FirstOrDefault(c => c.Id == selected);
        Split.RefreshCategories(Categories);
    }
}
