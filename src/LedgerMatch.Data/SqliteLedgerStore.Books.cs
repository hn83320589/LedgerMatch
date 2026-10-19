using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Reporting;
using LedgerMatch.Data.Mapping;

namespace LedgerMatch.Data;

public sealed partial class SqliteLedgerStore
{
    public IReadOnlyList<BookEntry> GetBookEntries(long accountId) =>
        Query(EntityMaps.BookEntry, "WHERE account_id = @a ORDER BY entry_date, id", ("@a", accountId));

    public BookEntry? GetBookEntry(long id) => Find<BookEntry>(id);

    public BookEntry? GetBookEntryClearedBy(long txnId) =>
        Query(EntityMaps.BookEntry, "WHERE cleared_txn_id = @t", ("@t", txnId)).SingleOrDefault();

    public StatementBalance? GetStatementBalance(long accountId, YearMonth month) =>
        Query(EntityMaps.StatementBalance, "WHERE account_id = @a AND month = @m", ("@a", accountId), ("@m", month)).SingleOrDefault();

    public IReadOnlyList<YearMonth> GetActiveMonths()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "SELECT posting_period FROM bank_txn UNION SELECT substr(issue_date, 1, 7) FROM receipt ORDER BY 1;");
        using var reader = command.ExecuteReader();
        var months = new List<YearMonth>();
        while (reader.Read())
        {
            months.Add(YearMonth.Parse(reader.GetString(0)));
        }

        return months;
    }

    public ReconciliationData GetReconciliationData(long accountId, YearMonth month)
    {
        var account = GetAccount(accountId) ?? throw new LedgerException($"找不到帳戶 #{accountId}。");
        var transactions = Query(EntityMaps.BankTransaction, "WHERE account_id = @a AND posting_period <= @m", ("@a", accountId), ("@m", month));
        var receipts = Query(EntityMaps.Receipt, "WHERE account_id = @a AND issue_date <= @d", ("@a", accountId), ("@d", month.LastDay));
        var matches = Query(
            EntityMaps.Match,
            "WHERE period <= @m AND deposit_txn_id IN (SELECT id FROM bank_txn WHERE account_id = @a)",
            ("@a", accountId),
            ("@m", month));
        var links = Query(
                EntityMaps.MatchLink,
                "WHERE period <= @m AND match_id IN (SELECT m.id FROM match m JOIN bank_txn t ON t.id = m.deposit_txn_id WHERE t.account_id = @a)",
                ("@a", accountId),
                ("@m", month))
            .ToLookup(l => l.MatchId, l => l.ReceiptId);
        var entries = Query(EntityMaps.BookEntry, "WHERE account_id = @a AND entry_date <= @d", ("@a", accountId), ("@d", month.LastDay));

        return new ReconciliationData(
            account,
            month,
            transactions,
            receipts,
            matches.Select(m => new MatchWithLinks(m, links[m.Id].ToList())).ToList(),
            entries,
            GetStatementBalance(accountId, month));
    }
}
