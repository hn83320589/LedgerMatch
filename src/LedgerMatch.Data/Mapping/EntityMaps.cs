using System.Text.Json;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Data.Mapping;

internal static class EntityMaps
{
    public static readonly TableMap<MappingProfile> Profile = new(
        "mapping_profile",
        [
            ("id", e => e.Id),
            ("name", e => e.Name),
            ("kind", e => e.Kind),
            ("config_json", e => LedgerJson.Serialize(e)),
        ],
        r => JsonSerializer.Deserialize<MappingProfile>(r.Str("config_json"), LedgerJson.Options)! with { Id = r.Long("id"), Name = r.Str("name") });

    public static readonly TableMap<Account> Account = new(
        "account",
        [
            ("id", e => e.Id),
            ("name", e => e.Name),
            ("profile_id", e => e.ProfileId),
            ("opening_balance_cents", e => e.OpeningBalance),
            ("opening_date", e => e.OpeningDate),
        ],
        r => new Account(r.Long("id"), r.Str("name"), r.Long("profile_id"), r.Money("opening_balance_cents"), r.Date("opening_date")));

    public static readonly TableMap<ImportBatch> ImportBatch = new(
        "import_batch",
        [
            ("id", e => e.Id),
            ("kind", e => e.Kind),
            ("account_id", e => e.AccountId),
            ("file_name", e => e.FileName),
            ("file_sha256", e => e.FileSha256),
            ("imported_at", e => e.ImportedAt),
            ("added", e => e.Added),
            ("skipped", e => e.Skipped),
            ("errors", e => e.Errors),
            ("pending", e => e.Pending),
        ],
        r => new ImportBatch(
            r.Long("id"), r.Enum<ImportKind>("kind"), r.NLong("account_id"), r.Str("file_name"), r.Str("file_sha256"),
            r.Timestamp("imported_at"), r.Int("added"), r.Int("skipped"), r.Int("errors"), r.Int("pending")));

    public static readonly TableMap<BankTransaction> BankTransaction = new(
        "bank_txn",
        [
            ("id", e => e.Id),
            ("account_id", e => e.AccountId),
            ("txn_date", e => e.TxnDate),
            ("posting_period", e => e.PostingPeriod),
            ("amount_cents", e => e.Amount),
            ("description", e => e.Description),
            ("serial", e => e.Serial),
            ("note", e => e.Note),
            ("dedup_key", e => e.DedupKey),
            ("import_batch_id", e => e.ImportBatchId),
            ("source_line", e => e.SourceLine),
            ("category_id", e => e.CategoryId),
            ("classified_by", e => e.ClassifiedBy),
            ("book_confirmed", e => e.BookConfirmed),
        ],
        ReadBankTransaction);

    public static readonly TableMap<PendingItem> PendingItem = new(
        "pending_item",
        [
            ("id", e => e.Id),
            ("account_id", e => e.AccountId),
            ("txn_date", e => e.TxnDate),
            ("amount_cents", e => e.Amount),
            ("description", e => e.Description),
            ("serial", e => e.Serial),
            ("note", e => e.Note),
            ("dedup_key", e => e.DedupKey),
            ("import_batch_id", e => e.ImportBatchId),
            ("source_line", e => e.SourceLine),
            ("status", e => e.Status),
            ("posted_txn_id", e => e.PostedTxnId),
        ],
        r => new PendingItem(
            r.Long("id"), r.Long("account_id"), r.Date("txn_date"), r.Money("amount_cents"), r.Str("description"), r.NStr("serial"),
            r.NStr("note"), r.Str("dedup_key"), r.Long("import_batch_id"), r.Int("source_line"), r.Enum<PendingStatus>("status"), r.NLong("posted_txn_id")));

    public static readonly TableMap<Period> Period = new(
        "period",
        [
            ("id", e => e.Id),
            ("month", e => e.Month),
            ("is_closed", e => e.IsClosed),
            ("closed_at", e => e.ClosedAt),
        ],
        r => new Period(r.Long("id"), r.Month("month"), r.Bool("is_closed"), r.NTimestamp("closed_at")));

    public static readonly TableMap<PeriodEvent> PeriodEvent = new(
        "period_event",
        [
            ("id", e => e.Id),
            ("month", e => e.Month),
            ("action", e => e.Action),
            ("reason", e => e.Reason),
            ("at", e => e.At),
        ],
        r => new PeriodEvent(r.Long("id"), r.Month("month"), r.Enum<PeriodAction>("action"), r.NStr("reason"), r.Timestamp("at")));

    public static readonly TableMap<Receipt> Receipt = new(
        "receipt",
        [
            ("id", e => e.Id),
            ("account_id", e => e.AccountId),
            ("issue_date", e => e.IssueDate),
            ("customer", e => e.Customer),
            ("receipt_no", e => e.ReceiptNo),
            ("amount_cents", e => e.Amount),
            ("note", e => e.Note),
            ("import_batch_id", e => e.ImportBatchId),
        ],
        r => new Receipt(
            r.Long("id"), r.Long("account_id"), r.Date("issue_date"), r.Str("customer"), r.Str("receipt_no"), r.Money("amount_cents"),
            r.NStr("note"), r.NLong("import_batch_id")));

    public static readonly TableMap<Match> Match = new(
        "match",
        [
            ("id", e => e.Id),
            ("deposit_txn_id", e => e.DepositTxnId),
            ("period", e => e.Period),
            ("kind", e => e.Kind),
            ("confidence", e => e.Confidence),
            ("fee_cents", e => e.Fee),
            ("note", e => e.Note),
        ],
        r => new Match(
            r.Long("id"), r.Long("deposit_txn_id"), r.Month("period"), r.Enum<MatchKind>("kind"), r.Enum<MatchConfidence>("confidence"),
            r.Money("fee_cents"), r.NStr("note")));

    public static readonly TableMap<MatchLink> MatchLink = new(
        "match_link",
        [
            ("id", e => e.Id),
            ("match_id", e => e.MatchId),
            ("receipt_id", e => e.ReceiptId),
            ("period", e => e.Period),
        ],
        r => new MatchLink(r.Long("id"), r.Long("match_id"), r.Long("receipt_id"), r.Month("period")));

    public static readonly TableMap<Setting> Setting = new(
        "setting",
        [
            ("id", e => e.Id),
            ("key", e => e.Key),
            ("value_json", e => e.ValueJson),
        ],
        r => new Setting(r.Long("id"), r.Str("key"), r.Str("value_json")));

    public static readonly IReadOnlyDictionary<Type, ITableMap> ByType =
        new ITableMap[] { Profile, Account, ImportBatch, BankTransaction, PendingItem, Period, PeriodEvent, Receipt, Match, MatchLink, Setting }
            .ToDictionary(m => m.EntityType);

    public static BankTransaction ReadBankTransaction(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.Long("id"), r.Long("account_id"), r.Date("txn_date"), r.Month("posting_period"), r.Money("amount_cents"), r.Str("description"),
        r.NStr("serial"), r.NStr("note"), r.Str("dedup_key"), r.NLong("import_batch_id"), r.NInt("source_line"), r.NLong("category_id"),
        r.Enum<ClassificationSource>("classified_by"), r.Bool("book_confirmed"));
}
