using System.Text;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Data.Mapping;

namespace LedgerMatch.Data;

public sealed partial class SqliteLedgerStore
{
    public IReadOnlyList<Category> GetCategories() => Query(EntityMaps.Category, "ORDER BY is_system DESC, name");

    public Category? GetCategory(long id) => Find<Category>(id);

    public bool IsCategoryInUse(long categoryId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "SELECT EXISTS (SELECT 1 FROM bank_txn WHERE category_id = @c) OR EXISTS (SELECT 1 FROM split_line WHERE category_id = @c) " +
            "OR EXISTS (SELECT 1 FROM rule WHERE category_id = @c);",
            ("@c", categoryId));
        return (long)command.ExecuteScalar()! != 0;
    }

    public IReadOnlyList<SplitLine> GetSplitLines(long txnId) => Query(EntityMaps.SplitLine, "WHERE txn_id = @t ORDER BY seq", ("@t", txnId));

    public IReadOnlyList<Rule> GetRules() => Query(EntityMaps.Rule, "ORDER BY priority, id");

    public Rule? GetRule(long id) => Find<Rule>(id);

    public IReadOnlyList<TransactionRow> FindTransactions(TransactionFilter filter, TransactionCursor? after = null, int take = int.MaxValue)
    {
        var (where, parameters) = BuildFilter(filter);
        if (after is not null)
        {
            where.Append(" AND (t.txn_date > @cd OR (t.txn_date = @cd AND t.id > @cid))");
            parameters.Add(("@cd", after.Date));
            parameters.Add(("@cid", after.Id));
        }

        parameters.Add(("@take", take));
        var sql =
            "SELECT t.*, m.id AS match_id, m.confidence AS match_confidence, c.name AS category_name, " +
            "EXISTS (SELECT 1 FROM split_line s WHERE s.txn_id = t.id) AS is_split " +
            $"FROM bank_txn t LEFT JOIN match m ON m.deposit_txn_id = t.id LEFT JOIN category c ON c.id = t.category_id WHERE {where} " +
            "ORDER BY t.txn_date, t.id LIMIT @take;";

        using var connection = database.OpenConnection();
        using var command = connection.Command(sql, [.. parameters]);
        using var reader = command.ExecuteReader();
        var rows = new List<TransactionRow>();
        while (reader.Read())
        {
            rows.Add(new TransactionRow(
                EntityMaps.ReadBankTransaction(reader),
                reader.NLong("match_id"),
                reader.NStr("match_confidence") is { } conf ? Enum.Parse<MatchConfidence>(conf) : null,
                reader.Bool("is_split"),
                reader.NStr("category_name")));
        }

        return rows;
    }

    public int CountTransactions(TransactionFilter filter)
    {
        var (where, parameters) = BuildFilter(filter);
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            $"SELECT count(*) FROM bank_txn t LEFT JOIN match m ON m.deposit_txn_id = t.id WHERE {where};", [.. parameters]);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static (StringBuilder Where, List<(string, object?)> Parameters) BuildFilter(TransactionFilter filter)
    {
        var where = new StringBuilder("1 = 1");
        var parameters = new List<(string, object?)>();

        void Add(string clause, string name, object? value)
        {
            where.Append(" AND ").Append(clause);
            parameters.Add((name, value));
        }

        if (filter.AccountId is { } account)
        {
            Add("t.account_id = @a", "@a", account);
        }

        if (filter.Period is { } period)
        {
            Add("t.posting_period = @p", "@p", period);
        }

        if (filter.From is { } from)
        {
            Add("t.txn_date >= @from", "@from", from);
        }

        if (filter.To is { } to)
        {
            Add("t.txn_date <= @to", "@to", to);
        }

        if (filter.MinAmount is { } min)
        {
            Add("t.amount_cents >= @min", "@min", min);
        }

        if (filter.MaxAmount is { } max)
        {
            Add("t.amount_cents <= @max", "@max", max);
        }

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var pattern = "%" + filter.Keyword.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            Add("(t.description LIKE @kw ESCAPE '\\' OR t.note LIKE @kw ESCAPE '\\' OR t.serial LIKE @kw ESCAPE '\\')", "@kw", pattern);
        }

        where.Append(filter.Status switch
        {
            TransactionStatusFilter.UnmatchedDeposits => " AND t.amount_cents > 0 AND m.id IS NULL",
            TransactionStatusFilter.SuggestedMatches => " AND m.confidence = 'Suggested'",
            TransactionStatusFilter.ConfirmedMatches => " AND m.confidence = 'Confirmed'",
            TransactionStatusFilter.Withdrawals => " AND t.amount_cents < 0",
            TransactionStatusFilter.Unclassified => " AND t.category_id IS NULL AND NOT EXISTS (SELECT 1 FROM split_line s WHERE s.txn_id = t.id)",
            _ => string.Empty,
        });

        return (where, parameters);
    }
}
