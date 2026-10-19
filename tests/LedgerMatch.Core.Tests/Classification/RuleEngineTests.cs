using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;

namespace LedgerMatch.Core.Tests.Classification;

public class RuleEngineTests
{
    private static readonly YearMonth March = new(2026, 3);

    private static BankTransaction Txn(long id, string desc, long cents, long account = 1, long? category = null) =>
        new(id, account, March.FirstDay, March, Money.FromCents(cents), desc, null, null, $"k{id}", null, null, category, ClassificationSource.None, false);

    private static Rule NewRule(long id, int priority, long category, string? contains = null, string? regex = null,
        long? min = null, long? max = null, long? account = null, bool enabled = true) =>
        new(id, $"rule{id}", priority, contains, regex, min is null ? null : Money.FromCents(min.Value), max is null ? null : Money.FromCents(max.Value), account, category, enabled);

    [Fact]
    public void FirstRuleByPriorityWins()
    {
        var rules = new[] { NewRule(1, 20, 100, contains: "電"), NewRule(2, 10, 200, contains: "電費") };

        var hit = Assert.Single(RuleEngine.Evaluate(rules, [Txn(1, "台電電費", -500)]));

        Assert.Equal((2L, 200L), (hit.RuleId, hit.NewCategoryId));
    }

    [Fact]
    public void EqualPriority_FallsBackToRuleId()
    {
        var rules = new[] { NewRule(9, 1, 900, contains: "x"), NewRule(3, 1, 300, contains: "x") };
        Assert.Equal(300, Assert.Single(RuleEngine.Evaluate(rules, [Txn(1, "x", 1)])).NewCategoryId);
    }

    [Fact]
    public void AllConditionsMustHold()
    {
        var rule = NewRule(1, 1, 100, contains: "轉帳", min: -10000, max: -1, account: 7);

        Assert.Single(RuleEngine.Evaluate([rule], [Txn(1, "ATM 轉帳", -500, account: 7)]));
        Assert.Empty(RuleEngine.Evaluate([rule], [Txn(1, "ATM 轉帳", 500, account: 7)]));     // positive amount
        Assert.Empty(RuleEngine.Evaluate([rule], [Txn(1, "ATM 轉帳", -500, account: 8)]));    // other account
        Assert.Empty(RuleEngine.Evaluate([rule], [Txn(1, "ATM 提款", -500, account: 7)]));    // text
        Assert.Single(RuleEngine.Evaluate([rule], [Txn(1, "轉帳", -10000, account: 7)]));      // inclusive bound
    }

    [Fact]
    public void ContainsIsCaseAndWidthInsensitive()
    {
        Assert.Single(RuleEngine.Evaluate([NewRule(1, 1, 100, contains: "atm")], [Txn(1, "ＡＴＭ 轉帳", -1)]));
    }

    [Fact]
    public void RegexCondition()
    {
        var rule = NewRule(1, 1, 100, regex: @"^(台電|自來水)");
        Assert.Single(RuleEngine.Evaluate([rule], [Txn(1, "自來水費 3月", -1)]));
        Assert.Empty(RuleEngine.Evaluate([rule], [Txn(1, "繳 台電", -1)]));
    }

    [Fact]
    public void DisabledRulesAndUnchangedCategories_ProduceNoHit()
    {
        Assert.Empty(RuleEngine.Evaluate([NewRule(1, 1, 100, contains: "x", enabled: false)], [Txn(1, "x", 1)]));
        Assert.Empty(RuleEngine.Evaluate([NewRule(1, 1, 100, contains: "x")], [Txn(1, "x", 1, category: 100)]));
    }

    [Fact]
    public void Validate_CatchesBadRules()
    {
        Assert.Contains(RuleEngine.Validate(NewRule(1, 1, 1)), e => e.Contains("條件"));
        Assert.Contains(RuleEngine.Validate(NewRule(1, 1, 1, regex: "(")), e => e.Contains("正規表達式"));
        Assert.Contains(RuleEngine.Validate(NewRule(1, 1, 1, min: 5, max: 1)), e => e.Contains("下限"));
        Assert.Empty(RuleEngine.Validate(NewRule(1, 1, 1, contains: "a")));
    }

    [Fact]
    public void CatastrophicRegex_TimesOutAsNoMatch()
    {
        var rule = NewRule(1, 1, 100, regex: "^(a+)+$");
        Assert.Empty(RuleEngine.Evaluate([rule], [Txn(1, new string('a', 40) + "!", -1)]));
    }
}
