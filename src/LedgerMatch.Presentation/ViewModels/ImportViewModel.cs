using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core.Importing;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

/// <summary>Statement and receipt import. Statement import runs in the background and can be cancelled.</summary>
public sealed partial class ImportViewModel : PageViewModel
{
    private readonly IFilePickerService _files;
    private readonly IFileService _fileSystem;
    private CancellationTokenSource? _cancellation;

    public ImportViewModel(LedgerSession session, IDialogService dialogs, IFilePickerService files, IFileService fileSystem)
        : base(session, dialogs)
    {
        _files = files;
        _fileSystem = fileSystem;
        session.DataChanged += (_, _) => RefreshProfiles();
        RefreshProfiles();
    }

    public ObservableCollection<MappingProfile> ReceiptProfiles { get; } = [];

    public ObservableCollection<RowError> Errors { get; } = [];

    [ObservableProperty]
    private MappingProfile? selectedReceiptProfile;

    [ObservableProperty]
    private string progressText = string.Empty;

    [ObservableProperty]
    private string? summary;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isImporting;

    public string Target => Session.CurrentAccount is { } a ? $"匯入到：{a.Name}" : "請先在上方選擇帳戶";

    private void RefreshProfiles()
    {
        var selected = SelectedReceiptProfile?.Id;
        ReceiptProfiles.Clear();
        foreach (var p in Session.Queries.GetProfiles().Where(p => p.Kind == ProfileKind.Receipt))
        {
            ReceiptProfiles.Add(p);
        }

        SelectedReceiptProfile = ReceiptProfiles.FirstOrDefault(p => p.Id == selected) ?? ReceiptProfiles.FirstOrDefault();
        OnPropertyChanged(nameof(Target));
    }

    [RelayCommand]
    private async Task ImportStatement()
    {
        var path = await _files.PickOpenFileAsync([new FileTypeFilter("CSV 檔", ".csv"), new FileTypeFilter("文字檔", ".txt")]);
        if (path is not null)
        {
            await ImportStatementFileAsync(path);
        }
    }

    public async Task ImportStatementFileAsync(string path)
    {
        var account = Session.CurrentAccount;
        if (account is null)
        {
            await Dialogs.ShowErrorAsync("無法匯入", "請先在上方選擇帳戶。");
            return;
        }

        _cancellation = new CancellationTokenSource();
        IsImporting = true;
        Errors.Clear();
        Summary = null;
        try
        {
            await RunAsync(async () =>
            {
                await using var stream = _fileSystem.OpenRead(path);
                var progress = new Progress<ImportProgress>(p => ProgressText = p.Stage switch
                {
                    ImportStage.Parsing => "解析檔案中…",
                    ImportStage.Planning => $"比對重複中… {p.Processed}/{p.Total}",
                    ImportStage.Saving => "寫入資料庫中…",
                    _ => "完成",
                });
                var result = await Session.Statements.ImportAsync(account.Id, stream, Path.GetFileName(path), progress, _cancellation.Token);
                ShowSummary(result.Added, result.SkippedDuplicates, result.Errors, result.Pending);
                Session.NotifyDataChanged();
            }, "匯入失敗");
        }
        finally
        {
            IsImporting = false;
            ProgressText = string.Empty;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsImporting))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private async Task ImportReceipts()
    {
        var path = await _files.PickOpenFileAsync([new FileTypeFilter("CSV 檔", ".csv")]);
        if (path is not null)
        {
            await ImportReceiptsFileAsync(path);
        }
    }

    public Task<bool> ImportReceiptsFileAsync(string path) => Run(() =>
    {
        if (SelectedReceiptProfile is null)
        {
            throw new Core.Persistence.LedgerException("請先建立並選擇收款單設定檔。");
        }

        Errors.Clear();
        using var stream = _fileSystem.OpenRead(path);
        var result = Session.Receipts.Import(Session.CurrentAccount?.Id, SelectedReceiptProfile.Id, stream, Path.GetFileName(path));
        ShowSummary(result.Added, result.SkippedDuplicates, result.Errors, pending: null);
        Session.NotifyDataChanged();
    }, "匯入收款單失敗");

    private void ShowSummary(int added, int skipped, IReadOnlyList<RowError> errors, int? pending)
    {
        foreach (var e in errors)
        {
            Errors.Add(e);
        }

        Summary = $"新增 {added} 筆，判定為重複而略過 {skipped} 筆，錯誤 {errors.Count} 列" + (pending is > 0 ? $"，{pending} 筆落在已結帳月份，已放入待處理。" : "。");
        StatusMessage = Summary;
    }
}
