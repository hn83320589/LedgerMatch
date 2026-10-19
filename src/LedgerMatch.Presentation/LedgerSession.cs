using CommunityToolkit.Mvvm.ComponentModel;
using LedgerMatch.Core;
using LedgerMatch.Core.Books;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Periods;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Receivables;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Core.Setup;

namespace LedgerMatch.Presentation;

/// <summary>
/// The application services for one open database plus the shared selection (account and month).
/// Pages listen to <see cref="DataChanged"/> to refresh after any write, including undo/redo.
/// </summary>
public sealed partial class LedgerSession : ObservableObject
{
    private readonly SynchronizationContext? _uiContext;

    public LedgerSession(ILedgerStore store, ILedgerQueries queries, TimeProvider time)
    {
        Store = store;
        Queries = queries;
        Time = time;
        Executor = new LedgerCommandExecutor(store, time);
        History = new UndoRedoService(Executor);
        Setup = new SetupService(store, queries, History);
        Settings = new SettingsService(store, queries, History);
        Statements = new StatementImportService(store, queries, Executor, time);
        Receipts = new ReceiptService(store, queries, Executor, History, time);
        Matching = new MatchingService(store, queries, History, Settings);
        Categories = new CategoryService(store, queries, History);
        Classification = new ClassificationService(store, queries, History);
        Rules = new RuleService(store, queries, History);
        Periods = new PeriodService(store, queries, Executor, History, time);
        Books = new BookService(store, queries, History);
        Reconciliation = new ReconciliationService(queries, time);
        // Background work (matching, imports) commits on worker threads; listeners update UI-bound collections.
        _uiContext = SynchronizationContext.Current;
        History.Changed += (_, _) => OnUiThread(NotifyDataChanged);
        // Start on the newest month that has data; a fresh database starts on the current month.
        var active = queries.GetActiveMonths();
        currentMonth = active.Count > 0 ? active[^1] : YearMonth.Of(DateOnly.FromDateTime(time.GetLocalNow().DateTime));
    }

    public event EventHandler? DataChanged;

    public ILedgerStore Store { get; }

    public ILedgerQueries Queries { get; }

    public TimeProvider Time { get; }

    public LedgerCommandExecutor Executor { get; }

    public UndoRedoService History { get; }

    public SetupService Setup { get; }

    public SettingsService Settings { get; }

    public StatementImportService Statements { get; }

    public ReceiptService Receipts { get; }

    public MatchingService Matching { get; }

    public CategoryService Categories { get; }

    public ClassificationService Classification { get; }

    public RuleService Rules { get; }

    public PeriodService Periods { get; }

    public BookService Books { get; }

    public ReconciliationService Reconciliation { get; }

    [ObservableProperty]
    private Account? currentAccount;

    [ObservableProperty]
    private YearMonth currentMonth;

    public bool IsCurrentMonthClosed => Store.GetClosedPeriods().Contains(CurrentMonth);

    /// <summary>Tells every page the data changed (writes that are not undo steps call this explicitly).</summary>
    public void NotifyDataChanged()
    {
        OnPropertyChanged(nameof(IsCurrentMonthClosed));
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnUiThread(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    partial void OnCurrentMonthChanged(YearMonth value) => OnPropertyChanged(nameof(IsCurrentMonthClosed));

    /// <summary>The months offered in pickers: months with data plus the current calendar month, newest first.</summary>
    public IReadOnlyList<YearMonth> SelectableMonths()
    {
        var today = YearMonth.Of(DateOnly.FromDateTime(Time.GetLocalNow().DateTime));
        return Queries.GetActiveMonths().Append(today).Append(CurrentMonth).Distinct().OrderDescending().ToList();
    }
}
