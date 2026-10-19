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

    public IReadOnlySet<string> GetTransactionDedupKeys(long accountId, DateOnly from, DateOnly to) =>
        Keys("SELECT dedup_key FROM bank_txn WHERE account_id = @a AND txn_date BETWEEN @from AND @to;", accountId, from, to);

    public IReadOnlySet<string> GetPendingDedupKeys(long accountId, DateOnly from, DateOnly to) =>
        Keys("SELECT dedup_key FROM pending_item WHERE account_id = @a AND txn_date BETWEEN @from AND @to;", accountId, from, to);

    public IReadOnlyList<BankTransaction> GetTransactions(long accountId) =>
        Query(EntityMaps.BankTransaction, "WHERE account_id = @a ORDER BY txn_date, id", ("@a", accountId));

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
