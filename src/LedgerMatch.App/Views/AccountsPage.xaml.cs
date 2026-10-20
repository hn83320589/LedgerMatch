using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class AccountsPage : UserControl
{
    public AccountsPage(AccountsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    public AccountsViewModel ViewModel { get; }

    public void SetHeaderRows(double value) => ViewModel.Editor.HeaderRowCount = Ui.ToInt(value);
}
