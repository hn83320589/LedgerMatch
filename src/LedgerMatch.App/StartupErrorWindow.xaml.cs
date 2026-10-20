using Microsoft.UI.Xaml;

namespace LedgerMatch.App;

/// <summary>Shown instead of the main window when the database is too new, damaged or not accessible.</summary>
public sealed partial class StartupErrorWindow : Window
{
    public StartupErrorWindow(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(820, 420));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
