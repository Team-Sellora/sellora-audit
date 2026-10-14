namespace Sellora.AuditService.Application.Ingestion;

/// <summary>A Kafka record as received, independent of the Kafka client library.</summary>
public sealed record IncomingMessage(
    string Topic,
    int Partition,
    long Offset,
    DateTimeOffset Timestamp,
    string? Key,
    IReadOnlyList<KeyValuePair<string, byte[]?>> Headers,
    byte[]? Value);
