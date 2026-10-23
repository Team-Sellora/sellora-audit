namespace Sellora.AuditService.Application.Audit;

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// Who may see what. Null province IDs mean the whole company (Company
/// Admin); otherwise only entries whose ProvinceId is in the list, and never
/// an entry without a province (US-E7-1-BE7).
/// </summary>
public sealed record AuditReadScope(Guid CompanyId, IReadOnlyCollection<Guid>? ProvinceIds)
{
    public bool IsWholeCompany => ProvinceIds is null;
}

public sealed record AuditSearchQuery(
    string? AggregateType = null,
    Guid? AggregateId = null,
    string? OrderReference = null,
    string? ActorUserId = null,
    string? EventType = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 20)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int SafePage => Page < 1 ? 1 : Page;

    public int SafePageSize => PageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => PageSize
    };

    /// <summary>An order or one aggregate reads as a timeline (oldest first); anything else newest first.</summary>
    public bool IsTimeline => !string.IsNullOrWhiteSpace(OrderReference) || AggregateId is not null;

    public string? Validate()
    {
        if (From is { } from && To is { } to && from > to)
        {
            return "from must not be later than to.";
        }

        return null;
    }
}

public sealed record AuditEntrySummary(
    Guid AuditEntryId,
    long ChainPosition,
    Guid EventId,
    string EventType,
    string AggregateType,
    Guid? AggregateId,
    string? OrderReference,
    string ActorUserId,
    string? ActorRole,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string? CorrelationId,
    string SourceTopic);

/// <param name="Payload">The exact stored JSON text, never re-serialised.</param>
public sealed record AuditEntryDetail(
    Guid AuditEntryId,
    long ChainPosition,
    Guid EventId,
    string EventType,
    string AggregateType,
    Guid? AggregateId,
    string? OrderReference,
    string ActorUserId,
    string? ActorRole,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string? CorrelationId,
    string SourceTopic,
    string? SchemaVersion,
    Guid? ProvinceId,
    int Partition,
    long Offset,
    string PreviousHash,
    string Hash,
    string Payload);

/// <summary>The last row of an extract page; the next page starts after it.</summary>
public sealed record AuditExtractCursor(DateTimeOffset OccurredAt, long ChainPosition);

public interface IAuditReadService
{
    Task<PagedResponse<AuditEntrySummary>> SearchAsync(AuditReadScope scope, AuditSearchQuery query, CancellationToken cancellationToken);

    /// <summary>Null when the entry does not exist or is outside the scope.</summary>
    Task<AuditEntryDetail?> GetAsync(AuditReadScope scope, Guid auditEntryId, CancellationToken cancellationToken);

    /// <summary>US-E7-3-BE2: one page of the search, in the on-screen order, for an export.</summary>
    Task<IReadOnlyList<AuditEntryDetail>> ExtractPageAsync(
        AuditReadScope scope, AuditSearchQuery query, AuditExtractCursor? after, int take, CancellationToken cancellationToken);
}
