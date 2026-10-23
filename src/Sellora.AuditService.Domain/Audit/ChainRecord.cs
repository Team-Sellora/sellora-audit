namespace Sellora.AuditService.Domain.Audit;

/// <summary>The stored columns that verification needs, read back from the database.</summary>
public sealed record ChainRecord(
    Guid AuditEntryId,
    long ChainPosition,
    Guid CompanyId,
    Guid EventId,
    string EventType,
    string AggregateType,
    Guid? AggregateId,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string Payload,
    string PreviousHash,
    string Hash);
