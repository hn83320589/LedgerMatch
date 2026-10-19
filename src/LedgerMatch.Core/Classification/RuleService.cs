using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Classification;

/// <summary>Which transactions rules are applied to; null means "all".</summary>
public sealed record RuleScope(long? AccountId = null, YearMonth? Period = null);

/// <summary>
/// Rule maintenance plus dry-run and apply. Rules only touch transactions in open months that are not
/// manually classified and not split. Apply is a single undo step.
/// </summary>
public sealed class RuleService(ILedgerStore store, ILedgerQueries queries, UndoRedoService history)
{
    public Rule Save(Rule rule)
    {
        rule = rule with
        {
            Name = rule.Name.Trim(),
            DescriptionContains = string.IsNullOrWhiteSpace(rule.DescriptionContains) ? null : rule.DescriptionContains.Trim(),
            DescriptionRegex = string.IsNullOrEmpty(rule.DescriptionRegex) ? null : rule.DescriptionRegex,
        };

        var problems = RuleEngine.Validate(rule);
        if (problems.Count > 0)
        {
            throw new LedgerException(string.Join(Environment.NewLine, problems));
        }

        _ = queries.GetCategory(rule.CategoryId) ?? throw new LedgerException("請選擇規則要歸入的科目。");

        if (rule.Id == 0)
        {
            var existing = queries.GetRules();
            rule = rule with { Id = store.ReserveIds(1), Priority = existing.Count == 0 ? 1 : existing.Max(r => r.Priority) + 1 };
            history.Execute(new ChangeSetBuilder($"新增規則「{rule.Name}」").Insert(rule).Build());
            return rule;
        }

        var before = queries.GetRule(rule.Id) ?? throw new LedgerException($"找不到規則 #{rule.Id}。");
        history.Execute(new ChangeSetBuilder($"編輯規則「{rule.Name}」").Update(before, rule).Build());
        return rule;
    }

    public void Delete(long ruleId)
    {
        var rule = queries.GetRule(ruleId) ?? throw new LedgerException($"找不到規則 #{ruleId}。");
        history.Execute(new ChangeSetBuilder($"刪除規則「{rule.Name}」").Delete(rule).Build());
    }

    /// <summary>Sets priorities to 1..n in the given order; the list must contain every rule exactly once.</summary>
    public void Reorder(IReadOnlyList<long> ruleIdsInOrder)
    {
        var rules = queries.GetRules();
        if (ruleIdsInOrder.Count != rules.Count || !rules.Select(r => r.Id).ToHashSet().SetEquals(ruleIdsInOrder))
        {
            throw new LedgerException("排序清單必須包含所有規則各一次。");
        }

        var byId = rules.ToDictionary(r => r.Id);
        var changes = new ChangeSetBuilder("調整規則順序");
        for (var i = 0; i < ruleIdsInOrder.Count; i++)
        {
            var rule = byId[ruleIdsInOrder[i]];
            changes.Update(rule, rule with { Priority = i + 1 });
        }

        history.Execute(changes.Build());
    }

    /// <summary>Dry run: what <see cref="Apply"/> would change. Never writes.</summary>
    public IReadOnlyList<RuleHit> Preview(RuleScope scope) => RuleEngine.Evaluate(queries.GetRules(), EligibleTransactions(scope));

    public int Apply(RuleScope scope)
    {
        var hits = Preview(scope);
        var changes = new ChangeSetBuilder($"套用規則（{hits.Count} 筆）");
        foreach (var hit in hits)
        {
            var txn = queries.GetTransaction(hit.TxnId)!;
            changes.Update(txn, txn with { CategoryId = hit.NewCategoryId, ClassifiedBy = ClassificationSource.Rule });
        }

        history.Execute(changes.Build());
        return hits.Count;
    }

    private IEnumerable<BankTransaction> EligibleTransactions(RuleScope scope)
    {
        var closed = store.GetClosedPeriods();
        return queries.FindTransactions(new TransactionFilter { AccountId = scope.AccountId, Period = scope.Period })
            .Where(row => !row.IsSplit
                && row.Transaction.ClassifiedBy != ClassificationSource.Manual
                && !closed.Contains(row.Transaction.PostingPeriod))
            .Select(row => row.Transaction);
    }
}
