using System.Text.Json;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Domain.Reporting;

namespace Sellora.AuditService.Application.Reporting;

public enum DimensionKind
{
    Rep,
    Territory,
    Agency
}

/// <summary>A fact about a rep, territory or agency taken from a hierarchy or order event.</summary>
public sealed record DimensionChange(
    DimensionKind Kind,
    Guid Id,
    string? Name,
    Guid? TerritoryId,
    Guid? AgencyId,
    Guid? ProvinceId);

/// <summary>
/// Reads the events the projections use out of an audit payload. Anything
/// else returns nothing and only lives in the audit log.
/// </summary>
public static class ProjectionEventReader
{
    public const string SalesRepAssigned = "SalesRepAssigned";
    public const string AgencyRegistered = "AgencyRegistered";
    public const string TerritoryAssignedToAgency = "TerritoryAssignedToAgency";

    public static OrderEventData? ReadOrderEvent(AuditEventContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.SourceTopic != AggregateRules.OrderTopic || !OrderEventTypes.IsOrderEvent(content.EventType))
        {
            return null;
        }

        using var document = JsonDocument.Parse(content.Payload);
        var root = document.RootElement;

        if (JsonFields.FirstGuid(root, new[] { "orderId", "entityId" }) is not { } orderId)
        {
            return null;
        }

        var agency = JsonFields.Object(root, "agency");
        var salesRep = JsonFields.Object(root, "salesRep");
        var shop = JsonFields.Object(root, "shop");
        var payment = JsonFields.Object(root, "payment");

        return new OrderEventData
        {
            EventType = content.EventType,
            OrderId = orderId,
            OrderReference = JsonFields.String(root, "orderReference") ?? content.OrderReference,
            FulfilmentType = JsonFields.String(root, "fulfilmentType"),
            ProvinceId = JsonFields.Guid(root, "provinceId") ?? content.ProvinceId,
            AgencyId = agency is { } a ? JsonFields.Guid(a, "agencyId") : null,
            AgencyName = agency is { } an ? JsonFields.String(an, "name") : null,
            TerritoryId = JsonFields.Guid(root, "territoryId"),
            SalesRepId = salesRep is { } r ? JsonFields.Guid(r, "salesRepId") : null,
            SalesRepName = salesRep is { } rn ? JsonFields.String(rn, "name") : null,
            ShopId = shop is { } s ? JsonFields.Guid(s, "shopId") : null,
            ShopName = shop is { } sn ? JsonFields.String(sn, "name") : null,
            Total = JsonFields.Decimal(root, "total"),
            Currency = JsonFields.String(root, "currency"),
            OccurredAt = content.OccurredAt,
            OrderDate = JsonFields.Timestamp(root, "orderDate"),
            ConfirmedAt = JsonFields.Timestamp(root, "confirmedAt"),
            CancelledAt = JsonFields.Timestamp(root, "cancelledAt"),
            PaymentAmount = payment is { } p ? JsonFields.Decimal(p, "amount") : null,
            PaymentRecordedAt = payment is { } pr ? JsonFields.Timestamp(pr, "recordedAt") : null,
            Lines = ReadLines(root)
        };
    }

    public static IReadOnlyList<DimensionChange> ReadDimensionChanges(AuditEventContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.SourceTopic == AggregateRules.HierarchyTopic)
        {
            return ReadHierarchyChange(content) is { } change ? new[] { change } : Array.Empty<DimensionChange>();
        }

        if (ReadOrderEvent(content) is not { } order)
        {
            return Array.Empty<DimensionChange>();
        }

        // Reps, agencies and territories seen only through orders still get a row.
        var changes = new List<DimensionChange>();

        if (order.SalesRepId is { } repId)
        {
            changes.Add(new DimensionChange(DimensionKind.Rep, repId, order.SalesRepName, order.TerritoryId, order.AgencyId, order.ProvinceId));
        }

        if (order.AgencyId is { } agencyId)
        {
            changes.Add(new DimensionChange(DimensionKind.Agency, agencyId, order.AgencyName, null, null, order.ProvinceId));
        }

        if (order.TerritoryId is { } territoryId)
        {
            changes.Add(new DimensionChange(DimensionKind.Territory, territoryId, null, null, order.AgencyId, order.ProvinceId));
        }

        return changes;
    }

    private static DimensionChange? ReadHierarchyChange(AuditEventContent content)
    {
        if (content.EventType is not (SalesRepAssigned or AgencyRegistered or TerritoryAssignedToAgency))
        {
            return null;
        }

        using var document = JsonDocument.Parse(content.Payload);
        var root = document.RootElement;

        return content.EventType switch
        {
            SalesRepAssigned when JsonFields.Guid(root, "salesRepId") is { } repId =>
                new DimensionChange(DimensionKind.Rep, repId, JsonFields.String(root, "salesRepName"),
                    JsonFields.Guid(root, "territoryId"), JsonFields.Guid(root, "agencyId"), JsonFields.Guid(root, "provinceId")),
            AgencyRegistered when JsonFields.Guid(root, "agencyId") is { } agencyId =>
                new DimensionChange(DimensionKind.Agency, agencyId, JsonFields.String(root, "agencyName"),
                    null, null, JsonFields.Guid(root, "provinceId")),
            TerritoryAssignedToAgency when JsonFields.Guid(root, "territoryId") is { } territoryId =>
                new DimensionChange(DimensionKind.Territory, territoryId, null,
                    null, JsonFields.Guid(root, "agencyId"), JsonFields.Guid(root, "provinceId")),
            _ => null
        };
    }

    private static IReadOnlyList<OrderLineData> ReadLines(JsonElement root)
    {
        if (!JsonFields.TryGet(root, "lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<OrderLineData>();
        }

        var result = new List<OrderLineData>();

        foreach (var line in lines.EnumerateArray())
        {
            if (JsonFields.Guid(line, "productId") is { } productId)
            {
                result.Add(new OrderLineData(
                    productId,
                    JsonFields.String(line, "productName"),
                    JsonFields.Int(line, "quantity") ?? 0,
                    JsonFields.Decimal(line, "lineTotal") ?? 0m));
            }
        }

        return result;
    }
}

public interface IReportingProjector
{
    /// <summary>Updates the projections for one appended entry, inside the append's transaction. Does not save.</summary>
    Task ProjectAsync(AuditEventContent content, CancellationToken cancellationToken);
}
