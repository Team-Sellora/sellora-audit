using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sellora.AuditService.Domain.Audit;

/// <summary>
/// The chain hash (US-E7-1-BE5):
/// SHA-256 over chainPosition|companyId|eventId|eventType|aggregateType|aggregateId|occurredAt|correlationId|payload|previousHash,
/// as lowercase hex. Guids are lowercase "D" format, a missing aggregate ID
/// or correlation ID is an empty string, and occurredAt is UTC with exactly
/// six fractional digits. Every part is culture invariant, so the same row
/// gives the same hash on any machine.
/// </summary>
public static class AuditHasher
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
        Justification = "The parameters are the hash input fields of US-E7-1-BE5, in their specified order.")]
    public static string Compute(
        long chainPosition,
        Guid companyId,
        Guid eventId,
        string eventType,
        string aggregateType,
        Guid? aggregateId,
        DateTimeOffset occurredAt,
        string? correlationId,
        string payload,
        string previousHash)
    {
        var input = string.Join('|',
            chainPosition.ToString(CultureInfo.InvariantCulture),
            companyId.ToString("D"),
            eventId.ToString("D"),
            eventType,
            aggregateType,
            aggregateId?.ToString("D") ?? string.Empty,
            FormatTimestamp(occurredAt),
            correlationId ?? string.Empty,
            payload,
            previousHash);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public static string Compute(ChainRecord record) => Compute(
        record.ChainPosition,
        record.CompanyId,
        record.EventId,
        record.EventType,
        record.AggregateType,
        record.AggregateId,
        record.OccurredAt,
        record.CorrelationId,
        record.Payload,
        record.PreviousHash);

    public static string FormatTimestamp(DateTimeOffset value) =>
        AuditTime.Normalise(value).UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
