using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Tests.Importing;
using LedgerMatch.Data;
using LedgerMatch.Data.Tests;
using LedgerMatch.Presentation.Services;
using Microsoft.Extensions.Time.Testing;

namespace LedgerMatch.Presentation.Tests;

public sealed class FakeDialogs : IDialogService
{
    public List<(string Title, string Message)> Errors { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public string? PromptAnswer { get; set; }

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmAnswer);

    public Task<string?> PromptAsync(string title, string message, string placeholder) => Task.FromResult(PromptAnswer);
}

public sealed class FakeFiles : IFilePickerService, IFileService
{
    public string? NextOpenPath { get; set; }

    public string? NextSavePath { get; set; }

    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> Opened { get; } = [];

    public Task<string?> PickOpenFileAsync(IReadOnlyList<FileTypeFilter> filters) => Task.FromResult(NextOpenPath);

    public Task<string?> PickSaveFileAsync(string suggestedName, FileTypeFilter filter) => Task.FromResult(NextSavePath);

    public Stream OpenRead(string path) => Files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes) : File.OpenRead(path);

    public void WriteAllBytes(string path, byte[] bytes) => Files[path] = bytes;

    public void WriteAllText(string path, string text) => Files[path] = System.Text.Encoding.UTF8.GetBytes(text);

    public void OpenWithShell(string path) => Opened.Add(path);
}

/// <summary>A session on a temporary SQLite file with fake dialogs and files.</summary>
public sealed class TestApp : IDisposable
{
    private readonly TestDatabase _db = new();

    public TestApp()
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.FromHours(8)));
        Time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("TPE", TimeSpan.FromHours(8), "TPE", "TPE"));
        var store = _db.OpenStore();
        Store = store;
        Session = new LedgerSession(store, store, Time);
    }

    public FakeTimeProvider Time { get; }

    public SqliteLedgerStore Store { get; }

    public LedgerSession Session { get; }

    public FakeDialogs Dialogs { get; } = new();

    public FakeFiles Files { get; } = new();

    public static YearMonth March { get; } = new(2026, 3);

    /// <summary>Creates 甲銀行 with its profile, selects it and March, and optionally imports a CSV.</summary>
    public Account SetUpBankA(string? csv = null)
    {
        var profile = Session.Setup.SaveProfile(TestProfiles.BankA);
        var account = Session.Setup.CreateAccount("甲銀行帳戶", profile.Id, Money.FromCents(1_000_000), new DateOnly(2026, 1, 1));
        Session.CurrentAccount = account;
        Session.CurrentMonth = March;
        if (csv is not null)
        {
            Session.Statements.Import(account.Id, TestProfiles.Utf8(csv), "a.csv");
        }

        Session.History.Clear();
        return account;
    }

    public void Dispose() => _db.Dispose();
}
