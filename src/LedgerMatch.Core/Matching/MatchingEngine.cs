using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Matching;

public sealed record DepositCandidate(long Id, DateOnly Date, Money Amount, string Description, string DedupKey);

public sealed record ReceiptCandidate(long Id, DateOnly Date, Money Amount, string ReceiptNo);

/// <summary>A proposed pairing. <see cref="FeasibleCombinations"/> counts every combination that satisfied the stage's rule.</summary>
public sealed record MatchProposal(
    long DepositId,
    IReadOnlyList<long> ReceiptIds,
    MatchKind Kind,
    MatchConfidence Confidence,
    Money Fee,
    int FeasibleCombinations,
    bool CandidatesTruncated);

public enum MatchingWarningKind
{
    CandidatesTruncated,
    SearchBudgetExceeded,
}

public sealed record MatchingWarning(long DepositId, MatchingWarningKind Kind, string Message);

public sealed record MatchingResult(IReadOnlyList<MatchProposal> Proposals, IReadOnlyList<MatchingWarning> Warnings);

public enum MatchingStage
{
    Exact = 1,
    OneToMany = 2,
    WithFee = 3,
}

public sealed record MatchingProgress(MatchingStage Stage, int Processed, int Total);

/// <summary>
/// Three-stage matcher (README "配對演算法"). Pure and deterministic: inputs are re-sorted by stable business
/// keys — deposits by (date, amount desc, dedup key), receipts by (issue date, receipt no) — so neither
/// database ids nor input order affect the result. Each stage only sees what earlier stages left.
/// </summary>
public static class MatchingEngine
{
    private const int MinReceiptNoLengthForTextMatch = 3;
    private const int CancellationCheckInterval = 8192;

