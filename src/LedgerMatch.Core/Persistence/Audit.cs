using System.Security.Cryptography;
using System.Text;

namespace LedgerMatch.Core.Persistence;

/// <summary>One append-only audit record. <see cref="Hash"/> chains to the previous record so tampering is detectable.</summary>
public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset At,
    string Operation,
    string EntityType,
    long EntityId,
    string Action,
    string? BeforeJson,
    string? AfterJson,
    string PrevHash,
    string Hash);

/// <summary>An audit record before the store assigns its sequence number and hash chain.</summary>
public sealed record PendingAuditEntry(
    DateTimeOffset At,
    string Operation,
    string EntityType,
    long EntityId,
    string Action,
    string? BeforeJson,
    string? AfterJson);

public static class AuditHasher
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(string prevHash, long sequence, PendingAuditEntry entry)
    {
        var payload = string.Join(
            '\u001f',
            prevHash,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            entry.Operation,
            entry.EntityType,
            entry.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.Action,
            entry.BeforeJson ?? string.Empty,
            entry.AfterJson ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>Returns the sequence number of the first broken link, or null when the whole chain verifies.</summary>
    public static long? FindFirstBrokenLink(IEnumerable<AuditEntry> entriesInOrder)
    {
        var prev = GenesisHash;
        foreach (var e in entriesInOrder)
        {
            var pending = new PendingAuditEntry(e.At, e.Operation, e.EntityType, e.EntityId, e.Action, e.BeforeJson, e.AfterJson);
            if (e.PrevHash != prev || e.Hash != Compute(prev, e.Sequence, pending))
            {
                return e.Sequence;
            }

            prev = e.Hash;
        }

        return null;
    }
}
