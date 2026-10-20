using LedgerMatch.Presentation;
using LedgerMatch.Presentation.Services;
using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LedgerMatch.App.Views;

/// <summary>Top bar (account, month, undo/redo) and navigation. Pages are created once and keep their state.</summary>
public sealed partial class ShellView : UserControl
{
    private readonly Dictionary<string, Func<UIElement>> _factories;
    private readonly Dictionary<string, UIElement> _pages = [];

    public ShellView(LedgerSession session, IDialogService dialogs, IFilePickerService pickers, IFileService files)
    {
        Shell = new ShellViewModel(session, dialogs);
        _factories = new()
        {
            ["accounts"] = () => new AccountsPage(new AccountsViewModel(session, dialogs, pickers, files)),
            ["import"] = () => new ImportPage(new ImportViewModel(session, dialogs, pickers, files)),
            ["transactions"] = () => new TransactionsPage(new TransactionsViewModel(session, dialogs)),
            ["receipts"] = () => new ReceiptsPage(new ReceiptsViewModel(session, dialogs)),
            ["matching"] = () => new MatchingPage(new MatchingViewModel(session, dialogs)),
            ["rules"] = () => new RulesPage(new RulesViewModel(session, dialogs)),
            ["reconciliation"] = () => new ReconciliationPage(new ReconciliationViewModel(session, dialogs, pickers, files)),
            ["periods"] = () => new PeriodsPage(new PeriodsViewModel(session, dialogs)),
            ["audit"] = () => new AuditPage(new AuditLogViewModel(session, dialogs)),
            ["settings"] = () => new SettingsPage(new SettingsViewModel(session, dialogs)),
        };
        InitializeComponent();
        Nav.SelectedItem = Nav.MenuItems[Shell.Accounts.Count == 0 ? 0 : 2];
    }

    public ShellViewModel Shell { get; }

    private void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            if (!_pages.TryGetValue(tag, out var page))
            {
                page = _factories[tag]();
                _pages[tag] = page;
            }

            PageHost.Content = page;
        }
    }

    private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Let text boxes keep their own Ctrl+Z.
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        args.Handled = true;
        if (Shell.UndoCommand.CanExecute(null))
        {
            Shell.UndoCommand.Execute(null);
        }
    }

    private void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        args.Handled = true;
        if (Shell.RedoCommand.CanExecute(null))
        {
            Shell.RedoCommand.Execute(null);
        }
    }
}
