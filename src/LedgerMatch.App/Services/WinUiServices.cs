using System.Diagnostics;
using LedgerMatch.Presentation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace LedgerMatch.App.Services;

/// <summary>ContentDialog-based dialogs. Only one ContentDialog may be open at a time, so calls are serialised.</summary>
public sealed class DialogService(Func<XamlRoot?> xamlRoot) : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ShowErrorAsync(string title, string message)
    {
        await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "確定",
        });
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "確定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        });
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> PromptAsync(string title, string message, string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder, AcceptsReturn = false };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var result = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "確定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        });
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            dialog.XamlRoot = xamlRoot() ?? throw new InvalidOperationException("Window is not ready for dialogs.");
            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Classic pickers initialised with the window handle (required for unpackaged apps).</summary>
public sealed class FilePickerService(Func<nint> windowHandle) : IFilePickerService
{
    public async Task<string?> PickOpenFileAsync(IReadOnlyList<FileTypeFilter> filters)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var f in filters)
        {
            picker.FileTypeFilter.Add(f.Extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, FileTypeFilter filter)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };
        picker.FileTypeChoices.Add(filter.Description, [filter.Extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}

public sealed class FileService : IFileService
{
    public Stream OpenRead(string path) => File.OpenRead(path);

    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    public void WriteAllText(string path, string text) => File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public void OpenWithShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
