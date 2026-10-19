using System.Globalization;
using LedgerMatch.Core;
using Microsoft.Data.Sqlite;

namespace LedgerMatch.Data.Mapping;

/// <summary>Converts domain values to their SQLite storage form and back. One representation per type keeps "IS" comparisons exact.</summary>
internal static class SqlValues
{
    public static object ToDb(object? value) => value switch
    {
        null => DBNull.Value,
        Money m => m.Cents,
        YearMonth ym => ym.ToString(),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? 1L : 0L,
        Enum e => e.ToString(),
        int i => (long)i,
        _ => value,
    };

    public static long Long(this SqliteDataReader r, string column) => r.GetInt64(r.GetOrdinal(column));

    public static long? NLong(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    public static int Int(this SqliteDataReader r, string column) => checked((int)r.Long(column));

    public static int? NInt(this SqliteDataReader r, string column) => r.NLong(column) is { } v ? checked((int)v) : null;

    public static string Str(this SqliteDataReader r, string column) => r.GetString(r.GetOrdinal(column));

    public static string? NStr(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static bool Bool(this SqliteDataReader r, string column) => r.Long(column) != 0;

    public static Money Money(this SqliteDataReader r, string column) => Core.Money.FromCents(r.Long(column));

    public static Money? NMoney(this SqliteDataReader r, string column) => r.NLong(column) is { } v ? Core.Money.FromCents(v) : null;

    public static DateOnly Date(this SqliteDataReader r, string column) =>
        DateOnly.ParseExact(r.Str(column), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static YearMonth Month(this SqliteDataReader r, string column) => YearMonth.Parse(r.Str(column));

    public static DateTimeOffset Timestamp(this SqliteDataReader r, string column) =>
        DateTimeOffset.ParseExact(r.Str(column), "O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? NTimestamp(this SqliteDataReader r, string column) =>
        r.NStr(column) is { } s ? DateTimeOffset.ParseExact(s, "O", CultureInfo.InvariantCulture) : null;

    public static T Enum<T>(this SqliteDataReader r, string column) where T : struct, Enum => System.Enum.Parse<T>(r.Str(column));

    public static SqliteCommand Command(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, ToDb(value));
        }

        return command;
    }
}
