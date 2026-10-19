using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Importing;

namespace LedgerMatch.Core.Persistence;

/// <summary>Read side of the ledger used by the application services.</summary>
public interface ILedgerQueries
{
    IReadOnlyList<MappingProfile> GetProfiles();

    MappingProfile? GetProfile(long id);

    IReadOnlyList<Account> GetAccounts();

    Account? GetAccount(long id);

    /// <summary>Dedup keys of the account's transactions whose original date is within the range (inclusive).</summary>
    IReadOnlySet<string> GetTransactionDedupKeys(long accountId, DateOnly from, DateOnly to);

    /// <summary>Dedup keys of the account's pending items (any status) whose date is within the range (inclusive).</summary>
    IReadOnlySet<string> GetPendingDedupKeys(long accountId, DateOnly from, DateOnly to);

    IReadOnlyList<BankTransaction> GetTransactions(long accountId);
}
