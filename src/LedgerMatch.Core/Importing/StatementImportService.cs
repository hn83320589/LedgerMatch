using System.Security.Cryptography;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Importing;

public sealed record ImportSummary(
    long BatchId,
    int Added,
    int SkippedDuplicates,
    int Pending,
    IReadOnlyList<RowError> Errors);

public enum ImportStage
{
    Parsing,
    Planning,
    Saving,
    Done,
}

public sealed record ImportProgress(ImportStage Stage, int Processed, int Total);

/// <summary>
/// Imports a bank statement file for one account. Parsing and planning happen in memory; the result is
/// committed in one transaction, so cancelling before the save leaves the database untouched.
/// Imports are audited but are not undo steps (see README "已知限制").
/// </summary>
public sealed class StatementImportService(ILedgerStore store, ILedgerQueries queries, LedgerCommandExecutor executor, TimeProvider time)
{
    public Task<ImportSummary> ImportAsync(
        long accountId,
        Stream file,
        string fileName,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Import(accountId, file, fileName, progress, cancellationToken), cancellationToken);

    public ImportSummary Import(
        long accountId,
        Stream file,
        string fileName,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var account = queries.GetAccount(accountId) ?? throw new LedgerException($"找不到帳戶 #{accountId}。");
        var profile = queries.GetProfile(account.ProfileId) ?? throw new LedgerException($"帳戶「{account.Name}」沒有綁定映射設定檔。");

        using var buffer = new MemoryStream();
        file.CopyTo(buffer);
        var sha256 = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        buffer.Position = 0;

        progress?.Report(new ImportProgress(ImportStage.Parsing, 0, 0));
        var parsed = StatementFileParser.Parse(buffer, profile);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new ImportProgress(ImportStage.Planning, 0, parsed.Rows.Count));
        var plan = Plan(account, profile, parsed.Rows);
        cancellationToken.ThrowIfCancellationRequested();

        var total = plan.ToInsert.Count + plan.ToPending.Count;
        var firstId = store.ReserveIds(total + 1);
        var batch = new ImportBatch(
            firstId, ImportKind.Statement, account.Id, fileName, sha256, time.GetUtcNow(),
            plan.ToInsert.Count, plan.Duplicates.Count, parsed.Errors.Count, plan.ToPending.Count);

        var changes = new ChangeSetBuilder($"匯入對帳單 {fileName}").Insert(batch);
        var nextId = firstId + 1;
        foreach (var planned in plan.ToInsert)
        {
            changes.Insert(NewTransaction(nextId++, account.Id, batch.Id, planned));
            ReportEvery(progress, changes.Count - 1, total, cancellationToken);
        }

        foreach (var planned in plan.ToPending)
        {
            var row = planned.Row;
            changes.Insert(new PendingItem(
                nextId++, account.Id, row.Date, row.Amount, row.Description, row.Serial, row.Note, planned.DedupKey,
                batch.Id, row.LineNumber, PendingStatus.Pending, null));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ImportProgress(ImportStage.Saving, total, total));
        executor.Execute(changes.Build());
        progress?.Report(new ImportProgress(ImportStage.Done, total, total));

        return new ImportSummary(batch.Id, plan.ToInsert.Count, plan.Duplicates.Count, plan.ToPending.Count, parsed.Errors);
    }

    internal static BankTransaction NewTransaction(long id, long accountId, long? batchId, PlannedRow planned)
    {
        var row = planned.Row;
        return new BankTransaction(
            id, accountId, row.Date, YearMonth.Of(row.Date), row.Amount, row.Description, row.Serial, row.Note, planned.DedupKey,
            batchId, row.LineNumber, null, ClassificationSource.None,
            BookConfirmed: row.Amount.IsNegative);
    }

    private ImportPlan Plan(Account account, MappingProfile profile, IReadOnlyList<ParsedStatementRow> rows)
    {
        if (rows.Count == 0)
        {
            return new ImportPlan([], [], []);
        }

        var from = rows.Min(r => r.Date);
        var to = rows.Max(r => r.Date);
        return ImportPlanner.Plan(
            rows,
            profile.Serial is not null,
            queries.GetTransactionDedupKeys(account.Id, from, to),
            queries.GetPendingDedupKeys(account.Id, from, to),
            store.GetClosedPeriods());
    }

    private static void ReportEvery(IProgress<ImportProgress>? progress, int processed, int total, CancellationToken cancellationToken)
    {
        if (processed % 500 == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ImportProgress(ImportStage.Planning, processed, total));
        }
    }
}
