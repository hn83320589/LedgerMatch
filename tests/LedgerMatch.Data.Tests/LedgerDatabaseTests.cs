using LedgerMatch.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace LedgerMatch.Data.Tests;

public class LedgerDatabaseTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void NewDatabase_IsCreatedAtLatestVersion()
    {
        var database = _db.Open();

        Assert.Equal(LedgerDatabase.LatestVersion, database.SchemaVersion);
        Assert.Null(database.BackupPath);
        Assert.Equal(LedgerDatabase.LatestVersion, Pragma("user_version"));
        Assert.Equal(LedgerDatabase.ApplicationId, Pragma("application_id"));
    }

    [Fact]
    public void Reopen_KeepsData()
    {
        var store = _db.OpenStore();
        var first = store.ReserveIds(5);

        var reopened = _db.OpenStore();
        Assert.Equal(first + 5, reopened.ReserveIds(1));
    }

    [Fact]
    public void TooNewDatabase_IsRejectedWithClearMessage_AndLeftUntouched()
    {
        _db.Open();
        SetPragma("user_version", LedgerDatabase.LatestVersion + 1);
        var before = File.ReadAllBytes(_db.Path);

        var ex = Assert.Throws<DatabaseOpenException>(() => _db.Open());

        Assert.Equal(DatabaseOpenFailure.TooNew, ex.Failure);
        Assert.Contains("較新版", ex.Message);
        Assert.Contains(_db.Path, ex.Message);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(_db.Path));
    }

    [Fact]
    public void GarbageFile_IsReportedAsCorrupt()
    {
        var random = new Random(42);
        var bytes = new byte[8192];
        random.NextBytes(bytes);
        File.WriteAllBytes(_db.Path, bytes);

        var ex = Assert.Throws<DatabaseOpenException>(() => _db.Open());
        Assert.Equal(DatabaseOpenFailure.Corrupt, ex.Failure);
    }

    [Fact]
    public void DamagedPages_AreReportedAsCorrupt()
    {
        var store = _db.OpenStore();
        for (var i = 0; i < 50; i++)
        {
            store.ReserveIds(1);
        }

        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={_db.Path};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=DELETE;";
            cmd.ExecuteNonQuery();
        }

        var bytes = File.ReadAllBytes(_db.Path);
        for (var i = 4096 + 8; i < bytes.Length; i += 7)
        {
            bytes[i] ^= 0x5A;
        }

        File.WriteAllBytes(_db.Path, bytes);

        var ex = Assert.Throws<DatabaseOpenException>(() => _db.Open());
        Assert.Equal(DatabaseOpenFailure.Corrupt, ex.Failure);
    }

    [Fact]
    public void ForeignSqliteDatabase_IsRejected()
    {
        using (var connection = new SqliteConnection($"Data Source={_db.Path};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE other (x INTEGER);";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<DatabaseOpenException>(() => _db.Open());
        Assert.Contains("不是 LedgerMatch", ex.Message);
    }

    [Fact]
    public void OldDatabase_IsUpgradedInOrder_KeepingDataAndBackup()
    {
        Migration v1 = new(1, "init", "CREATE TABLE note (id INTEGER PRIMARY KEY, text TEXT NOT NULL); INSERT INTO note VALUES (1, 'kept');");
        Migration v2 = new(2, "add_col", "ALTER TABLE note ADD COLUMN tag TEXT NOT NULL DEFAULT 'none';");
        Migration v3 = new(3, "fill", "UPDATE note SET tag = 'upgraded';");

        Assert.Equal(1, _db.Open([v1]).SchemaVersion);
        var upgraded = _db.Open([v1, v2, v3]);

        Assert.Equal(3, upgraded.SchemaVersion);
        Assert.NotNull(upgraded.BackupPath);
        Assert.True(File.Exists(upgraded.BackupPath));
        using var connection = upgraded.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT text || ':' || tag FROM note;";
        Assert.Equal("kept:upgraded", cmd.ExecuteScalar());
    }

    [Fact]
    public void FailingMigration_RollsBackThatVersion()
    {
        Migration v1 = new(1, "init", "CREATE TABLE note (id INTEGER PRIMARY KEY);");
        Migration bad = new(2, "bad", "CREATE TABLE extra (id INTEGER); THIS IS NOT SQL;");
        _db.Open([v1]);

        Assert.ThrowsAny<SqliteException>(() => _db.Open([v1, bad]));
        Assert.Equal(1, Pragma("user_version"));
    }

    [Fact]
    public void BuiltInMigrations_AreContiguousFromOne()
    {
        Assert.Equal(Enumerable.Range(1, LedgerDatabase.BuiltInMigrations.Count), LedgerDatabase.BuiltInMigrations.Select(m => m.Version));
    }

    private long Pragma(string name)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={_db.Path};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {name};";
        return (long)cmd.ExecuteScalar()!;
    }

    private void SetPragma(string name, int value)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={_db.Path};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {name} = {value};";
        cmd.ExecuteNonQuery();
    }
}
