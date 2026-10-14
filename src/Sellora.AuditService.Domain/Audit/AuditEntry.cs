using Sellora.AuditService.Domain.Tenancy;

namespace Sellora.AuditService.Domain.Audit;

/// <summary>
/// One immutable row of a company's audit chain. There are no setters and no
/// mutating methods; AuditDbContext also refuses to save a modified or
/// deleted entry, and the runtime database role has no UPDATE or DELETE.
/// </summary>
public sealed class AuditEntry : ITenantScoped
{
    private AuditEntry()
    {
        EventType = string.Empty;
        SourceTopic = string.Empty;
        AggregateType = string.Empty;
        ActorUserId = string.Empty;
        Payload = string.Empty;
        PreviousHash = string.Empty;
        Hash = string.Empty;
    }

    public Guid AuditEntryId { get; private set; }

    public Guid CompanyId { get; private set; }

    public long ChainPosition { get; private set; }

    public Guid EventId { get; private set; }

    public string EventType { get; private set; }

    public string? SchemaVersion { get; private set; }

    public string SourceTopic { get; private set; }

    public int Partition { get; private set; }

    public long Offset { get; private set; }

    public string AggregateType { get; private set; }

    public Guid? AggregateId { get; private set; }

    public string? OrderReference { get; private set; }

    public Guid? ProvinceId { get; private set; }

    public string ActorUserId { get; private set; }

    public string? ActorRole { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public string? CorrelationId { get; private set; }

    public string Payload { get; private set; }

    public string PreviousHash { get; private set; }

    public string Hash { get; private set; }

    /// <summary>Places the event after <paramref name="previousHash"/> at <paramref name="chainPosition"/>.</summary>
    public static AuditEntry Append(
        AuditEventContent content,
        long chainPosition,
        string previousHash,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(chainPosition, 1);

        if (previousHash.Length != AuditHasher.GenesisHash.Length)
        {
            throw new ArgumentException("A previous hash is 64 hexadecimal characters.", nameof(previousHash));
        }

        var occurredAt = AuditTime.Normalise(content.OccurredAt);

        return new AuditEntry
        {
            AuditEntryId = Guid.NewGuid(),
            CompanyId = content.CompanyId,
            ChainPosition = chainPosition,
            EventId = content.EventId,
            EventType = content.EventType,
            SchemaVersion = content.SchemaVersion,
            SourceTopic = content.SourceTopic,
            Partition = content.Partition,
            Offset = content.Offset,
            AggregateType = content.AggregateType,
            AggregateId = content.AggregateId,
            OrderReference = content.OrderReference,
            ProvinceId = content.ProvinceId,
            ActorUserId = content.ActorUserId,
            ActorRole = content.ActorRole,
            OccurredAt = occurredAt,
            RecordedAt = AuditTime.Normalise(recordedAt),
            CorrelationId = content.CorrelationId,
            Payload = content.Payload,
            PreviousHash = previousHash,
            Hash = AuditHasher.Compute(
                chainPosition,
                content.CompanyId,
                content.EventId,
                content.EventType,
                content.AggregateType,
                content.AggregateId,
                occurredAt,
                content.CorrelationId,
                content.Payload,
                previousHash)
        };
    }

    public ChainRecord ToChainRecord() => new(
        AuditEntryId,
        ChainPosition,
        CompanyId,
        EventId,
        EventType,
        AggregateType,
        AggregateId,
        OccurredAt,
        CorrelationId,
        Payload,
        PreviousHash,
        Hash);
}
