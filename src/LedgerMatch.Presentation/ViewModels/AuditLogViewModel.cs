using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed record AuditItem(AuditEntry Entry)
{
    public string At => Entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string Target => $"{Entry.EntityType} #{Entry.EntityId}";

    public string Action => Entry.Action switch
    {
        "Insert" => "新增",
        "Update" => "修改",
        "Delete" => "刪除",
        _ => Entry.Action,
    };

    public string Before => Entry.BeforeJson ?? string.Empty;

    public string After => Entry.AfterJson ?? string.Empty;
}

/// <summary>Read-only audit trail. There is deliberately no command that edits or deletes entries.</summary>
public sealed partial class AuditLogViewModel : PageViewModel
{
    public const int PageSize = 500;

    private long _lastSequence;

    public AuditLogViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
    }

    public ObservableCollection<AuditItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool hasMore;

    [RelayCommand]
    public Task Reload()
    {
        Items.Clear();
        _lastSequence = 0;
        return LoadMore();
    }

    [RelayCommand(CanExecute = nameof(HasMore))]
    private Task LoadMore() => RunAsync(async () =>
    {
        var after = _lastSequence;
        var page = await Task.Run(() => Session.Store.GetAuditEntries(after, PageSize));
        foreach (var entry in page)
        {
            Items.Add(new AuditItem(entry));
        }

        if (page.Count > 0)
        {
            _lastSequence = page[^1].Sequence;
        }

        HasMore = page.Count == PageSize;
        StatusMessage = $"已載入 {Items.Count} 筆稽核紀錄";
    }, "無法載入稽核紀錄");

    /// <summary>Re-reads the whole log and checks the hash chain, reporting the first broken entry if any.</summary>
    [RelayCommand]
    private Task Verify() => RunAsync(async () =>
    {
        var broken = await Task.Run(() =>
        {
            var all = new List<AuditEntry>();
            long after = 0;
            while (true)
            {
                var page = Session.Store.GetAuditEntries(after, 5000);
                if (page.Count == 0)
                {
                    break;
                }

                all.AddRange(page);
                after = page[^1].Sequence;
            }

            return (Count: all.Count, Broken: AuditHasher.FindFirstBrokenLink(all));
        });

        StatusMessage = broken.Broken is { } seq
            ? $"稽核紀錄第 {seq} 筆與雜湊鏈不符，檔案可能在程式外被修改。"
            : $"稽核紀錄完整，共 {broken.Count} 筆，雜湊鏈驗證通過。";
    }, "無法驗證稽核紀錄");
}
