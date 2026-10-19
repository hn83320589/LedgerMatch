namespace LedgerMatch.Core.Importing;

public sealed record RowError(int LineNumber, string Reason);

public sealed record ParsedStatementRow(
    int LineNumber,
    DateOnly Date,
    Money Amount,
    string Description,
    string? Serial,
    string? Note);

public sealed record ParsedReceiptRow(
    int LineNumber,
    DateOnly IssueDate,
    string Customer,
    string ReceiptNo,
    Money Amount,
    string? Note,
    string? AccountName = null);

public sealed record ParseResult<T>(IReadOnlyList<T> Rows, IReadOnlyList<RowError> Errors);

/// <summary>The whole file cannot be read with the given profile (wrong encoding, missing header, invalid profile).</summary>
public sealed class ImportFileException(string message, Exception? inner = null) : Exception(message, inner);
