using System.Security.Cryptography;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Receivables;

public sealed record ReceiptImportSummary(long BatchId, int Added, int SkippedDuplicates, IReadOnlyList<RowError> Errors);

/// <summary>Receipt (收款單) maintenance. Manual edits are undo steps; imports are audited but not undoable.</summary>
public sealed class ReceiptService(ILedgerStore store, ILedgerQueries queries, LedgerCommandExecutor executor, UndoRedoService history, TimeProvider time)
{
    public Receipt Add(long accountId, DateOnly issueDate, string customer, string receiptNo, Money amount, string? note)
    {
        var receipt = new Receipt(0, accountId, issueDate, customer.Trim(), receiptNo.Trim(), amount, NullIfBlank(note), null);
        Validate(receipt);
        receipt = receipt with { Id = store.ReserveIds(1) };
        history.Execute(new ChangeSetBuilder($"新增收款單 {receipt.ReceiptNo}").Insert(receipt).Build());
        return receipt;
    }

    public Receipt Update(Receipt receipt)
    {
        var existing = queries.GetReceipt(receipt.Id) ?? throw new LedgerException($"找不到收款單 #{receipt.Id}。");
        receipt = receipt with { Customer = receipt.Customer.Trim(), ReceiptNo = receipt.ReceiptNo.Trim(), Note = NullIfBlank(receipt.Note) };
        Validate(receipt);

        var matched = queries.GetMatchForReceipt(receipt.Id) is not null;
        if (matched && (receipt.Amount != existing.Amount || receipt.IssueDate != existing.IssueDate || receipt.AccountId != existing.AccountId))
        {
            throw new LedgerException($"收款單 {existing.ReceiptNo} 已配對，修改金額、日期或帳戶前請先解除配對。");
        }

        history.Execute(new ChangeSetBuilder($"編輯收款單 {receipt.ReceiptNo}").Update(existing, receipt).Build());
        return receipt;
    }

    public void Delete(long receiptId)
    {
        var existing = queries.GetReceipt(receiptId) ?? throw new LedgerException($"找不到收款單 #{receiptId}。");
        if (queries.GetMatchForReceipt(receiptId) is not null)
        {
            throw new LedgerException($"收款單 {existing.ReceiptNo} 已配對，請先解除配對再刪除。");
        }

        history.Execute(new ChangeSetBuilder($"刪除收款單 {existing.ReceiptNo}").Delete(existing).Build());
    }

    /// <summary>
    /// Imports receipts. A receipt number that already exists with identical content is a skipped duplicate;
    /// with different content, or dated in a closed month, the row is reported as an error. When the profile has
    /// an account column, each row goes to the account it names; otherwise to <paramref name="defaultAccountId"/>.
    /// </summary>
    public ReceiptImportSummary Import(long? defaultAccountId, long profileId, Stream file, string fileName)
    {
        if (defaultAccountId is { } id0 && queries.GetAccount(id0) is null)
        {
            throw new LedgerException($"找不到帳戶 #{id0}。");
        }

        var profile = queries.GetProfile(profileId) ?? throw new LedgerException($"找不到設定檔 #{profileId}。");
        var accountsByName = queries.GetAccounts().ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase);

        using var buffer = new MemoryStream();
        file.CopyTo(buffer);
        var sha256 = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        buffer.Position = 0;
        var parsed = ReceiptFileParser.Parse(buffer, profile);

        var closed = store.GetClosedPeriods();
        var errors = parsed.Errors.ToList();
        var toAdd = new List<(ParsedReceiptRow Row, long AccountId)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var row in parsed.Rows)
        {
            long accountId;
            if (row.AccountName is { } name)
            {
                if (!accountsByName.TryGetValue(name, out accountId))
                {
                    errors.Add(new RowError(row.LineNumber, $"找不到名為「{name}」的帳戶"));
                    continue;
                }
            }
            else if (defaultAccountId is { } fallback)
            {
                accountId = fallback;
            }
            else
            {
                errors.Add(new RowError(row.LineNumber, "未指定入帳帳戶"));
                continue;
            }

            var existing = queries.GetReceiptByNumber(row.ReceiptNo);
            if (existing is not null || !seen.Add(row.ReceiptNo))
            {
                var same = existing is null
                    || (existing.AccountId == accountId && existing.IssueDate == row.IssueDate && existing.Amount == row.Amount && existing.Customer == row.Customer);
                if (same)
                {
                    skipped++;
                }
                else
                {
                    errors.Add(new RowError(row.LineNumber, $"單據編號 {row.ReceiptNo} 已存在，但內容不同"));
                }
            }
            else if (closed.Contains(YearMonth.Of(row.IssueDate)))
            {
                errors.Add(new RowError(row.LineNumber, $"開立日期 {row.IssueDate:yyyy-MM-dd} 落在已結帳月份"));
            }
            else
            {
                toAdd.Add((row, accountId));
            }
        }

        errors.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));
        var id = store.ReserveIds(1 + toAdd.Count);
        var batch = new ImportBatch(id, ImportKind.Receipt, defaultAccountId, fileName, sha256, time.GetUtcNow(), toAdd.Count, skipped, errors.Count, 0);
        var changes = new ChangeSetBuilder($"匯入收款單 {fileName}").Insert(batch);
        foreach (var (row, accountId) in toAdd)
        {
            changes.Insert(new Receipt(++id, accountId, row.IssueDate, row.Customer, row.ReceiptNo, row.Amount, row.Note, batch.Id));
        }

        executor.Execute(changes.Build());
        return new ReceiptImportSummary(batch.Id, toAdd.Count, skipped, errors);
    }

    private void Validate(Receipt receipt)
    {
        if (queries.GetAccount(receipt.AccountId) is null)
        {
            throw new LedgerException("請選擇收款單所屬帳戶。");
        }

        if (receipt.Customer.Length == 0)
        {
            throw new LedgerException("客戶名稱不可空白。");
        }

        if (receipt.ReceiptNo.Length == 0)
        {
            throw new LedgerException("單據編號不可空白。");
        }

        if (!receipt.Amount.IsPositive)
        {
            throw new LedgerException("收款單金額必須大於零。");
        }

        if (queries.GetReceiptByNumber(receipt.ReceiptNo) is { } other && other.Id != receipt.Id)
        {
            throw new LedgerException($"單據編號 {receipt.ReceiptNo} 已存在。");
        }
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
