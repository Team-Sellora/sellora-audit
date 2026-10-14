namespace Sellora.AuditService.Domain.Audit;

/// <summary>
/// One consumed event after envelope normalisation, before it is placed in
/// its company's chain. Payload is the exact text received from Kafka.
/// </summary>
public sealed record AuditEventContent(
    Guid CompanyId,
    Guid EventId,
    string EventType,
    string? SchemaVersion,
    string SourceTopic,
    int Partition,
    long Offset,
    string AggregateType,
    Guid? AggregateId,
    string? OrderReference,
    Guid? ProvinceId,
    string ActorUserId,
    string? ActorRole,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string Payload);
