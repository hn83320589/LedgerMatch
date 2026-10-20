using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class RulesPage : UserControl
{
    public RulesPage(RulesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    public RulesViewModel ViewModel { get; }
}
