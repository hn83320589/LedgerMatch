using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class ReceiptsPage : UserControl
{
    public ReceiptsPage(ReceiptsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    public ReceiptsViewModel ViewModel { get; }
}
