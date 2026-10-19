using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Setup;

namespace LedgerMatch.Core.Matching;

public sealed record MatchingRunSummary(int Confirmed, int Suggested, IReadOnlyList<MatchingWarning> Warnings);

/// <summary>
/// Runs the engine for one account and month and manages match lifecycle. A whole engine run is one
/// undo step; it is computed in memory and committed at the end, so cancelling leaves no partial result.
/// </summary>
public sealed class MatchingService(ILedgerStore store, ILedgerQueries queries, UndoRedoService history, SettingsService settings)
{
    public Task<MatchingRunSummary> RunAsync(
        long accountId, YearMonth period, IProgress<MatchingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(accountId, period, progress, cancellationToken), cancellationToken);

    public MatchingRunSummary Run(long accountId, YearMonth period, IProgress<MatchingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var account = queries.GetAccount(accountId) ?? throw new LedgerException($"找不到帳戶 #{accountId}。");
        PeriodGuard.EnsureOpen(period, store.GetClosedPeriods(), $"{account.Name} 的配對");
        var config = settings.GetMatchingSettings();

        var deposits = queries.GetUnmatchedDeposits(accountId, period);
        // No lower bound: a receipt number in the description matches regardless of date.
        var receipts = queries.GetUnmatchedReceipts(accountId, DateOnly.MinValue, period.LastDay.AddDays(config.ExactDateToleranceDays));

        var result = MatchingEngine.Run(
            deposits.Select(d => new DepositCandidate(d.Id, d.TxnDate, d.Amount, d.Description, d.DedupKey)),
            receipts.Select(r => new ReceiptCandidate(r.Id, r.IssueDate, r.Amount, r.ReceiptNo)),
            config,
            progress,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var depositsById = deposits.ToDictionary(d => d.Id);
        var changes = new ChangeSetBuilder($"自動配對 {account.Name} {period}");
        var linkCount = result.Proposals.Sum(p => p.ReceiptIds.Count);
        var nextId = result.Proposals.Count == 0 ? 0 : store.ReserveIds(result.Proposals.Count + linkCount);
        foreach (var p in result.Proposals)
        {
            var deposit = depositsById[p.DepositId];
            var note = p.FeasibleCombinations > 1 ? $"共有 {p.FeasibleCombinations} 個可行組合" : null;
            if (p.CandidatesTruncated)
            {
                note = (note is null ? string.Empty : note + "；") + "候選已截斷";
            }

            var match = new Match(nextId++, deposit.Id, deposit.PostingPeriod, p.Kind, p.Confidence, p.Fee, note);
            changes.Insert(match);
            foreach (var receiptId in p.ReceiptIds)
            {
                changes.Insert(new MatchLink(nextId++, match.Id, receiptId, match.Period));
            }
        }

        history.Execute(changes.Build());
        return new MatchingRunSummary(
            result.Proposals.Count(p => p.Confidence == MatchConfidence.Confirmed),
            result.Proposals.Count(p => p.Confidence == MatchConfidence.Suggested),
            result.Warnings);
    }

    /// <summary>Confirms suggestions; one undo step for the whole selection.</summary>
    public int Confirm(IEnumerable<long> matchIds)
    {
        var matches = Load(matchIds).Where(m => m.Confidence == MatchConfidence.Suggested).ToList();
        var changes = new ChangeSetBuilder($"確認配對（{matches.Count} 筆）");
        foreach (var m in matches)
        {
            changes.Update(m, m with { Confidence = MatchConfidence.Confirmed });
        }

        history.Execute(changes.Build());
        return matches.Count;
    }

    /// <summary>Removes matches (confirmed or suggested) and their receipt links; one undo step.</summary>
    public int Unmatch(IEnumerable<long> matchIds, string operation = "解除配對")
    {
        var matches = Load(matchIds);
        var changes = new ChangeSetBuilder($"{operation}（{matches.Count} 筆）");
        foreach (var m in matches)
        {
            foreach (var link in queries.GetMatchLinks(m.Id))
            {
                changes.Delete(link);
            }

            changes.Delete(m);
        }

        history.Execute(changes.Build());
        return matches.Count;
    }

    /// <summary>Discards suggestions only; confirmed matches in the selection are left alone.</summary>
    public int Reject(IEnumerable<long> matchIds) =>
        Unmatch(Load(matchIds).Where(m => m.Confidence == MatchConfidence.Suggested).Select(m => m.Id), "拒絕建議配對");

    /// <summary>
    /// Pairs one deposit with receipts chosen by the user. Amounts must be equal, or the receipts may exceed the
    /// deposit when the user accepts the difference as a fee entry.
    /// </summary>
    public Match ManualMatch(long depositTxnId, IReadOnlyCollection<long> receiptIds, bool acceptDifferenceAsFee)
    {
        var deposit = queries.GetTransaction(depositTxnId) ?? throw new LedgerException($"找不到交易 #{depositTxnId}。");
        if (!deposit.IsDeposit)
        {
            throw new LedgerException("只有入帳（金額大於 0）可以與收款單配對。");
        }

        if (queries.GetMatchForDeposit(deposit.Id) is not null)
        {
            throw new LedgerException("這筆入帳已有配對，請先解除。");
        }

        if (receiptIds.Count == 0 || receiptIds.Distinct().Count() != receiptIds.Count)
        {
            throw new LedgerException("請選擇至少一張不重複的收款單。");
        }

        var receipts = receiptIds.Select(id => queries.GetReceipt(id) ?? throw new LedgerException($"找不到收款單 #{id}。")).ToList();
        foreach (var r in receipts)
        {
            if (r.AccountId != deposit.AccountId)
            {
                throw new LedgerException($"收款單 {r.ReceiptNo} 屬於其他帳戶。");
            }

            if (queries.GetMatchForReceipt(r.Id) is not null)
            {
                throw new LedgerException($"收款單 {r.ReceiptNo} 已有配對，請先解除。");
            }
        }

        var total = receipts.Sum(r => r.Amount);
        var difference = total - deposit.Amount;
        if (difference.IsNegative)
        {
            throw new LedgerException($"收款單合計 {total} 少於入帳金額 {deposit.Amount}，無法配對。");
        }

        if (difference.IsPositive && !acceptDifferenceAsFee)
        {
            throw new LedgerException($"收款單合計 {total} 比入帳金額多 {difference}；若差額是手續費，請勾選「差額列為手續費」。");
        }

        var id = store.ReserveIds(1 + receipts.Count);
        var match = new Match(id, deposit.Id, deposit.PostingPeriod, MatchKind.Manual, MatchConfidence.Confirmed, difference, null);
        var changes = new ChangeSetBuilder($"人工配對 {deposit.TxnDate:yyyy-MM-dd} {deposit.Amount}").Insert(match);
        foreach (var r in receipts)
        {
            changes.Insert(new MatchLink(++id, match.Id, r.Id, match.Period));
        }

        history.Execute(changes.Build());
        return match;
    }

    private List<Match> Load(IEnumerable<long> matchIds) =>
        matchIds.Distinct().Select(id => queries.GetMatch(id) ?? throw new LedgerException($"找不到配對 #{id}。")).ToList();
}