    public static MatchingResult Run(
        IEnumerable<DepositCandidate> deposits,
        IEnumerable<ReceiptCandidate> receipts,
        MatchingSettings settings,
        IProgress<MatchingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));
        }

        var orderedDeposits = deposits
            .Where(d => d.Amount.IsPositive)
            .OrderBy(d => d.Date)
            .ThenByDescending(d => d.Amount)
            .ThenBy(d => d.DedupKey, StringComparer.Ordinal)
            .ToList();
        var orderedReceipts = receipts
            .Where(r => r.Amount.IsPositive)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.ReceiptNo, StringComparer.Ordinal)
            .ToArray();

        var state = new State(orderedReceipts);
        var proposals = new List<MatchProposal>();
        var warnings = new List<MatchingWarning>();

        RunExact(orderedDeposits, state, settings, proposals, progress, cancellationToken);
        RunOneToMany(orderedDeposits, state, settings, proposals, warnings, progress, cancellationToken);
        if (settings.FeeMatchingEnabled)
        {
            RunWithFee(orderedDeposits, state, settings, proposals, warnings, progress, cancellationToken);
        }

        return new MatchingResult(proposals, warnings);
    }

    private static void RunExact(
        List<DepositCandidate> deposits, State state, MatchingSettings settings, List<MatchProposal> proposals,
        IProgress<MatchingProgress>? progress, CancellationToken ct)
    {
        var byAmount = Enumerable.Range(0, state.Receipts.Length).ToLookup(i => state.Receipts[i].Amount);
        for (var d = 0; d < deposits.Count; d++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new MatchingProgress(MatchingStage.Exact, d, deposits.Count));

            var deposit = deposits[d];
            var description = ImportPlanner.NormalizeText(deposit.Description);
            var best = -1;
            (int Hit, int Diff) bestKey = (int.MaxValue, int.MaxValue);
            var feasible = 0;

            foreach (var rank in byAmount[deposit.Amount])
            {
                if (state.Used[rank])
                {
                    continue;
                }

                var receipt = state.Receipts[rank];
                var diff = Math.Abs(deposit.Date.DayNumber - receipt.Date.DayNumber);
                var hit = NumberAppearsIn(receipt.ReceiptNo, description);
                if (!hit && diff > settings.ExactDateToleranceDays)
                {
                    continue;
                }

                feasible++;
                var key = (hit ? 0 : 1, diff);
                if (key.CompareTo(bestKey) < 0)
                {
                    // Ranks ascend, so on equal keys the first (earliest receipt) is kept.
                    best = rank;
                    bestKey = key;
                }
            }

            if (best >= 0)
            {
                state.Used[best] = true;
                state.Matched.Add(deposit.Id);
                proposals.Add(new MatchProposal(deposit.Id, [state.Receipts[best].Id], MatchKind.Exact, MatchConfidence.Confirmed, Money.Zero, feasible, false));
            }
        }
    }

    private static void RunOneToMany(
        List<DepositCandidate> deposits, State state, MatchingSettings settings, List<MatchProposal> proposals,
        List<MatchingWarning> warnings, IProgress<MatchingProgress>? progress, CancellationToken ct)
    {
        for (var d = 0; d < deposits.Count; d++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new MatchingProgress(MatchingStage.OneToMany, d, deposits.Count));

            var deposit = deposits[d];
            if (state.Matched.Contains(deposit.Id))
            {
                continue;
            }

            var (candidates, truncated) = SelectCandidates(deposit, state, settings, r => r.Amount < deposit.Amount, warnings);
            if (candidates.Count < 2)
            {
                continue;
            }

            var search = new ComboSearch(candidates, settings.NodeBudget, ct);
            var target = deposit.Amount.Cents;
            var found = search.Run(2, settings.MaxComboSize, target, target, isBetter: (a, b) => a.Size != b.Size ? a.Size < b.Size : a.DateDiff < b.DateDiff, stopAfterFirstFeasibleSize: true);

            if (!Accept(deposit, search, found, warnings))
            {
                continue;
            }

            Commit(state, deposit, candidates, found!, MatchKind.OneToMany, Money.Zero, search.Feasible, truncated, proposals);
        }
    }

    private static void RunWithFee(
        List<DepositCandidate> deposits, State state, MatchingSettings settings, List<MatchProposal> proposals,
        List<MatchingWarning> warnings, IProgress<MatchingProgress>? progress, CancellationToken ct)
    {
        for (var d = 0; d < deposits.Count; d++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new MatchingProgress(MatchingStage.WithFee, d, deposits.Count));

            var deposit = deposits[d];
            if (state.Matched.Contains(deposit.Id))
            {
                continue;
            }

            var maxTotal = MaxReceiptsTotal(deposit.Amount, settings);
            var (candidates, truncated) = SelectCandidates(deposit, state, settings, r => r.Amount.Cents <= maxTotal, warnings);
            if (candidates.Count == 0)
            {
                continue;
            }

            var search = new ComboSearch(candidates, settings.NodeBudget, ct)
            {
                Accept = total => total > deposit.Amount.Cents
                    && total - deposit.Amount.Cents <= settings.AllowedFee(Money.FromCents(total)).Cents,
            };
            var found = search.Run(
                1, settings.MaxComboSize, deposit.Amount.Cents + 1, maxTotal,
                isBetter: (a, b) => a.Total != b.Total ? a.Total < b.Total : a.Size != b.Size ? a.Size < b.Size : a.DateDiff < b.DateDiff,
                stopAfterFirstFeasibleSize: false);

            if (!Accept(deposit, search, found, warnings))
            {
                continue;
            }

            var fee = Money.FromCents(found!.Total - deposit.Amount.Cents);
            Commit(state, deposit, candidates, found, MatchKind.WithFee, fee, search.Feasible, truncated, proposals);
        }
    }

    /// <summary>Upper bound on a receipts total S that can still satisfy S − D ≤ max(fixed, floor(S × rate)).</summary>
    private static long MaxReceiptsTotal(Money deposit, MatchingSettings settings)
    {
        var bound = deposit.Cents + (settings.FeeFixedCap?.Cents ?? 0);
        if (settings.FeeRateCap is { } rate and < 1m)
        {
            bound = Math.Max(bound, (long)decimal.Floor(deposit.Cents / (1m - rate)));
        }

        return bound;
    }

    private static (List<Candidate> Candidates, bool Truncated) SelectCandidates(
        DepositCandidate deposit, State state, MatchingSettings settings, Func<ReceiptCandidate, bool> filter, List<MatchingWarning> warnings)
    {
        var all = new List<Candidate>();
        for (var rank = 0; rank < state.Receipts.Length; rank++)
        {
            var r = state.Receipts[rank];
            var daysBefore = deposit.Date.DayNumber - r.Date.DayNumber;
            if (!state.Used[rank] && daysBefore >= 0 && daysBefore <= settings.LookbackDays && filter(r))
            {
                all.Add(new Candidate(rank, r.Id, r.Amount.Cents, daysBefore));
            }
        }

        if (all.Count <= settings.MaxCandidates)
        {
            return (all, false);
        }

        if (!warnings.Any(w => w.DepositId == deposit.Id && w.Kind == MatchingWarningKind.CandidatesTruncated))
        {
            warnings.Add(new MatchingWarning(
                deposit.Id,
                MatchingWarningKind.CandidatesTruncated,
                $"候選收款單 {all.Count} 張超過上限 {settings.MaxCandidates} 張，只搜尋日期最接近的 {settings.MaxCandidates} 張。"));
        }

        return (all
            .OrderBy(c => c.DaysBefore)
            .ThenBy(c => c.Rank)
            .Take(settings.MaxCandidates)
            .OrderBy(c => c.Rank)
            .ToList(), true);
    }

    private static bool Accept(DepositCandidate deposit, ComboSearch search, ComboSearch.Combo? found, List<MatchingWarning> warnings)
    {
        if (search.BudgetExceeded)
        {
            warnings.Add(new MatchingWarning(
                deposit.Id,
                MatchingWarningKind.SearchBudgetExceeded,
                $"組合搜尋超過 {search.NodeBudget:N0} 步上限，這筆入帳不自動配對，請手動處理。"));
            return false;
        }

        return found is not null;
    }

    private static void Commit(
        State state, DepositCandidate deposit, List<Candidate> candidates, ComboSearch.Combo found, MatchKind kind, Money fee,
        int feasible, bool truncated, List<MatchProposal> proposals)
    {
        var picked = found.Indices.Select(i => candidates[i]).ToList();
        foreach (var c in picked)
        {
            state.Used[c.Rank] = true;
        }

        state.Matched.Add(deposit.Id);
        proposals.Add(new MatchProposal(deposit.Id, picked.Select(c => c.ReceiptId).ToList(), kind, MatchConfidence.Suggested, fee, feasible, truncated));
    }

    internal static bool NumberAppearsIn(string receiptNo, string normalizedDescription)
    {
        var number = ImportPlanner.NormalizeText(receiptNo);
        return number.Length >= MinReceiptNoLengthForTextMatch
            && normalizedDescription.Contains(number, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class State(ReceiptCandidate[] receipts)
    {
        public ReceiptCandidate[] Receipts { get; } = receipts;

        public bool[] Used { get; } = new bool[receipts.Length];

        public HashSet<long> Matched { get; } = [];
    }

    internal sealed record Candidate(int Rank, long ReceiptId, long Amount, int DaysBefore);

    /// <summary>
    /// Enumerates combinations of candidate indices in lexicographic (= receipt rank) order, smallest size first,
    /// with sum bounds pruning. Among feasible combinations the first one that no later one beats wins, which
    /// makes "lexicographically smallest receipt list" the final tie-breaker.
    /// </summary>
    internal sealed class ComboSearch
    {
        private readonly List<Candidate> _candidates;
        private readonly CancellationToken _ct;
        private readonly long[,] _minSuffix;
        private readonly long[,] _maxSuffix;
        private readonly int[] _stack;
        private long _nodes;

        public ComboSearch(List<Candidate> candidates, int nodeBudget, CancellationToken ct)
        {
            _candidates = candidates;
            NodeBudget = nodeBudget;
            _ct = ct;
            var n = candidates.Count;
            _stack = new int[n];
            _minSuffix = new long[n + 1, n + 1];
            _maxSuffix = new long[n + 1, n + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                var sorted = candidates.Skip(i).Select(c => c.Amount).Order().ToArray();
                for (var j = 1; j <= sorted.Length; j++)
                {
                    _minSuffix[i, j] = _minSuffix[i, j - 1] + sorted[j - 1];
                    _maxSuffix[i, j] = _maxSuffix[i, j - 1] + sorted[^j];
                }
            }
        }

        public int NodeBudget { get; }

        public bool BudgetExceeded { get; private set; }

        public int Feasible { get; private set; }

        public Func<long, bool> Accept { get; init; } = _ => true;

        public Combo? Run(int minSize, int maxSize, long lowTotal, long highTotal, Func<Combo, Combo, bool> isBetter, bool stopAfterFirstFeasibleSize)
        {
            Combo? best = null;
            for (var size = minSize; size <= Math.Min(maxSize, _candidates.Count); size++)
            {
                Search(0, 0, size, 0, 0, lowTotal, highTotal, isBetter, ref best);
                if (BudgetExceeded || (stopAfterFirstFeasibleSize && best is not null))
                {
                    break;
                }
            }

            return BudgetExceeded ? null : best;
        }

        private void Search(int start, int depth, int size, long sum, int dateDiff, long low, long high, Func<Combo, Combo, bool> isBetter, ref Combo? best)
        {
            if (depth == size)
            {
                if (sum >= low && sum <= high && Accept(sum))
                {
                    Feasible++;
                    var combo = new Combo(_stack[..size], size, sum, dateDiff);
                    if (best is null || isBetter(combo, best))
                    {
                        best = combo;
                    }
                }

                return;
            }

            var need = size - depth;
            for (var i = start; i <= _candidates.Count - need; i++)
            {
                if (++_nodes > NodeBudget)
                {
                    BudgetExceeded = true;
                    return;
                }

                if (_nodes % CancellationCheckInterval == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                }

                // Both bounds only get worse further right, so the rest of this level can be skipped.
                if (sum + _minSuffix[i, need] > high || sum + _maxSuffix[i, need] < low)
                {
                    break;
                }

                _stack[depth] = i;
                var c = _candidates[i];
                Search(i + 1, depth + 1, size, sum + c.Amount, dateDiff + c.DaysBefore, low, high, isBetter, ref best);
                if (BudgetExceeded)
                {
                    return;
                }
            }
        }

        internal sealed record Combo(int[] Indices, int Size, long Total, int DateDiff);
    }
}
