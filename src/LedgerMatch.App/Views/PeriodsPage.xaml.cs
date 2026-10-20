using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class PeriodsPage : UserControl
{
    public PeriodsPage(PeriodsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    public PeriodsViewModel ViewModel { get; }
}
