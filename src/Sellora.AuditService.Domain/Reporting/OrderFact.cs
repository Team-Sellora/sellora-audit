using Sellora.AuditService.Domain.Tenancy;

namespace Sellora.AuditService.Domain.Reporting;

public static class OrderEventTypes
{
    public const string OrderPlaced = "OrderPlaced";
    public const string OrderApproved = "OrderApproved";
    public const string OrderConfirmed = "OrderConfirmed";
    public const string PaymentRecorded = "PaymentRecorded";
    public const string OrderCancelled = "OrderCancelled";

    public static bool IsOrderEvent(string eventType) => eventType is
        OrderPlaced or OrderApproved or OrderConfirmed or PaymentRecorded or OrderCancelled;
}

public static class OrderFactStatus
{
    public const string Placed = "Placed";
    public const string Approved = "Approved";
    public const string Confirmed = "Confirmed";
    public const string Cancelled = "Cancelled";

    public static int Rank(string status) => status switch
    {
        Cancelled => 4,
        Confirmed => 3,
        Approved => 2,
        _ => 1
    };

    // Taken from the event type, not the payload, so only a confirming event can make a sale.
    public static string For(string eventType) => eventType switch
    {
        OrderEventTypes.OrderApproved => Approved,
        OrderEventTypes.OrderConfirmed or OrderEventTypes.PaymentRecorded => Confirmed,
        OrderEventTypes.OrderCancelled => Cancelled,
        _ => Placed
    };
}

/// <summary>
/// US-E7-2-BE1: one row per order. Apply is safe to repeat and safe out of
/// order: the status only moves forward, times keep the earliest value and
/// snapshot fields only fill gaps.
/// </summary>
public sealed class OrderFact : ITenantScoped
{
    private readonly List<OrderFactLine> _lines = new();

    private OrderFact()
    {
        OrderReference = string.Empty;
        Status = OrderFactStatus.Placed;
        Currency = "LKR";
    }

    public Guid CompanyId { get; private set; }

    public Guid OrderId { get; private set; }

    public string OrderReference { get; private set; }

    public Guid? ProvinceId { get; private set; }

    public Guid? AgencyId { get; private set; }

    public string? AgencyName { get; private set; }

    public Guid? TerritoryId { get; private set; }

    public Guid? SalesRepId { get; private set; }

    public string? SalesRepName { get; private set; }

    public Guid? ShopId { get; private set; }

    public string? ShopName { get; private set; }

    public string? FulfilmentType { get; private set; }

    public string Status { get; private set; }

    public decimal Total { get; private set; }

    public string Currency { get; private set; }

    public DateTimeOffset? PlacedAt { get; private set; }

    public DateOnly? PlacedDate { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateOnly? SaleDate { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public decimal PaidAmount { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<OrderFactLine> Lines => _lines;

    public static OrderFact Start(Guid companyId, Guid orderId) => new()
    {
        CompanyId = companyId,
        OrderId = orderId
    };

    public void Apply(OrderEventData data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.OrderId != OrderId)
        {
            throw new ArgumentException("The event belongs to another order.", nameof(data));
        }

        if (string.IsNullOrWhiteSpace(OrderReference))
        {
            OrderReference = data.OrderReference ?? string.Empty;
        }

        ProvinceId ??= data.ProvinceId;
        AgencyId ??= data.AgencyId;
        AgencyName ??= data.AgencyName;
        TerritoryId ??= data.TerritoryId;
        SalesRepId ??= data.SalesRepId;
        SalesRepName ??= data.SalesRepName;
        ShopId ??= data.ShopId;
        ShopName ??= data.ShopName;
        FulfilmentType ??= data.FulfilmentType;

        if (data.Total is { } total)
        {
            Total = total;
        }

        if (!string.IsNullOrWhiteSpace(data.Currency))
        {
            Currency = data.Currency;
        }

        var placedAt = data.OrderDate ?? (data.EventType == OrderEventTypes.OrderPlaced ? data.OccurredAt : null);
        if (placedAt is { } placed && (PlacedAt is null || placed < PlacedAt))
        {
            PlacedAt = placed;
            PlacedDate = SalesCalendar.ToSalesDate(placed);
        }

        switch (data.EventType)
        {
            case OrderEventTypes.OrderConfirmed:
                SetConfirmed(data.ConfirmedAt ?? data.OccurredAt);
                break;
            case OrderEventTypes.PaymentRecorded:
                // A cash sale is confirmed by its payment.
                SetConfirmed(data.ConfirmedAt ?? data.OccurredAt);
                PaidAmount = data.PaymentAmount ?? PaidAmount;
                PaidAt = Earliest(PaidAt, data.PaymentRecordedAt ?? data.OccurredAt);
                break;
            case OrderEventTypes.OrderCancelled:
                CancelledAt = Earliest(CancelledAt, data.CancelledAt ?? data.OccurredAt);
                break;
        }

        var status = OrderFactStatus.For(data.EventType);
        if (OrderFactStatus.Rank(status) > OrderFactStatus.Rank(Status))
        {
            Status = status;
        }

        if (_lines.Count == 0 && data.Lines.Count > 0)
        {
            var number = 0;
            foreach (var line in data.Lines)
            {
                _lines.Add(new OrderFactLine(CompanyId, OrderId, ++number, line.ProductId, line.ProductName, line.Quantity, line.LineTotal));
            }
        }

        UpdatedAt = now;
    }

    private void SetConfirmed(DateTimeOffset confirmedAt)
    {
        ConfirmedAt = Earliest(ConfirmedAt, confirmedAt);
        SaleDate = SalesCalendar.ToSalesDate(ConfirmedAt.Value);
    }

    private static DateTimeOffset Earliest(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } existing && existing <= candidate ? existing : candidate;
}

public sealed class OrderFactLine : ITenantScoped
{
    private OrderFactLine()
    {
    }

    public OrderFactLine(Guid companyId, Guid orderId, int lineNumber, Guid productId, string? productName, int quantity, decimal lineTotal)
    {
        CompanyId = companyId;
        OrderId = orderId;
        LineNumber = lineNumber;
        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;
        LineTotal = lineTotal;
    }

    public Guid CompanyId { get; private set; }

    public Guid OrderId { get; private set; }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public string? ProductName { get; private set; }

    public int Quantity { get; private set; }

    public decimal LineTotal { get; private set; }
}
