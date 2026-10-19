namespace LedgerMatch.Data.Tests;

/// <summary>A database file in a fresh temp folder, deleted on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LedgerMatchTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Path = System.IO.Path.Combine(Directory, "ledger.db");
    }

    public string Directory { get; }

    public string Path { get; }

    public LedgerDatabase Open(IReadOnlyList<Migration>? migrations = null) => LedgerDatabase.Open(Path, migrations);

    public SqliteLedgerStore OpenStore() => new(Open());

    public void Dispose()
    {
        LedgerDatabase.ReleasePool(Path);
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the temp folder is cleaned by the OS eventually.
        }
    }
}
