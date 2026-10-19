namespace LedgerMatch.Core.Persistence;

public enum DatabaseOpenFailure
{
    /// <summary>The file was written by a newer version of LedgerMatch.</summary>
    TooNew,

    /// <summary>The file is damaged or is not a LedgerMatch database.</summary>
    Corrupt,
}

/// <summary>The database cannot be opened safely; the message tells the user what happened and where the file is.</summary>
public sealed class DatabaseOpenException(DatabaseOpenFailure failure, string path, string message, Exception? inner = null)
    : LedgerException(message, inner)
{
    public DatabaseOpenFailure Failure { get; } = failure;

    public string Path { get; } = path;
}
