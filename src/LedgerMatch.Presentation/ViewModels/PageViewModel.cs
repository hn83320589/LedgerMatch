using CommunityToolkit.Mvvm.ComponentModel;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

/// <summary>
/// Base for pages: busy flag, status line, and a single place that turns business errors into user messages.
/// Unexpected exceptions are shown too (with their type) so they are never silently lost.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(LedgerSession session, IDialogService dialogs)
    {
        Session = session;
        Dialogs = dialogs;
    }

    protected LedgerSession Session { get; }

    protected IDialogService Dialogs { get; }

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    /// <summary>Last error shown to the user; kept for tests and for the status bar.</summary>
    [ObservableProperty]
    private string? lastError;

    protected async Task<bool> RunAsync(Func<Task> action, string errorTitle = "無法完成操作")
    {
        try
        {
            IsBusy = true;
            LastError = null;
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消，資料未變更。";
            return false;
        }
        catch (Exception ex) when (ex is LedgerException or ImportFileException)
        {
            LastError = ex.Message;
            await Dialogs.ShowErrorAsync(errorTitle, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            await Dialogs.ShowErrorAsync("發生未預期的錯誤", LastError);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected Task<bool> Run(Action action, string errorTitle = "無法完成操作") =>
        RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }, errorTitle);
}
