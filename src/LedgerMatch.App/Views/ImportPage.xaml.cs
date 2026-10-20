using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class ImportPage : UserControl
{
    public ImportPage(ImportViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public ImportViewModel ViewModel { get; }
}
