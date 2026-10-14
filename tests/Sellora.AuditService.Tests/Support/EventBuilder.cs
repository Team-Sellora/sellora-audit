using System.Text;
using System.Text.Json;
using Sellora.AuditService.Application.Ingestion;

namespace Sellora.AuditService.Tests.Support;

public sealed record SaleParty(Guid ProvinceId, Guid AgencyId, string AgencyName, Guid TerritoryId, Guid RepId, string RepName);

/// <summary>Builds order and hierarchy records shaped like sellora-order's and sellora-organization's real events.</summary>
public static class EventBuilder
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static long _offset;

    public static IncomingMessage Order(
        Guid companyId,
        string eventType,
        Guid orderId,
        string reference,
        SaleParty party,
        DateTimeOffset occurredAt,
        decimal total,
        string status,
        Guid? productId = null,
        decimal? paymentAmount = null,
        string fulfilmentType = "ScheduledDelivery")
    {
        var product = productId ?? Guid.Parse("45000000-0000-0000-0000-000000000001");
        var payload = new Dictionary<string, object?>
        {
            ["eventId"] = Guid.NewGuid(),
            ["eventType"] = eventType,
            ["schemaVersion"] = "1.0",
            ["companyId"] = companyId,
            ["entityId"] = orderId,
            ["orderId"] = orderId,
            ["orderReference"] = reference,
            ["occurredAt"] = occurredAt,
            ["correlationId"] = $"corr-{reference}",
            ["fulfilmentType"] = fulfilmentType,
            ["status"] = status,
            ["orderDate"] = occurredAt,
            ["shop"] = new { shopId = Guid.NewGuid(), name = "Test Shop" },
            ["agency"] = new { agencyId = party.AgencyId, name = party.AgencyName },
            ["territoryId"] = party.TerritoryId,
            ["provinceId"] = party.ProvinceId,
            ["salesRep"] = new { salesRepId = party.RepId, name = party.RepName },
            ["lines"] = new[] { new { productId = product, productName = "Anchor 400g", quantity = 1, unitPrice = total, lineTotal = total } },
            ["subtotal"] = total,
            ["total"] = total,
            ["currency"] = "LKR"
        };

        if (eventType == "OrderConfirmed")
        {
            payload["confirmedAt"] = occurredAt;
        }

        if (eventType == "OrderCancelled")
        {
            payload["cancelledAt"] = occurredAt;
            payload["source"] = "ShopCancellation";
            payload["cancelledBy"] = new { userId = "shop-owner-1", role = "ShopOwner" };
        }

        if (paymentAmount is { } amount)
        {
            payload["payment"] = new { paymentId = Guid.NewGuid(), amount, method = "Cash", recordedAt = occurredAt };
        }

        return Record("sellora.order.v1", reference, JsonSerializer.Serialize(payload, Web),
            ("event-id", Encoding.UTF8.GetBytes(((Guid)payload["eventId"]!).ToString())),
            ("company-id", Encoding.UTF8.GetBytes(companyId.ToString())));
    }

    public static IncomingMessage SalesRepAssigned(Guid companyId, SaleParty party) =>
        Record("sellora.hierarchy.v1", party.RepId.ToString(), JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = "SalesRepAssigned",
            schemaVersion = "1.0",
            companyId,
            entityId = party.RepId,
            salesRepId = party.RepId,
            salesRepName = party.RepName,
            territoryId = party.TerritoryId,
            agencyId = party.AgencyId,
            effectiveAt = DateTimeOffset.UtcNow,
            correlationId = "corr-hierarchy"
        }), ("event-id", Guid.NewGuid().ToByteArray()));

    public static IncomingMessage AgencyRegistered(Guid companyId, SaleParty party) =>
        Record("sellora.hierarchy.v1", party.AgencyId.ToString(), JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = "AgencyRegistered",
            schemaVersion = "1.0",
            companyId,
            entityId = party.AgencyId,
            agencyId = party.AgencyId,
            agencyName = party.AgencyName,
            provinceId = party.ProvinceId,
            operatorId = Guid.NewGuid(),
            effectiveAt = DateTimeOffset.UtcNow,
            correlationId = "corr-hierarchy"
        }), ("event-id", Guid.NewGuid().ToByteArray()));

    public static IncomingMessage Raw(string topic, string value, params (string Key, byte[] Value)[] headers) =>
        Record(topic, null, value, headers);

    public static SaleParty Party(Guid province, string agency, string rep) =>
        new(province, Guid.NewGuid(), agency, Guid.NewGuid(), Guid.NewGuid(), rep);

    private static IncomingMessage Record(string topic, string? key, string value, params (string Key, byte[] Value)[] headers) => new(
        topic,
        0,
        Interlocked.Increment(ref _offset),
        DateTimeOffset.UtcNow,
        key,
        headers.Select(header => new KeyValuePair<string, byte[]?>(header.Key, header.Value)).ToList(),
        Encoding.UTF8.GetBytes(value));
}
