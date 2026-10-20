using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class AuditPage : UserControl
{
    public AuditPage(AuditLogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.Reload();
    }

    public AuditLogViewModel ViewModel { get; }
}
