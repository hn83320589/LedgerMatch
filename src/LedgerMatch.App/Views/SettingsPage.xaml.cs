using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LedgerMatch.App.Views;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    public void SetTolerance(double value) => ViewModel.ExactDateToleranceDays = Ui.ToInt(value);

    public void SetLookback(double value) => ViewModel.LookbackDays = Ui.ToInt(value);

    public void SetCandidates(double value) => ViewModel.MaxCandidates = Ui.ToInt(value);

    public void SetComboSize(double value) => ViewModel.MaxComboSize = Ui.ToInt(value);
}
