using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class ReconciliationPage : UserControl
{
    public ReconciliationPage(ReconciliationViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Generate();
    }

    public ReconciliationViewModel ViewModel { get; }
}
