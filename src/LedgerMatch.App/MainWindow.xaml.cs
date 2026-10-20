using LedgerMatch.App.Services;
using LedgerMatch.App.Views;
using LedgerMatch.Presentation;
using LedgerMatch.Presentation.Services;
using Microsoft.UI.Xaml;

namespace LedgerMatch.App;

public sealed partial class MainWindow : Window
{
    private DialogService? _dialogs;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
    }

    public void Initialize(LedgerSession session, IFileService files)
    {
        _dialogs = new DialogService(() => Root.XamlRoot);
        var pickers = new FilePickerService(() => WinRT.Interop.WindowNative.GetWindowHandle(this));
        Root.Children.Add(new ShellView(session, _dialogs, pickers, files));
    }

    public async Task ShowFatalAsync(Exception exception)
    {
        if (_dialogs is not null)
        {
            await _dialogs.ShowErrorAsync("發生未預期的錯誤", $"{exception.GetType().Name}: {exception.Message}\n\n資料庫的每次寫入都是完整交易，已寫入的資料不受影響。");
        }
    }
}
