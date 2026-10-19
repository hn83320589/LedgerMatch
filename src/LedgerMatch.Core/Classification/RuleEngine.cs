using System.Text.RegularExpressions;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Classification;

/// <summary>What applying the rules would change for one transaction.</summary>
public sealed record RuleHit(long TxnId, long RuleId, string RuleName, long? OldCategoryId, long NewCategoryId);

/// <summary>Pure rule evaluation: rules ordered by (priority, id), disabled rules skipped, first matching rule wins.</summary>
public static class RuleEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<string> Validate(Rule rule)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            errors.Add("規則名稱不可空白。");
        }

        if (string.IsNullOrEmpty(rule.DescriptionContains) && string.IsNullOrEmpty(rule.DescriptionRegex)
            && rule.MinAmount is null && rule.MaxAmount is null && rule.AccountId is null)
        {
            errors.Add("規則至少要有一個條件。");
        }

        if (rule.MinAmount is { } min && rule.MaxAmount is { } max && min > max)
        {
            errors.Add("金額下限不可大於上限。");
        }

        if (!string.IsNullOrEmpty(rule.DescriptionRegex))
        {
            try
            {
                _ = new Regex(rule.DescriptionRegex, RegexOptions.None, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"正規表達式有誤：{ex.Message}");
            }
        }

        return errors;
    }

    /// <summary>Returns one hit per transaction whose category would change.</summary>
    public static IReadOnlyList<RuleHit> Evaluate(IEnumerable<Rule> rules, IEnumerable<BankTransaction> transactions)
    {
        var compiled = rules
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .Select(r => new CompiledRule(r))
            .ToList();

        var hits = new List<RuleHit>();
        foreach (var txn in transactions)
        {
            var description = ImportPlanner.NormalizeText(txn.Description);
            var rule = compiled.FirstOrDefault(r => r.Matches(txn, description));
            if (rule is not null && rule.Rule.CategoryId != txn.CategoryId)
            {
                hits.Add(new RuleHit(txn.Id, rule.Rule.Id, rule.Rule.Name, txn.CategoryId, rule.Rule.CategoryId));
            }
        }

        return hits;
    }

    private sealed class CompiledRule(Rule rule)
    {
        private readonly string? _contains = string.IsNullOrEmpty(rule.DescriptionContains) ? null : ImportPlanner.NormalizeText(rule.DescriptionContains);
        private readonly Regex? _regex = string.IsNullOrEmpty(rule.DescriptionRegex) ? null : new Regex(rule.DescriptionRegex, RegexOptions.None, RegexTimeout);

        public Rule Rule => rule;

        public bool Matches(BankTransaction txn, string normalizedDescription)
        {
            if (rule.AccountId is { } account && account != txn.AccountId)
            {
                return false;
            }

            if ((rule.MinAmount is { } min && txn.Amount < min) || (rule.MaxAmount is { } max && txn.Amount > max))
            {
                return false;
            }

            if (_contains is not null && !normalizedDescription.Contains(_contains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_regex is not null)
            {
                try
                {
                    return _regex.IsMatch(txn.Description);
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological pattern must not hang classification; treat as "no match".
                    return false;
                }
            }

            return true;
        }
    }
}
