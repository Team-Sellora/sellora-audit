using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Domain.Reporting;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Reporting;

/// <summary>
/// US-E7-2-BE1: keeps the reporting schema in step with the audit log. Runs
/// inside the append's transaction and leaves saving to the caller.
/// </summary>
public sealed class ReportingProjector(AuditDbContext db, TimeProvider clock) : IReportingProjector
{
    /// <summary>Data that will fail however often it is retried, from the payload or from PostgreSQL.</summary>
    public static bool IsDataError(Exception exception) =>
        exception is FormatException or OverflowException or JsonException ||
        AuditDatabase.IsPermanentDataError(exception);

    public async Task ProjectAsync(AuditEventContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        foreach (var change in ProjectionEventReader.ReadDimensionChanges(content))
        {
            await ApplyAsync(content.CompanyId, change, cancellationToken);
        }

        if (ProjectionEventReader.ReadOrderEvent(content) is { } order)
        {
            await ApplyAsync(content.CompanyId, order, cancellationToken);
        }
    }

    private async Task ApplyAsync(Guid companyId, OrderEventData order, CancellationToken cancellationToken)
    {
        var fact = await db.OrderFacts.FindAsync(new object[] { companyId, order.OrderId }, cancellationToken);

        if (fact is null)
        {
            fact = OrderFact.Start(companyId, order.OrderId);
            db.OrderFacts.Add(fact);
        }
        else
        {
            await db.Entry(fact).Collection(existing => existing.Lines).LoadAsync(cancellationToken);
        }

        fact.Apply(order, clock.GetUtcNow());
    }

    private async Task ApplyAsync(Guid companyId, DimensionChange change, CancellationToken cancellationToken)
    {
        switch (change.Kind)
        {
            case DimensionKind.Agency:
            {
                var agency = await db.AgencyDimensions.FindAsync(new object[] { companyId, change.Id }, cancellationToken);
                if (agency is null)
                {
                    agency = AgencyDimension.Start(companyId, change.Id);
                    db.AgencyDimensions.Add(agency);
                }

                agency.Update(change.Name, change.ProvinceId);
                break;
            }
            case DimensionKind.Territory:
            {
                var territory = await db.TerritoryDimensions.FindAsync(new object[] { companyId, change.Id }, cancellationToken);
                if (territory is null)
                {
                    territory = TerritoryDimension.Start(companyId, change.Id);
                    db.TerritoryDimensions.Add(territory);
                }

                territory.Update(change.AgencyId, change.ProvinceId ?? await ProvinceOfAgencyAsync(companyId, change.AgencyId, cancellationToken));
                break;
            }
            case DimensionKind.Rep:
            {
                var rep = await db.RepDimensions.FindAsync(new object[] { companyId, change.Id }, cancellationToken);
                if (rep is null)
                {
                    rep = RepDimension.Start(companyId, change.Id);
                    db.RepDimensions.Add(rep);
                }

                // SalesRepAssigned names the agency but not the province; take it from the agency.
                rep.Update(change.Name, change.TerritoryId, change.AgencyId,
                    change.ProvinceId ?? await ProvinceOfAgencyAsync(companyId, change.AgencyId, cancellationToken));
                break;
            }
        }
    }

    private async Task<Guid?> ProvinceOfAgencyAsync(Guid companyId, Guid? agencyId, CancellationToken cancellationToken)
    {
        if (agencyId is not { } id)
        {
            return null;
        }

        var agency = await db.AgencyDimensions.FindAsync(new object[] { companyId, id }, cancellationToken);
        return agency?.ProvinceId;
    }
}
