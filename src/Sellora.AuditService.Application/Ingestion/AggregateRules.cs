namespace Sellora.AuditService.Application.Ingestion;

/// <param name="IdFields">Payload fields holding this aggregate's ID, in order of preference.</param>
public sealed record AggregateRule(string AggregateType, IReadOnlyList<string> IdFields);

/// <summary>
/// The aggregate an event is about, from its topic and event type
/// (US-E7-1-BE3). The type is decided first and the ID field is chosen for
/// that type: a DeliveryStatusChanged carries both deliveryId and orderId,
/// and its aggregate is the delivery. Event types not listed still get the
/// topic's default rule, so unknown events are recorded too.
/// </summary>
public static class AggregateRules
{
    public const string HierarchyTopic = "sellora.hierarchy.v1";
    public const string CatalogTopic = "sellora.catalog.v1";
    public const string OrderTopic = "sellora.order.v1";
    public const string InventoryTopic = "sellora.inventory.v1";
    public const string DeliveryTopic = "sellora.delivery.v1";

    private static readonly AggregateRule Order = new("Order", new[] { "orderId", "entityId" });
    private static readonly AggregateRule VanReturn = new("VanReturn", new[] { "vanReturnId", "entityId" });
    private static readonly AggregateRule Delivery = new("Delivery", new[] { "deliveryId", "deliveryJobId", "entityId" });
    private static readonly AggregateRule Return = new("Return", new[] { "returnRequestId", "entityId" });

    private static readonly Dictionary<(string Topic, string EventType), AggregateRule> ByEvent = new()
    {
        [(OrderTopic, "OrderPlaced")] = Order,
        [(OrderTopic, "OrderApproved")] = Order,
        [(OrderTopic, "OrderConfirmed")] = Order,
        [(OrderTopic, "PaymentRecorded")] = Order,
        [(OrderTopic, "OrderCancelled")] = Order,
        [(OrderTopic, "VanStockReturned")] = VanReturn,
        [(DeliveryTopic, "DeliveryJobCreated")] = Delivery,
        [(DeliveryTopic, "DeliveryStatusChanged")] = Delivery,
        [(DeliveryTopic, "DeliveryConfirmed")] = Delivery,
        [(DeliveryTopic, "DeliveryDisputed")] = Delivery,
        [(DeliveryTopic, "ReturnRequested")] = Return,
        [(DeliveryTopic, "ReturnAccepted")] = Return,
        [(DeliveryTopic, "ReturnRejected")] = Return,
        [(HierarchyTopic, "AgencyRegistered")] = new("Agency", new[] { "agencyId", "entityId" }),
        [(HierarchyTopic, "TerritoryAssignedToAgency")] = new("Territory", new[] { "territoryId", "entityId" }),
        [(HierarchyTopic, "ShopRegistered")] = new("Shop", new[] { "shopId", "entityId" }),
        [(HierarchyTopic, "ShopUpdated")] = new("Shop", new[] { "shopId", "entityId" }),
        [(HierarchyTopic, "SalesRepAssigned")] = new("SalesRep", new[] { "salesRepId", "entityId" }),
        [(CatalogTopic, "PriceChanged")] = new("Product", new[] { "productId", "entityId" }),
        [(InventoryTopic, "LowStockDetected")] = new("StockItem", new[] { "stockItemId", "entityId" })
    };

    private static readonly Dictionary<string, AggregateRule> ByTopic = new()
    {
        [OrderTopic] = new("Order", new[] { "orderId", "entityId", "aggregateId" }),
        [DeliveryTopic] = new("Delivery", new[] { "deliveryId", "deliveryJobId", "entityId", "aggregateId" }),
        [HierarchyTopic] = new("Hierarchy", new[] { "entityId", "aggregateId" }),
        [CatalogTopic] = new("Catalog", new[] { "productId", "entityId", "aggregateId" }),
        [InventoryTopic] = new("Inventory", new[] { "stockItemId", "entityId", "aggregateId" })
    };

    public static readonly IReadOnlyList<string> SourceTopics = new[]
    {
        HierarchyTopic, CatalogTopic, OrderTopic, InventoryTopic, DeliveryTopic
    };

    /// <summary>Aggregate types the API and the web filter offer.</summary>
    public static readonly IReadOnlyList<string> KnownAggregateTypes = new[]
    {
        "Order", "VanReturn", "Delivery", "Return", "Agency", "Territory", "Shop", "SalesRep", "Product", "StockItem"
    };

    public static AggregateRule For(string topic, string eventType)
    {
        if (ByEvent.TryGetValue((topic, eventType), out var rule))
        {
            return rule;
        }

        return ByTopic.TryGetValue(topic, out var topicRule)
            ? topicRule
            : new AggregateRule(topic, new[] { "entityId", "aggregateId" });
    }
}
