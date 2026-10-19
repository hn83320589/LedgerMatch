namespace LedgerMatch.Presentation.Services;

/// <summary>Dialogs implemented by the UI layer (ContentDialog in the WinUI app, fakes in tests).</summary>
public interface IDialogService
{
    Task ShowErrorAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Asks for a line of text; returns null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string placeholder);
}

public sealed record FileTypeFilter(string Description, string Extension);

public interface IFilePickerService
{
    Task<string?> PickOpenFileAsync(IReadOnlyList<FileTypeFilter> filters);

    Task<string?> PickSaveFileAsync(string suggestedName, FileTypeFilter filter);
}

/// <summary>File access behind an interface so view models can be tested without touching disk.</summary>
public interface IFileService
{
    Stream OpenRead(string path);

    void WriteAllBytes(string path, byte[] bytes);

    void WriteAllText(string path, string text);

    /// <summary>Opens the file with the system default application (e.g. the browser for HTML).</summary>
    void OpenWithShell(string path);
}
