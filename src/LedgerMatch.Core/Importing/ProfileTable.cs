using System.Text;

namespace LedgerMatch.Core.Importing;

/// <summary>
/// Decodes a file with a profile's encoding, skips header rows and resolves column references.
/// Shared by the statement and receipt parsers.
/// </summary>
internal sealed class ProfileTable
{
    private readonly Dictionary<ColumnRef, int> _resolved = [];

    static ProfileTable()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private ProfileTable(IReadOnlyList<CsvRecord> dataRecords)
    {
        DataRecords = dataRecords;
    }

    public IReadOnlyList<CsvRecord> DataRecords { get; }

    public static ProfileTable Load(Stream stream, MappingProfile profile, ProfileKind expectedKind)
    {
        if (profile.Kind != expectedKind)
        {
            throw new ImportFileException($"設定檔「{profile.Name}」不是{(expectedKind == ProfileKind.Statement ? "對帳單" : "收款單")}設定檔。");
        }

        var problems = profile.Validate();
        if (problems.Count > 0)
        {
            throw new ImportFileException($"設定檔「{profile.Name}」有誤：{string.Join(" ", problems)}");
        }

        var text = Decode(stream, profile.Encoding);
        var records = CsvReader.Read(new StringReader(text), profile.Delimiter).ToList();
        var header = profile.HeaderRowCount > 0 && records.Count >= profile.HeaderRowCount
            ? records[profile.HeaderRowCount - 1]
            : null;

        var table = new ProfileTable(records.Skip(profile.HeaderRowCount).ToList());
        foreach (var column in profile.AllColumns())
        {
            table._resolved[column] = ResolveIndex(column, header);
        }

        return table;
    }

    /// <summary>Returns the trimmed cell text, null for a missing optional column, or an error for a short row.</summary>
    public bool TryGet(CsvRecord record, ColumnRef? column, out string? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (column is null)
        {
            return true;
        }

        var index = _resolved[column];
        if (index >= record.Fields.Count)
        {
            error = $"{column}不存在（此列只有 {record.Fields.Count} 欄）";
            return false;
        }

        value = record.Fields[index].Trim();
        return true;
    }

    private static int ResolveIndex(ColumnRef column, CsvRecord? header)
    {
        if (column.Index is { } index)
        {
            return index - 1;
        }

        if (header is null)
        {
            throw new ImportFileException("檔案沒有標題列，無法以名稱尋找欄位。");
        }

        for (var i = 0; i < header.Fields.Count; i++)
        {
            if (string.Equals(header.Fields[i].Trim(), column.Name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ImportFileException(
            $"標題列找不到欄位{column}。檔案中的欄位有：{string.Join("、", header.Fields.Select(f => f.Trim()))}");
    }

    private static string Decode(Stream stream, TextEncodingKind kind)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        try
        {
            if (kind == TextEncodingKind.Utf8)
            {
                var offset = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset);
            }

            var big5 = Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return big5.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ImportFileException(
                $"檔案內容不是有效的 {(kind == TextEncodingKind.Utf8 ? "UTF-8" : "Big5")} 編碼，請確認設定檔的編碼設定。", ex);
        }
    }
}
