using System.Reflection;
using System.Text.RegularExpressions;
using LedgerMatch.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace LedgerMatch.Data;

public sealed record Migration(int Version, string Name, string Sql);

/// <summary>
/// Opens (and creates or upgrades) the SQLite file. Schema version lives in PRAGMA user_version;
/// migrations are embedded SQL scripts named V###__name.sql.
/// </summary>
public sealed partial class LedgerDatabase
{
    /// <summary>"LGMT" — marks the file as a LedgerMatch database.</summary>
    public const int ApplicationId = 0x4C474D54;

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private LedgerDatabase(string path, int schemaVersion, string? backupPath)
    {
        Path = path;
        SchemaVersion = schemaVersion;
        BackupPath = backupPath;
        ConnectionString = PooledConnectionString(path);
    }

    public static IReadOnlyList<Migration> BuiltInMigrations { get; } = LoadEmbeddedMigrations();

    public static int LatestVersion => BuiltInMigrations[^1].Version;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LedgerMatch", "ledger.db");

    public string Path { get; }

    public string ConnectionString { get; }

    public int SchemaVersion { get; }

    /// <summary>Copy taken before an upgrade, or null when no upgrade happened.</summary>
    public string? BackupPath { get; }

    public static LedgerDatabase Open(string path, IReadOnlyList<Migration>? migrations = null)
    {
        migrations ??= BuiltInMigrations;
        var latest = migrations[^1].Version;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        var createConnection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        int version;
        string? backup = null;

        try
        {
            using var connection = new SqliteConnection(createConnection);
            connection.Open();
            version = Inspect(connection, path, latest);

            if (version < latest)
            {
                if (version > 0)
                {
                    backup = $"{path}.bak-v{version}";
                    using var backupConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
                    connection.BackupDatabase(backupConnection);
                }

                foreach (var migration in migrations.Where(m => m.Version > version))
                {
                    Apply(connection, migration);
                }

                version = latest;
            }

            Execute(connection, "PRAGMA journal_mode=WAL;");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            throw Corrupt(path, ex);
        }

        return new LedgerDatabase(path, version, backup);
    }

    /// <summary>
    /// Closes the idle pooled connections of one database file so the file can be moved, deleted or reopened.
    /// Unlike <see cref="SqliteConnection.ClearAllPools"/>, it does not touch connections to other files that may be in use.
    /// </summary>
    public static void ReleasePool(string path)
    {
        using var connection = new SqliteConnection(PooledConnectionString(path));
        SqliteConnection.ClearPool(connection);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static string PooledConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        ForeignKeys = true,
        Pooling = true,
    }.ToString();

    private static int Inspect(SqliteConnection connection, string path, int latest)
    {
        var applicationId = Convert.ToInt32(Scalar(connection, "PRAGMA application_id;"));
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        var tableCount = Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master;"));

        if (applicationId == 0 && version == 0 && tableCount == 0)
        {
            return 0;
        }

        if (applicationId != ApplicationId)
        {
            throw new DatabaseOpenException(DatabaseOpenFailure.Corrupt, path, $"「{path}」不是 LedgerMatch 的資料庫檔案。");
        }

        if (version > latest)
        {
            throw new DatabaseOpenException(
                DatabaseOpenFailure.TooNew,
                path,
                $"資料庫「{path}」的版本（v{version}）比這個程式支援的版本（v{latest}）新，請改用較新版的 LedgerMatch 開啟。程式不會修改這個檔案。");
        }

        var check = Convert.ToString(Scalar(connection, "PRAGMA quick_check;"));
        if (check != "ok")
        {
            throw new DatabaseOpenException(DatabaseOpenFailure.Corrupt, path, $"資料庫「{path}」已損毀（{check}）。請從備份還原。");
        }

        return version;
    }

    private static void Apply(SqliteConnection connection, Migration migration)
    {
        using var tx = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = migration.Sql;
            command.ExecuteNonQuery();
            command.CommandText = $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {migration.Version};";
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static DatabaseOpenException Corrupt(string path, Exception inner) =>
        new(DatabaseOpenFailure.Corrupt, path, $"資料庫「{path}」已損毀或不是有效的資料庫檔案（{inner.Message}）。請從備份還原。", inner);

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<Migration> LoadEmbeddedMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = MigrationName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add(new Migration(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups[2].Value, reader.ReadToEnd()));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Migration versions must be contiguous from 1; found V{migrations[i].Version:D3}.");
            }
        }

        return migrations;
    }

    [GeneratedRegex(@"\.V(\d{3})__(\w+)\.sql$")]
    private static partial Regex MigrationName();
}
