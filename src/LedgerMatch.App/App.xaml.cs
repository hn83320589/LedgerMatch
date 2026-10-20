using LedgerMatch.App.Services;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Data;
using LedgerMatch.Presentation;
using Microsoft.UI.Xaml;

namespace LedgerMatch.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Last resort: keep the app alive and tell the user instead of disappearing.
            e.Handled = true;
            _ = (_window as MainWindow)?.ShowFatalAsync(e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var path = Environment.GetEnvironmentVariable("LEDGERMATCH_DB") is { Length: > 0 } custom ? custom : LedgerDatabase.DefaultPath;
        try
        {
            var database = LedgerDatabase.Open(path);
            var store = new SqliteLedgerStore(database);
            var session = new LedgerSession(store, store, TimeProvider.System);
            var window = new MainWindow();
            window.Initialize(session, new FileService());
            _window = window;
        }
        catch (DatabaseOpenException ex)
        {
            _window = new StartupErrorWindow(ex.Failure == DatabaseOpenFailure.TooNew ? "資料庫版本過新" : "資料庫無法開啟", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _window = new StartupErrorWindow("資料庫無法開啟", $"無法存取「{path}」：{ex.Message}");
        }

        _window.Activate();
    }
}
