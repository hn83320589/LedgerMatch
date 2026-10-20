using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class MatchingPage : UserControl
{
    public MatchingPage(MatchingViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    public MatchingViewModel ViewModel { get; }
}
