namespace Sellora.AuditService.Domain.Reporting;

public sealed record OrderLineData(Guid ProductId, string? ProductName, int Quantity, decimal LineTotal);

/// <summary>The fields of an order event that the reporting projections use.</summary>
public sealed record OrderEventData
{
    public required string EventType { get; init; }

    public required Guid OrderId { get; init; }

    public string? OrderReference { get; init; }

    public string? FulfilmentType { get; init; }

    public Guid? ProvinceId { get; init; }

    public Guid? AgencyId { get; init; }

    public string? AgencyName { get; init; }

    public Guid? TerritoryId { get; init; }

    public Guid? SalesRepId { get; init; }

    public string? SalesRepName { get; init; }

    public Guid? ShopId { get; init; }

    public string? ShopName { get; init; }

    public decimal? Total { get; init; }

    public string? Currency { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? OrderDate { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public decimal? PaymentAmount { get; init; }

    public DateTimeOffset? PaymentRecordedAt { get; init; }

    public IReadOnlyList<OrderLineData> Lines { get; init; } = Array.Empty<OrderLineData>();
}
