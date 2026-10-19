using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed record RuleHitItem(string Date, string Description, string Amount, string Rule, string OldCategory, string NewCategory);

/// <summary>Categories and classification rules, with dry-run preview and one-step apply for the selected account and month.</summary>
public sealed partial class RulesViewModel : PageViewModel
{
    public RulesViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
        session.DataChanged += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<Category> Categories { get; } = [];

    public ObservableCollection<Rule> Rules { get; } = [];

    public ObservableCollection<RuleHitItem> PreviewHits { get; } = [];

    [ObservableProperty]
    private Category? selectedCategory;

    [ObservableProperty]
    private string categoryName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(DeleteRuleCommand))]
    private Rule? selectedRule;

    [ObservableProperty]
    private string ruleName = string.Empty;

    [ObservableProperty]
    private string contains = string.Empty;

    [ObservableProperty]
    private string regex = string.Empty;

    [ObservableProperty]
    private string minAmount = string.Empty;

    [ObservableProperty]
    private string maxAmount = string.Empty;

    [ObservableProperty]
    private bool onlyCurrentAccount;

    [ObservableProperty]
    private Category? ruleCategory;

    [ObservableProperty]
    private bool ruleEnabled = true;

    /// <summary>Apply to the selected account and month only (otherwise every open month of every account).</summary>
    [ObservableProperty]
    private bool scopeToCurrent = true;

    public void Refresh()
    {
        var selectedRuleId = SelectedRule?.Id;
        Categories.Clear();
        foreach (var c in Session.Queries.GetCategories())
        {
            Categories.Add(c);
        }

        Rules.Clear();
        foreach (var r in Session.Queries.GetRules())
        {
            Rules.Add(r);
        }

        SelectedRule = Rules.FirstOrDefault(r => r.Id == selectedRuleId);
    }

    partial void OnSelectedCategoryChanged(Category? value) => CategoryName = value?.Name ?? string.Empty;

    partial void OnSelectedRuleChanged(Rule? value)
    {
        RuleName = value?.Name ?? string.Empty;
        Contains = value?.DescriptionContains ?? string.Empty;
        Regex = value?.DescriptionRegex ?? string.Empty;
        MinAmount = value?.MinAmount?.ToPlainString() ?? string.Empty;
        MaxAmount = value?.MaxAmount?.ToPlainString() ?? string.Empty;
        OnlyCurrentAccount = value?.AccountId is not null;
        RuleCategory = Categories.FirstOrDefault(c => c.Id == value?.CategoryId);
        RuleEnabled = value?.Enabled ?? true;
    }

    [RelayCommand]
    private Task AddCategory() => Run(() => Session.Categories.Add(CategoryName), "無法新增科目");

    [RelayCommand]
    private Task RenameCategory() => Run(() =>
        Session.Categories.Rename((SelectedCategory ?? throw new LedgerException("請先選擇科目。")).Id, CategoryName), "無法改名");

    [RelayCommand]
    private Task DeleteCategory() => Run(() =>
        Session.Categories.Delete((SelectedCategory ?? throw new LedgerException("請先選擇科目。")).Id), "無法刪除科目");

    [RelayCommand]
    private void NewRule() => SelectedRule = null;

    [RelayCommand]
    private Task SaveRule() => Run(() =>
    {
        var rule = new Rule(
            SelectedRule?.Id ?? 0,
            RuleName,
            SelectedRule?.Priority ?? 0,
            Contains,
            Regex,
            ParseOptionalMoney(MinAmount, "金額下限"),
            ParseOptionalMoney(MaxAmount, "金額上限"),
            OnlyCurrentAccount ? Session.CurrentAccount?.Id ?? throw new LedgerException("請先選擇帳戶。") : null,
            (RuleCategory ?? throw new LedgerException("請選擇規則要歸入的科目。")).Id,
            RuleEnabled);
        var saved = Session.Rules.Save(rule);
        StatusMessage = $"已儲存規則「{saved.Name}」。";
    }, "無法儲存規則");

    [RelayCommand(CanExecute = nameof(HasRule))]
    private Task DeleteRule() => Run(() => Session.Rules.Delete(SelectedRule!.Id), "無法刪除規則");

    [RelayCommand(CanExecute = nameof(HasRule))]
    private Task MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasRule))]
    private Task MoveDown() => Move(+1);

    private bool HasRule() => SelectedRule is not null;

    private Task Move(int delta) => Run(() =>
    {
        var ids = Rules.Select(r => r.Id).ToList();
        var index = ids.IndexOf(SelectedRule!.Id);
        var target = index + delta;
        if (target < 0 || target >= ids.Count)
        {
            return;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);
        Session.Rules.Reorder(ids);
    }, "無法調整順序");

    [RelayCommand]
    private Task Preview() => Run(() =>
    {
        PreviewHits.Clear();
        var categories = Session.Queries.GetCategories().ToDictionary(c => c.Id, c => c.Name);
        foreach (var hit in Session.Rules.Preview(Scope()))
        {
            var txn = Session.Queries.GetTransaction(hit.TxnId)!;
            PreviewHits.Add(new RuleHitItem(
                Formatting.Date(txn.TxnDate),
                txn.Description,
                Formatting.Amount(txn.Amount),
                hit.RuleName,
                hit.OldCategoryId is { } old ? categories.GetValueOrDefault(old, "?") : "（未歸類）",
                categories.GetValueOrDefault(hit.NewCategoryId, "?")));
        }

        StatusMessage = $"預覽：套用後會修改 {PreviewHits.Count} 筆交易（尚未寫入）。";
    }, "無法預覽");

    [RelayCommand]
    private Task Apply() => Run(() =>
    {
        var count = Session.Rules.Apply(Scope());
        PreviewHits.Clear();
        StatusMessage = $"已套用規則，修改 {count} 筆交易。可用「復原」一步還原。";
    }, "無法套用規則");

    private RuleScope Scope() => ScopeToCurrent ? new RuleScope(Session.CurrentAccount?.Id, Session.CurrentMonth) : new RuleScope();

    private static Money? ParseOptionalMoney(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Formatting.TryParseMoney(text, out var money, out var error) ? money : throw new LedgerException($"{label}：{error}");
    }
}
