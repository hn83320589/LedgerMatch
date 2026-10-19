using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Data.Mapping;
using Microsoft.Data.Sqlite;

namespace LedgerMatch.Data;

/// <summary>SQLite implementation of the ledger ports. Every call opens its own pooled connection, so it is safe from background tasks.</summary>
public sealed partial class SqliteLedgerStore(LedgerDatabase database) : ILedgerStore
{
    private const int SqliteConstraint = 19;

    public LedgerDatabase Database => database;

    public long ReserveIds(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "UPDATE id_sequence SET next_id = next_id + @n WHERE name = 'global' RETURNING next_id - @n;", ("@n", count));
        return (long)command.ExecuteScalar()!;
    }

    public IReadOnlySet<YearMonth> GetClosedPeriods()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("SELECT month FROM period WHERE is_closed = 1;");
        using var reader = command.ExecuteReader();
        var result = new HashSet<YearMonth>();
        while (reader.Read())
        {
            result.Add(YearMonth.Parse(reader.GetString(0)));
        }

        return result;
    }

    public void Commit(ChangeSet changes, IReadOnlyList<PendingAuditEntry> audit)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            using (var cache = new CommandCache(connection, tx))
            {
                foreach (var change in changes.Changes)
                {
                    if (!EntityMaps.ByType.TryGetValue(change.EntityType, out var map))
                    {
                        throw new InvalidOperationException($"No table mapping for {change.EntityType.Name}.");
                    }

                    switch (change.Kind)
                    {
                        case ChangeKind.Insert:
                            map.Insert(cache, change.After!);
                            break;
                        case ChangeKind.Update:
                            map.Update(cache, change.Before!, change.After!);
                            break;
                        case ChangeKind.Delete:
                            map.Delete(cache, change.Before!);
                            break;
                    }
                }

                AppendAudit(connection, tx, audit);
            }

            tx.Commit();
        }
        catch (SqliteException ex) when (ex.Message.Contains("PERIOD_CLOSED", StringComparison.Ordinal))
        {
            throw new LedgerException("資料庫拒絕修改已結帳月份的資料。", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            throw new ConcurrencyConflictException($"資料與目前狀態衝突，這次修改沒有寫入（{ex.Message}）。", ex);
        }
    }

    public IReadOnlyList<AuditEntry> GetAuditEntries(long afterSequence, int take)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "SELECT * FROM audit_log WHERE seq > @after ORDER BY seq LIMIT @take;", ("@after", afterSequence), ("@take", take));
        using var reader = command.ExecuteReader();
        var result = new List<AuditEntry>();
        while (reader.Read())
        {
            result.Add(new AuditEntry(
                reader.Long("seq"), reader.Timestamp("at"), reader.Str("operation"), reader.Str("entity_type"), reader.Long("entity_id"),
                reader.Str("action"), reader.NStr("before_json"), reader.NStr("after_json"), reader.Str("prev_hash"), reader.Str("hash")));
        }

        return result;
    }

    /// <summary>Loads one entity by id, or null.</summary>
    public T? Find<T>(long id) where T : class, IEntity
    {
        var map = (TableMap<T>)EntityMaps.ByType[typeof(T)];
        return Query(map, "WHERE id = @id", ("@id", id)).SingleOrDefault();
    }

    public IReadOnlyList<T> All<T>() where T : class, IEntity
    {
        var map = (TableMap<T>)EntityMaps.ByType[typeof(T)];
        return Query(map, "ORDER BY id");
    }

    internal List<T> Query<T>(TableMap<T> map, string whereAndOrder, params (string Name, object? Value)[] parameters) where T : class, IEntity
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"{map.SelectSql} {whereAndOrder};", parameters);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            result.Add(map.Read(reader));
        }

        return result;
    }

    private static void AppendAudit(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<PendingAuditEntry> audit)
    {
        if (audit.Count == 0)
        {
            return;
        }

        long seq = 0;
        var prevHash = AuditHasher.GenesisHash;
        using (var last = connection.Command("SELECT seq, hash FROM audit_log ORDER BY seq DESC LIMIT 1;"))
        {
            last.Transaction = tx;
            using var reader = last.ExecuteReader();
            if (reader.Read())
            {
                seq = reader.GetInt64(0);
                prevHash = reader.GetString(1);
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO audit_log (seq, at, operation, entity_type, entity_id, action, before_json, after_json, prev_hash, hash) " +
            "VALUES (@seq, @at, @op, @type, @eid, @action, @before, @after, @prev, @hash);";
        foreach (var name in new[] { "@seq", "@at", "@op", "@type", "@eid", "@action", "@before", "@after", "@prev", "@hash" })
        {
            insert.Parameters.Add(new SqliteParameter(name, null));
        }

        insert.Prepare();
        foreach (var entry in audit)
        {
            seq++;
            var hash = AuditHasher.Compute(prevHash, seq, entry);
            insert.Parameters["@seq"].Value = seq;
            insert.Parameters["@at"].Value = SqlValues.ToDb(entry.At);
            insert.Parameters["@op"].Value = entry.Operation;
            insert.Parameters["@type"].Value = entry.EntityType;
            insert.Parameters["@eid"].Value = entry.EntityId;
            insert.Parameters["@action"].Value = entry.Action;
            insert.Parameters["@before"].Value = SqlValues.ToDb(entry.BeforeJson);
            insert.Parameters["@after"].Value = SqlValues.ToDb(entry.AfterJson);
            insert.Parameters["@prev"].Value = prevHash;
            insert.Parameters["@hash"].Value = hash;
            insert.ExecuteNonQuery();
            prevHash = hash;
        }
    }
}
