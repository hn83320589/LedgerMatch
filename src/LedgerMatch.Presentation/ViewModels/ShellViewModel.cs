using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

/// <summary>Window-level state: account and month pickers, undo/redo, closed-month banner.</summary>
public sealed partial class ShellViewModel : PageViewModel
{
    private bool _refreshing;

    public ShellViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
        session.DataChanged += (_, _) => Refresh();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LedgerSession.IsCurrentMonthClosed) or nameof(LedgerSession.CurrentMonth))
            {
                OnPropertyChanged(nameof(MonthStatus));
                OnPropertyChanged(nameof(IsCurrentMonthClosed));
            }
        };
        Refresh();
    }

    public ObservableCollection<Account> Accounts { get; } = [];

    public ObservableCollection<YearMonth> Months { get; } = [];

    [ObservableProperty]
    private Account? selectedAccount;

    [ObservableProperty]
    private YearMonth selectedMonth;

    public bool IsCurrentMonthClosed => Session.IsCurrentMonthClosed;

    public string MonthStatus => Session.IsCurrentMonthClosed ? $"{Session.CurrentMonth} 已結帳（唯讀）" : $"{Session.CurrentMonth} 未結帳";

    public string UndoDescription => Session.History.NextUndoOperation is { } op ? $"復原：{op}" : "沒有可以復原的操作";

    public string RedoDescription => Session.History.NextRedoOperation is { } op ? $"重做：{op}" : "沒有可以重做的操作";

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            Sync(Accounts, Session.Queries.GetAccounts());
            Sync(Months, Session.SelectableMonths());
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == Session.CurrentAccount?.Id) ?? Accounts.FirstOrDefault();
            SelectedMonth = Session.CurrentMonth;
        }
        finally
        {
            _refreshing = false;
        }

        Session.CurrentAccount = SelectedAccount;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
        OnPropertyChanged(nameof(MonthStatus));
        OnPropertyChanged(nameof(IsCurrentMonthClosed));
    }

    partial void OnSelectedAccountChanged(Account? value)
    {
        if (!_refreshing)
        {
            Session.CurrentAccount = value;
            Session.NotifyDataChanged();
        }
    }

    partial void OnSelectedMonthChanged(YearMonth value)
    {
        if (!_refreshing && value != default)
        {
            Session.CurrentMonth = value;
            Session.NotifyDataChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task Undo() => Run(() => Session.History.Undo(), "無法復原");

    private bool CanUndo() => Session.History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task Redo() => Run(() => Session.History.Redo(), "無法重做");

    private bool CanRedo() => Session.History.CanRedo;

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source))
        {
            return;
        }

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
