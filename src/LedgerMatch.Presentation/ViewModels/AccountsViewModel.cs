using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

/// <summary>Bank accounts and mapping profiles.</summary>
public sealed partial class AccountsViewModel : PageViewModel
{
    public AccountsViewModel(LedgerSession session, IDialogService dialogs, IFilePickerService files, IFileService fileSystem)
        : base(session, dialogs)
    {
        Editor = new ProfileEditorViewModel(session, dialogs, files, fileSystem);
        Editor.Saved += (_, _) => Refresh();
        session.DataChanged += (_, _) => Refresh();
        Refresh();
    }

    public ProfileEditorViewModel Editor { get; }

    public ObservableCollection<MappingProfile> Profiles { get; } = [];

    public ObservableCollection<MappingProfile> StatementProfiles { get; } = [];

    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditProfileCommand), nameof(DeleteProfileCommand))]
    private MappingProfile? selectedProfile;

    [ObservableProperty]
    private Account? selectedAccount;

    [ObservableProperty]
    private string accountName = string.Empty;

    [ObservableProperty]
    private MappingProfile? accountProfile;

    [ObservableProperty]
    private string openingBalance = "0";

    [ObservableProperty]
    private string openingDate = string.Empty;

    public void Refresh()
    {
        var profiles = Session.Queries.GetProfiles();
        Replace(Profiles, profiles);
        Replace(StatementProfiles, profiles.Where(p => p.Kind == ProfileKind.Statement));
        Replace(Accounts, Session.Queries.GetAccounts());
    }

    partial void OnSelectedProfileChanged(MappingProfile? value)
    {
        if (value is not null)
        {
            Editor.Load(value);
        }
    }

    partial void OnSelectedAccountChanged(Account? value)
    {
        AccountName = value?.Name ?? string.Empty;
        AccountProfile = StatementProfiles.FirstOrDefault(p => p.Id == value?.ProfileId);
        OpeningBalance = value is null ? "0" : value.OpeningBalance.ToPlainString();
        OpeningDate = value is null ? Formatting.Date(DateOnly.FromDateTime(Session.Time.GetLocalNow().DateTime)) : Formatting.Date(value.OpeningDate);
    }

    [RelayCommand]
    private void NewProfile() => Editor.Load(null);

    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private void EditProfile() => Editor.Load(SelectedProfile);

    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private async Task DeleteProfile()
    {
        var profile = SelectedProfile!;
        if (await Dialogs.ConfirmAsync("刪除設定檔", $"確定刪除設定檔「{profile.Name}」？"))
        {
            await Run(() => Session.Setup.DeleteProfile(profile.Id), "無法刪除設定檔");
        }
    }

    private bool HasSelectedProfile() => SelectedProfile is not null;

    [RelayCommand]
    private void NewAccount() => SelectedAccount = null;

    [RelayCommand]
    private Task SaveAccount() => Run(() =>
    {
        if (AccountProfile is null)
        {
            throw new Core.Persistence.LedgerException("請選擇對帳單設定檔。");
        }

        if (!Formatting.TryParseMoney(OpeningBalance, out var opening, out var error))
        {
            throw new Core.Persistence.LedgerException($"期初餘額：{error}");
        }

        if (!Formatting.TryParseDate(OpeningDate, out var date))
        {
            throw new Core.Persistence.LedgerException("期初日期格式應為 yyyy-MM-dd。");
        }

        var saved = SelectedAccount is null
            ? Session.Setup.CreateAccount(AccountName, AccountProfile.Id, opening, date)
            : Session.Setup.UpdateAccount(SelectedAccount with { Name = AccountName, ProfileId = AccountProfile.Id, OpeningBalance = opening, OpeningDate = date });
        StatusMessage = $"已儲存帳戶「{saved.Name}」。";
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == saved.Id);
    }, "無法儲存帳戶");

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
