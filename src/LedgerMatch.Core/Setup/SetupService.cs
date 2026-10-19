using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Setup;

/// <summary>Creates and edits mapping profiles and bank accounts. Every edit is one undo step.</summary>
public sealed class SetupService(ILedgerStore store, ILedgerQueries queries, UndoRedoService history)
{
    public MappingProfile SaveProfile(MappingProfile profile)
    {
        var problems = profile.Validate();
        if (problems.Count > 0)
        {
            throw new LedgerException(string.Join(Environment.NewLine, problems));
        }

        if (queries.GetProfiles().Any(p => p.Id != profile.Id && string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LedgerException($"已有名為「{profile.Name}」的設定檔。");
        }

        if (profile.Id == 0)
        {
            var created = profile with { Id = store.ReserveIds(1) };
            history.Execute(new ChangeSetBuilder($"新增設定檔「{created.Name}」").Insert(created).Build());
            return created;
        }

        var existing = queries.GetProfile(profile.Id) ?? throw new LedgerException($"找不到設定檔 #{profile.Id}。");
        if (existing.Kind != profile.Kind && queries.GetAccounts().Any(a => a.ProfileId == profile.Id))
        {
            throw new LedgerException("已綁定帳戶的設定檔不能改成收款單設定檔。");
        }

        history.Execute(new ChangeSetBuilder($"編輯設定檔「{profile.Name}」").Update(existing, profile).Build());
        return profile;
    }

    public void DeleteProfile(long profileId)
    {
        var existing = queries.GetProfile(profileId) ?? throw new LedgerException($"找不到設定檔 #{profileId}。");
        var users = queries.GetAccounts().Where(a => a.ProfileId == profileId).Select(a => a.Name).ToList();
        if (users.Count > 0)
        {
            throw new LedgerException($"設定檔「{existing.Name}」仍被帳戶使用：{string.Join("、", users)}。");
        }

        history.Execute(new ChangeSetBuilder($"刪除設定檔「{existing.Name}」").Delete(existing).Build());
    }

    public Account CreateAccount(string name, long profileId, Money openingBalance, DateOnly openingDate)
    {
        var account = new Account(0, name.Trim(), profileId, openingBalance, openingDate);
        Validate(account);
        account = account with { Id = store.ReserveIds(1) };
        history.Execute(new ChangeSetBuilder($"新增帳戶「{account.Name}」").Insert(account).Build());
        return account;
    }

    public Account UpdateAccount(Account account)
    {
        var existing = queries.GetAccount(account.Id) ?? throw new LedgerException($"找不到帳戶 #{account.Id}。");
        account = account with { Name = account.Name.Trim() };
        Validate(account);
        history.Execute(new ChangeSetBuilder($"編輯帳戶「{account.Name}」").Update(existing, account).Build());
        return account;
    }

    private void Validate(Account account)
    {
        if (account.Name.Length == 0)
        {
            throw new LedgerException("帳戶名稱不可空白。");
        }

        if (queries.GetAccounts().Any(a => a.Id != account.Id && string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LedgerException($"已有名為「{account.Name}」的帳戶。");
        }

        var profile = queries.GetProfile(account.ProfileId) ?? throw new LedgerException("請選擇映射設定檔。");
        if (profile.Kind != ProfileKind.Statement)
        {
            throw new LedgerException($"帳戶必須綁定對帳單設定檔，「{profile.Name}」是收款單設定檔。");
        }
    }
}
