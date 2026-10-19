using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Data.Mapping;

namespace LedgerMatch.Data;

public sealed partial class SqliteLedgerStore : ILedgerQueries
{
    public IReadOnlyList<MappingProfile> GetProfiles() => Query(EntityMaps.Profile, "ORDER BY name");

    public MappingProfile? GetProfile(long id) => Find<MappingProfile>(id);

    public IReadOnlyList<Account> GetAccounts() => Query(EntityMaps.Account, "ORDER BY name");

    public Account? GetAccount(long id) => Find<Account>(id);

    public Setting? GetSetting(string key) => Query(EntityMaps.Setting, "WHERE key = @k", ("@k", key)).SingleOrDefault();

    public IReadOnlySet<string> GetTransactionDedupKeys(long accountId, DateOnly from, DateOnly to) =>
        Keys("SELECT dedup_key FROM bank_txn WHERE account_id = @a AND txn_date BETWEEN @from AND @to;", accountId, from, to);

    public IReadOnlySet<string> GetPendingDedupKeys(long accountId, DateOnly from, DateOnly to) =>
        Keys("SELECT dedup_key FROM pending_item WHERE account_id = @a AND txn_date BETWEEN @from AND @to;", accountId, from, to);

    public BankTransaction? GetTransaction(long id) => Find<BankTransaction>(id);

    public IReadOnlyList<BankTransaction> GetTransactions(long accountId) =>
        Query(EntityMaps.BankTransaction, "WHERE account_id = @a ORDER BY txn_date, id", ("@a", accountId));

    public IReadOnlyList<BankTransaction> GetUnmatchedDeposits(long accountId, YearMonth period) =>
        Query(
            EntityMaps.BankTransaction,
            "WHERE account_id = @a AND posting_period = @p AND amount_cents > 0 " +
            "AND NOT EXISTS (SELECT 1 FROM match m WHERE m.deposit_txn_id = bank_txn.id) ORDER BY txn_date, id",
            ("@a", accountId),
            ("@p", period));

    public Receipt? GetReceipt(long id) => Find<Receipt>(id);

    public Receipt? GetReceiptByNumber(string receiptNo) =>
        Query(EntityMaps.Receipt, "WHERE receipt_no = @n", ("@n", receiptNo)).SingleOrDefault();

    public IReadOnlyList<Receipt> GetReceipts(long accountId) =>
        Query(EntityMaps.Receipt, "WHERE account_id = @a ORDER BY issue_date, receipt_no", ("@a", accountId));

    public IReadOnlyList<Receipt> GetUnmatchedReceipts(long accountId, DateOnly from, DateOnly to) =>
        Query(
            EntityMaps.Receipt,
            "WHERE account_id = @a AND issue_date BETWEEN @from AND @to " +
            "AND NOT EXISTS (SELECT 1 FROM match_link l WHERE l.receipt_id = receipt.id) ORDER BY issue_date, receipt_no",
            ("@a", accountId),
            ("@from", from),
            ("@to", to));

    public Match? GetMatch(long id) => Find<Match>(id);

    public Match? GetMatchForDeposit(long depositTxnId) =>
        Query(EntityMaps.Match, "WHERE deposit_txn_id = @t", ("@t", depositTxnId)).SingleOrDefault();

    public Match? GetMatchForReceipt(long receiptId) =>
        Query(EntityMaps.Match, "WHERE id = (SELECT match_id FROM match_link WHERE receipt_id = @r)", ("@r", receiptId)).SingleOrDefault();

    public IReadOnlyList<MatchLink> GetMatchLinks(long matchId) =>
        Query(EntityMaps.MatchLink, "WHERE match_id = @m ORDER BY id", ("@m", matchId));

    public IReadOnlyList<Match> GetMatches(long accountId, YearMonth period) =>
        Query(
            EntityMaps.Match,
            "WHERE period = @p AND deposit_txn_id IN (SELECT id FROM bank_txn WHERE account_id = @a) ORDER BY id",
            ("@a", accountId),
            ("@p", period));

    private HashSet<string> Keys(string sql, long accountId, DateOnly from, DateOnly to)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(sql, ("@a", accountId), ("@from", from), ("@to", to));
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }
}
