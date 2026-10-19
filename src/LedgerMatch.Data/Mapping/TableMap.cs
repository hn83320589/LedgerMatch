using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace LedgerMatch.Data.Mapping;

internal interface ITableMap
{
    Type EntityType { get; }

    string Table { get; }

    void Insert(CommandCache cache, IEntity entity);

    void Update(CommandCache cache, IEntity before, IEntity after);

    void Delete(CommandCache cache, IEntity before);
}

/// <summary>Reuses prepared commands within one transaction so large batches stay fast.</summary>
internal sealed class CommandCache(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
{
    private readonly Dictionary<string, SqliteCommand> _commands = [];

    public SqliteCommand Get(string sql, int parameterCount, string prefix, int beforeCount = 0)
    {
        if (!_commands.TryGetValue(sql, out var command))
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            for (var i = 0; i < parameterCount; i++)
            {
                command.Parameters.Add(new SqliteParameter($"@{prefix}{i}", null));
            }

            for (var i = 0; i < beforeCount; i++)
            {
                command.Parameters.Add(new SqliteParameter($"@b{i}", null));
            }

            command.Prepare();
            _commands[sql] = command;
        }

        return command;
    }

    public void Dispose()
    {
        foreach (var command in _commands.Values)
        {
            command.Dispose();
        }
    }
}

/// <summary>
/// Maps one entity record to one table. Column 0 must be "id". Updates and deletes compare every
/// column of the Before image (<c>IS</c>), which gives optimistic concurrency for undo.
/// </summary>
internal sealed class TableMap<T>(string table, IReadOnlyList<(string Name, Func<T, object?> Get)> columns, Func<SqliteDataReader, T> read)
    : ITableMap where T : class, IEntity
{
    private readonly string _insertSql =
        $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => c.Name))}) VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))});";

    private readonly string _updateSql =
        $"UPDATE {table} SET {string.Join(", ", columns.Skip(1).Select((c, i) => $"{c.Name} = @p{i + 1}"))} " +
        $"WHERE {string.Join(" AND ", columns.Select((c, i) => $"{c.Name} IS @b{i}"))};";

    private readonly string _deleteSql =
        $"DELETE FROM {table} WHERE {string.Join(" AND ", columns.Select((c, i) => $"{c.Name} IS @b{i}"))};";

    public Type EntityType => typeof(T);

    public string Table => table;

    public string SelectSql => $"SELECT * FROM {table}";

    public T Read(SqliteDataReader reader) => read(reader);

    public void Insert(CommandCache cache, IEntity entity)
    {
        var command = cache.Get(_insertSql, columns.Count, "p");
        Bind(command, "p", (T)entity);
        command.ExecuteNonQuery();
    }

    public void Update(CommandCache cache, IEntity before, IEntity after)
    {
        var command = cache.Get(_updateSql, columns.Count, "p", columns.Count);
        Bind(command, "p", (T)after);
        Bind(command, "b", (T)before);
        EnsureOneRow(command.ExecuteNonQuery(), before);
    }

    public void Delete(CommandCache cache, IEntity before)
    {
        var command = cache.Get(_deleteSql, 0, "p", columns.Count);
        Bind(command, "b", (T)before);
        EnsureOneRow(command.ExecuteNonQuery(), before);
    }

    private void Bind(SqliteCommand command, string prefix, T entity)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters[$"@{prefix}{i}"].Value = SqlValues.ToDb(columns[i].Get(entity));
        }
    }

    private static void EnsureOneRow(int affected, IEntity before)
    {
        if (affected != 1)
        {
            throw new ConcurrencyConflictException($"{typeof(T).Name} #{before.Id} 已被其他操作變更或刪除，無法套用這次修改。");
        }
    }
}
