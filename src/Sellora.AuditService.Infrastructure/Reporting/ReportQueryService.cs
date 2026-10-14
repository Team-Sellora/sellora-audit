using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sellora.AuditService.Application.Dependencies;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Domain.Reporting;
using Sellora.AuditService.Infrastructure.Dependencies;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Reporting;

/// <summary>
/// US-E7-2-BE3/BE4: aggregates order_fact at query time. A sale is a
/// Confirmed order counted on its Colombo SaleDate; a cancelled order is no
/// longer Confirmed and so leaves every sales figure.
/// </summary>
public sealed class ReportQueryService(
    AuditDbContext db,
    IOrganizationClient organization,
    IAccessTokenAccessor tokens,
    ILogger<ReportQueryService> logger) : IReportQueryService
{
    private const string Currency = "LKR";

    public async Task<SalesSummaryResponse> SummaryAsync(ReportScope scope, SalesSummaryQuery query, CancellationToken cancellationToken)
    {
        var totals = await TotalsAsync(scope, query.From, query.To, cancellationToken);

        var daily = await Sales(scope, query.From, query.To)
            .GroupBy(fact => fact.SaleDate!.Value)
            .Select(group => new { Date = group.Key, Value = group.Sum(fact => fact.Total), Count = group.Count() })
            .ToListAsync(cancellationToken);

        var statuses = await Facts(scope)
            .Where(fact => fact.PlacedDate >= query.From && fact.PlacedDate <= query.To)
            .GroupBy(fact => fact.Status)
            .Select(group => new StatusCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        SalesComparison? comparison = null;

        if (query.CompareFrom is { } compareFrom && query.CompareTo is { } compareTo)
        {
            var previous = await TotalsAsync(scope, compareFrom, compareTo, cancellationToken);
            comparison = new SalesComparison(compareFrom, compareTo, previous, ReportMath.Deltas(totals, previous));
        }

        return new SalesSummaryResponse(
            query.From,
            query.To,
            Currency,
            totals,
            statuses.OrderByDescending(status => status.Count).ThenBy(status => status.Status, StringComparer.Ordinal).ToList(),
            ReportMath.FillDaily(query.From, query.To, daily.ToDictionary(day => day.Date, day => (day.Value, day.Count))),
            comparison);
    }

    public async Task<SalesBreakdownResponse> BreakdownAsync(ReportScope scope, SalesBreakdownQuery query, CancellationToken cancellationToken)
    {
        var rows = query.GroupBy switch
        {
            BreakdownGroupings.Rep => await ByRepAsync(scope, query, cancellationToken),
            BreakdownGroupings.Territory => await ByTerritoryAsync(scope, query, cancellationToken),
            BreakdownGroupings.Agency => await ByAgencyAsync(scope, query, cancellationToken),
            _ => await ByProductAsync(scope, query, cancellationToken)
        };

        var sorted = ReportMath.Sort(rows, query.Sort, query.Direction);

        return new SalesBreakdownResponse(
            query.GroupBy,
            query.From,
            query.To,
            query.Sort,
            query.Direction,
            Currency,
            sorted,
            sorted.Sum(row => row.OrderCount),
            sorted.Sum(row => row.SalesValue));
    }

    private IQueryable<OrderFact> Facts(ReportScope scope)
    {
        var facts = db.OrderFacts.AsNoTracking().Where(fact => fact.CompanyId == scope.CompanyId);

        if (scope.IsWholeCompany)
        {
            return facts;
        }

        var provinces = scope.ProvinceIds!.ToList();
        return facts.Where(fact => fact.ProvinceId != null && provinces.Contains(fact.ProvinceId.Value));
    }

    private IQueryable<OrderFact> Sales(ReportScope scope, DateOnly from, DateOnly to) =>
        Facts(scope).Where(fact =>
            fact.Status == OrderFactStatus.Confirmed &&
            fact.SaleDate >= from &&
            fact.SaleDate <= to);

    private async Task<SalesFigures> TotalsAsync(ReportScope scope, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var totals = await Sales(scope, from, to)
            .GroupBy(_ => 1)
            .Select(group => new { Value = group.Sum(fact => fact.Total), Count = group.Count(), Paid = group.Sum(fact => fact.PaidAmount) })
            .SingleOrDefaultAsync(cancellationToken);

        return ReportMath.Figures(totals?.Value ?? 0m, totals?.Count ?? 0, totals?.Paid ?? 0m);
    }

    private static List<Guid>? Provinces(ReportScope scope) => scope.ProvinceIds?.ToList();

    private async Task<IReadOnlyList<BreakdownRow>> ByRepAsync(ReportScope scope, SalesBreakdownQuery query, CancellationToken cancellationToken)
    {
        var sales = await Sales(scope, query.From, query.To)
            .Where(fact => fact.SalesRepId != null)
            .GroupBy(fact => new { fact.SalesRepId, fact.SalesRepName })
            .Select(group => new { group.Key.SalesRepId, group.Key.SalesRepName, Count = group.Count(), Value = group.Sum(fact => fact.Total) })
            .ToListAsync(cancellationToken);

        var provinces = Provinces(scope);
        var reps = await db.RepDimensions.AsNoTracking()
            .Where(rep => rep.CompanyId == scope.CompanyId)
            .Where(rep => provinces == null || (rep.ProvinceId != null && provinces.Contains(rep.ProvinceId.Value)))
            .Select(rep => new { rep.SalesRepId, rep.Name })
            .ToListAsync(cancellationToken);

        return ReportMath.WithZeroRows(
            Merge(sales.Select(row => (row.SalesRepId!.Value, row.SalesRepName, row.Count, row.Value))),
            reps.ToDictionary(rep => rep.SalesRepId, rep => rep.Name),
            id => ReportMath.ShortName("Rep", id));
    }

    private async Task<IReadOnlyList<BreakdownRow>> ByAgencyAsync(ReportScope scope, SalesBreakdownQuery query, CancellationToken cancellationToken)
    {
        var sales = await Sales(scope, query.From, query.To)
            .Where(fact => fact.AgencyId != null)
            .GroupBy(fact => new { fact.AgencyId, fact.AgencyName })
            .Select(group => new { group.Key.AgencyId, group.Key.AgencyName, Count = group.Count(), Value = group.Sum(fact => fact.Total) })
            .ToListAsync(cancellationToken);

        var provinces = Provinces(scope);
        var agencies = await db.AgencyDimensions.AsNoTracking()
            .Where(agency => agency.CompanyId == scope.CompanyId)
            .Where(agency => provinces == null || (agency.ProvinceId != null && provinces.Contains(agency.ProvinceId.Value)))
            .Select(agency => new { agency.AgencyId, agency.Name })
            .ToListAsync(cancellationToken);

        return ReportMath.WithZeroRows(
            Merge(sales.Select(row => (row.AgencyId!.Value, row.AgencyName, row.Count, row.Value))),
            agencies.ToDictionary(agency => agency.AgencyId, agency => agency.Name),
            id => ReportMath.ShortName("Agency", id));
    }

    private async Task<IReadOnlyList<BreakdownRow>> ByTerritoryAsync(ReportScope scope, SalesBreakdownQuery query, CancellationToken cancellationToken)
    {
        var sales = await Sales(scope, query.From, query.To)
            .Where(fact => fact.TerritoryId != null)
            .GroupBy(fact => fact.TerritoryId!.Value)
            .Select(group => new { TerritoryId = group.Key, Count = group.Count(), Value = group.Sum(fact => fact.Total) })
            .ToListAsync(cancellationToken);

        var provinces = Provinces(scope);
        var territories = await db.TerritoryDimensions
            .Where(territory => territory.CompanyId == scope.CompanyId)
            .Where(territory => provinces == null || (territory.ProvinceId != null && provinces.Contains(territory.ProvinceId.Value)))
            .ToListAsync(cancellationToken);

        await NameTerritoriesAsync(territories, cancellationToken);

        return ReportMath.WithZeroRows(
            Merge(sales.Select(row => (row.TerritoryId, (string?)null, row.Count, row.Value))),
            territories.ToDictionary(territory => territory.TerritoryId, territory => territory.Name),
            id => ReportMath.ShortName("Territory", id));
    }

    private async Task<IReadOnlyList<BreakdownRow>> ByProductAsync(ReportScope scope, SalesBreakdownQuery query, CancellationToken cancellationToken)
    {
        var sales = Sales(scope, query.From, query.To);

        var lines = await (
                from line in db.OrderFactLines.AsNoTracking()
                join fact in sales on new { line.CompanyId, line.OrderId } equals new { fact.CompanyId, fact.OrderId }
                group line by new { line.ProductId, line.ProductName } into product
                select new
                {
                    product.Key.ProductId,
                    product.Key.ProductName,
                    Count = product.Select(line => line.OrderId).Distinct().Count(),
                    Value = product.Sum(line => line.LineTotal)
                })
            .ToListAsync(cancellationToken);

        return ReportMath.WithZeroRows(
            Merge(lines.Select(row => (row.ProductId, (string?)row.ProductName, row.Count, row.Value))),
            new Dictionary<Guid, string?>(),
            id => ReportMath.ShortName("Product", id));
    }

    // One row per ID even when its name changed during the range.
    private static Dictionary<Guid, (string? Name, int Count, decimal Value)> Merge(
        IEnumerable<(Guid Id, string? Name, int Count, decimal Value)> rows)
    {
        var merged = new Dictionary<Guid, (string? Name, int Count, decimal Value)>();

        foreach (var (id, name, count, value) in rows)
        {
            merged[id] = merged.TryGetValue(id, out var existing)
                ? (existing.Name ?? name, existing.Count + count, existing.Value + value)
                : (name, count, value);
        }

        return merged;
    }

    /// <summary>
    /// Territory names are not in hierarchy events. On an interactive request
    /// they come from Organization with the caller's token and are kept on the
    /// dimension. If Organization is down the breakdown shows short IDs.
    /// </summary>
    private async Task NameTerritoriesAsync(IReadOnlyList<TerritoryDimension> territories, CancellationToken cancellationToken)
    {
        if (!territories.Any(territory => string.IsNullOrWhiteSpace(territory.Name)) || tokens.GetBearerToken() is null)
        {
            return;
        }

        HierarchyNames names;

        try
        {
            names = await organization.GetHierarchyNamesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DependencyUnavailableException or DependencyRejectedException)
        {
            logger.LogWarning(exception, "Territory names are unavailable from Organization; the breakdown shows IDs");
            return;
        }

        var renamed = false;

        foreach (var territory in territories.Where(territory => string.IsNullOrWhiteSpace(territory.Name)))
        {
            if (names.Territories.TryGetValue(territory.TerritoryId, out var name))
            {
                territory.Rename(name);
                renamed = true;
            }
        }

        if (renamed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
