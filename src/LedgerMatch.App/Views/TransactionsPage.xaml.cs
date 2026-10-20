using LedgerMatch.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LedgerMatch.App.Views;

/// <summary>
/// Virtualised transaction list. The next page is requested when the user scrolls near the bottom, so only
/// visible rows are realised and only loaded pages are kept in memory.
/// </summary>
public sealed partial class TransactionsPage : UserControl
{
    private ScrollViewer? _scroller;

    public TransactionsPage(TransactionsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public TransactionsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroller is null)
        {
            _scroller = FindScrollViewer(List);
            if (_scroller is not null)
            {
                _scroller.ViewChanged += OnViewChanged;
            }
        }

        await ViewModel.ReloadAsync();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is { } s && s.VerticalOffset >= s.ScrollableHeight - 400 && ViewModel.LoadMoreCommand.CanExecute(null) && !ViewModel.IsBusy)
        {
            ViewModel.LoadMoreCommand.Execute(null);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (TransactionItem item in e.RemovedItems)
        {
            ViewModel.SelectedItems.Remove(item);
        }

        foreach (TransactionItem item in e.AddedItems)
        {
            ViewModel.SelectedItems.Add(item);
        }
    }

    private void OnSplitCategoryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox { Tag: SplitLineInput line } combo && combo.ItemsSource is null)
        {
            combo.ItemsSource = ViewModel.Split.Categories;
            combo.SelectedItem = line.Category;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
