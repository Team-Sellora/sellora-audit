using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Application.Dependencies;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Integration;

/// <summary>
/// US-E7-2 against PostgreSQL as audit_app, with orders across three
/// agencies and a month end: figures match a hand calculation, cancelled
/// orders leave sales, Area Managers see only their provinces, and a
/// rebuild reproduces identical figures.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class ReportingTests(PostgreSqlFixture fixture)
{
    private static readonly DateOnly September1 = new(2026, 9, 1);
    private static readonly DateOnly September30 = new(2026, 9, 30);
    private static readonly DateOnly October1 = new(2026, 10, 1);
    private static readonly DateOnly October31 = new(2026, 10, 31);

    private sealed record Seed(Guid CompanyId, Guid Western, Guid Southern, SaleParty Colombo, SaleParty Gampaha, SaleParty Galle, SaleParty IdleRep);

    [Fact]
    public async Task The_september_summary_matches_a_manual_calculation()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);

        var summary = await harness.Reports.SummaryAsync(WholeCompany(seed), new SalesSummaryQuery(September1, September30), CancellationToken.None);

        // Confirmed in September (Colombo): 1000 + 2000 + 1500 (23:30 on the 30th). The 3000 order was
        // cancelled and the 700 order was confirmed at 00:30 on 1 October.
        Assert.Equal(4500m, summary.Totals.SalesValue);
        Assert.Equal(3, summary.Totals.OrderCount);
        Assert.Equal(1500m, summary.Totals.AverageOrderValue);
        Assert.Equal(4500m, summary.Totals.Expected);
        Assert.Equal(1000m, summary.Totals.Collected);
        Assert.Equal(3500m, summary.Totals.Outstanding);
        Assert.Equal("LKR", summary.Currency);
        Assert.Equal(30, summary.Daily.Count);
        Assert.Equal(1500m, summary.Daily.Single(day => day.Date == September30).SalesValue);
        Assert.Equal(0m, summary.Daily.Single(day => day.Date == new DateOnly(2026, 9, 11)).SalesValue);
        Assert.Null(summary.Comparison);
    }

    [Fact]
    public async Task The_status_breakdown_counts_orders_placed_in_the_range_with_cancelled_ones_under_cancelled()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);

        var summary = await harness.Reports.SummaryAsync(WholeCompany(seed), new SalesSummaryQuery(September1, September30), CancellationToken.None);

        Assert.Equal(new[] { new StatusCount("Confirmed", 3), new StatusCount("Cancelled", 1), new StatusCount("Placed", 1) }, summary.StatusBreakdown);
    }

    [Fact]
    public async Task The_comparison_period_gives_the_right_deltas()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);

        var summary = await harness.Reports.SummaryAsync(
            WholeCompany(seed), new SalesSummaryQuery(October1, October31, September1, September30), CancellationToken.None);

        Assert.Equal(700m, summary.Totals.SalesValue);
        Assert.Equal(4500m, summary.Comparison!.Totals.SalesValue);
        Assert.Equal(-3800m, summary.Comparison.Deltas.SalesValue.Absolute);
        Assert.Equal(-84.4m, summary.Comparison.Deltas.SalesValue.Percent);
        Assert.Equal(-2m, summary.Comparison.Deltas.OrderCount.Absolute);
        Assert.Equal(-100m, summary.Comparison.Deltas.Collected.Percent);
    }

    [Fact]
    public async Task An_area_manager_sees_only_their_province_in_every_figure()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);
        var scope = new ReportScope(seed.CompanyId, new[] { seed.Southern });

        var summary = await harness.Reports.SummaryAsync(scope, new SalesSummaryQuery(September1, September30), CancellationToken.None);
        var agencies = await harness.Reports.BreakdownAsync(scope, new SalesBreakdownQuery("agency", September1, September30), CancellationToken.None);
        var reps = await harness.Reports.BreakdownAsync(scope, new SalesBreakdownQuery("rep", September1, September30), CancellationToken.None);
        var territories = await harness.Reports.BreakdownAsync(scope, new SalesBreakdownQuery("territory", September1, September30), CancellationToken.None);

        Assert.Equal(2000m, summary.Totals.SalesValue);
        Assert.Equal(new[] { new StatusCount("Confirmed", 1) }, summary.StatusBreakdown);
        Assert.Equal(seed.Galle.AgencyId, Assert.Single(agencies.Rows).Key);
        Assert.Equal(seed.Galle.RepId, Assert.Single(reps.Rows).Key);
        Assert.Equal(seed.Galle.TerritoryId, Assert.Single(territories.Rows).Key);
    }

    [Fact]
    public async Task The_rep_breakdown_shows_a_rep_with_no_sales_as_zero_and_sorts_both_ways()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);

        var byValue = await harness.Reports.BreakdownAsync(WholeCompany(seed), new SalesBreakdownQuery("rep", September1, September30, "value", "desc"), CancellationToken.None);
        var byCount = await harness.Reports.BreakdownAsync(WholeCompany(seed), new SalesBreakdownQuery("rep", September1, September30, "count", "asc"), CancellationToken.None);

        Assert.Equal(4, byValue.Rows.Count);
        Assert.Contains(byValue.Rows, row => row.Key == seed.IdleRep.RepId && row.Name == "Idle Rep" && row.OrderCount == 0 && row.SalesValue == 0m);
        Assert.Equal(seed.Galle.RepId, byValue.Rows[0].Key);
        Assert.Equal(seed.IdleRep.RepId, byCount.Rows[0].Key);
        Assert.Equal(4500m, byValue.TotalSalesValue);
        Assert.Equal(3, byValue.TotalOrderCount);
    }

    [Fact]
    public async Task The_agency_and_product_breakdowns_add_up_to_the_sales_total()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);

        var agencies = await harness.Reports.BreakdownAsync(WholeCompany(seed), new SalesBreakdownQuery("agency", September1, September30, "name", "asc"), CancellationToken.None);
        var products = await harness.Reports.BreakdownAsync(WholeCompany(seed), new SalesBreakdownQuery("product", September1, September30), CancellationToken.None);

        Assert.Equal(new[] { "Colombo Agency", "Galle Agency", "Gampaha Agency" }, agencies.Rows.Select(row => row.Name));
        Assert.Equal(4500m, agencies.TotalSalesValue);
        var product = Assert.Single(products.Rows);
        Assert.Equal("Anchor 400g", product.Name);
        Assert.Equal(4500m, product.SalesValue);
        Assert.Equal(3, product.OrderCount);
    }

    [Fact]
    public async Task Territory_names_come_from_organization_and_are_kept_on_the_dimension()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);
        harness.Organization.Names = new HierarchyNames(new Dictionary<Guid, string> { [seed.Colombo.TerritoryId] = "Colombo 01" });

        var territories = await harness.Reports.BreakdownAsync(WholeCompany(seed), new SalesBreakdownQuery("territory", September1, September30), CancellationToken.None);

        Assert.Equal(4500m, territories.TotalSalesValue);
        Assert.Equal("Colombo 01", territories.Rows.Single(row => row.Key == seed.Colombo.TerritoryId).Name);

        await using var db = fixture.CreateAppContext(new TenantStub(seed.CompanyId));
        Assert.Equal("Colombo 01", (await db.TerritoryDimensions.SingleAsync(territory => territory.TerritoryId == seed.Colombo.TerritoryId)).Name);
    }

    [Fact]
    public async Task With_organization_down_the_territory_breakdown_still_answers_with_short_ids()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);
        harness.Organization.Unavailable = true;

        var territories = await harness.Reports.BreakdownAsync(WholeCompany(seed), new SalesBreakdownQuery("territory", September1, September30), CancellationToken.None);

        Assert.Equal(1, harness.Organization.NameCalls);
        Assert.Equal(4500m, territories.TotalSalesValue);
        Assert.Equal($"Territory {seed.Galle.TerritoryId:N}"[..18], territories.Rows.Single(row => row.Key == seed.Galle.TerritoryId).Name);
    }

    [Fact]
    public async Task An_empty_range_returns_zeros_and_a_full_daily_series()
    {
        var seed = await SeedAsync();
        await using var harness = new AuditHarness(fixture, seed.CompanyId);

        var summary = await harness.Reports.SummaryAsync(WholeCompany(seed), new SalesSummaryQuery(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 7)), CancellationToken.None);

        Assert.Equal(0m, summary.Totals.SalesValue);
        Assert.Equal(0m, summary.Totals.AverageOrderValue);
        Assert.Equal(7, summary.Daily.Count);
        Assert.Empty(summary.StatusBreakdown);
    }

    [Fact]
    public async Task A_rebuild_from_the_audit_log_reproduces_identical_figures()
    {
        var seed = await SeedAsync();
        var scope = WholeCompany(seed);
        var range = new SalesSummaryQuery(September1, October31, September1, September30);

        await using var harness = new AuditHarness(fixture, seed.CompanyId);
        var before = await harness.Reports.SummaryAsync(scope, range, CancellationToken.None);
        var breakdownsBefore = await BreakdownsAsync(harness, scope);

        var rebuilt = await harness.Rebuild.RebuildAsync(seed.CompanyId, CancellationToken.None);

        var after = await harness.Reports.SummaryAsync(scope, range, CancellationToken.None);
        var breakdownsAfter = await BreakdownsAsync(harness, scope);

        Assert.Equal(6, rebuilt.OrdersProjected);
        Assert.Equal(17, rebuilt.EntriesReplayed);
        Assert.Equal(before.Totals, after.Totals);
        Assert.Equal(before.StatusBreakdown, after.StatusBreakdown);
        Assert.Equal(before.Daily, after.Daily);
        Assert.Equal(before.Comparison, after.Comparison);
        Assert.Equal(breakdownsBefore, breakdownsAfter);
    }

    private static async Task<List<BreakdownRow>> BreakdownsAsync(AuditHarness harness, ReportScope scope)
    {
        var rows = new List<BreakdownRow>();

        foreach (var groupBy in BreakdownGroupings.All)
        {
            var breakdown = await harness.Reports.BreakdownAsync(scope, new SalesBreakdownQuery(groupBy, September1, October31), CancellationToken.None);
            rows.AddRange(breakdown.Rows);
        }

        return rows;
    }

    private async Task<Seed> SeedAsync()
    {
        var companyId = Guid.NewGuid();
        var western = Guid.NewGuid();
        var southern = Guid.NewGuid();
        var colombo = EventBuilder.Party(western, "Colombo Agency", "Nimal Perera");
        var gampaha = EventBuilder.Party(western, "Gampaha Agency", "Kamal Silva");
        var galle = EventBuilder.Party(southern, "Galle Agency", "Sunil Fernando");
        var idle = colombo with { RepId = Guid.NewGuid(), RepName = "Idle Rep" };

        var messages = new List<IncomingMessage>
        {
            EventBuilder.AgencyRegistered(companyId, colombo),
            EventBuilder.AgencyRegistered(companyId, gampaha),
            EventBuilder.AgencyRegistered(companyId, galle),
            EventBuilder.SalesRepAssigned(companyId, idle)
        };

        // Colombo: 1000, confirmed and paid on 10 September.
        messages.AddRange(Sale(companyId, colombo, "ORD-SEED-1", new DateTimeOffset(2026, 9, 10, 4, 0, 0, TimeSpan.Zero), 1000m, paid: true));
        // Galle: 2000, confirmed on 15 September, on credit.
        messages.AddRange(Sale(companyId, galle, "ORD-SEED-2", new DateTimeOffset(2026, 9, 15, 4, 0, 0, TimeSpan.Zero), 2000m, paid: false));
        // Gampaha: 1500, confirmed at 23:30 Colombo time on 30 September (18:00 UTC).
        messages.AddRange(Sale(companyId, gampaha, "ORD-SEED-3", new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero), 1500m, paid: false));
        // Gampaha: 700, confirmed at 00:30 Colombo time on 1 October (19:00 UTC on 30 September).
        messages.AddRange(Sale(companyId, gampaha, "ORD-SEED-4", new DateTimeOffset(2026, 9, 30, 19, 0, 0, TimeSpan.Zero), 700m, paid: false));
        // Colombo: 3000, confirmed then cancelled in September.
        var cancelled = Guid.NewGuid();
        messages.AddRange(Sale(companyId, colombo, "ORD-SEED-5", new DateTimeOffset(2026, 9, 20, 4, 0, 0, TimeSpan.Zero), 3000m, paid: false, orderId: cancelled));
        messages.Add(EventBuilder.Order(companyId, "OrderCancelled", cancelled, "ORD-SEED-5", colombo, new DateTimeOffset(2026, 9, 20, 5, 0, 0, TimeSpan.Zero), 3000m, "Cancelled"));
        // Colombo: 400, placed on 25 September and never confirmed.
        messages.Add(EventBuilder.Order(companyId, "OrderPlaced", Guid.NewGuid(), "ORD-SEED-6", colombo, new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero), 400m, "PendingApproval"));

        await using var harness = new AuditHarness(fixture, companyId);
        await harness.IngestAllAsync(messages);

        return new Seed(companyId, western, southern, colombo, gampaha, galle, idle);
    }

    private static IEnumerable<IncomingMessage> Sale(
        Guid companyId, SaleParty party, string reference, DateTimeOffset confirmedAt, decimal total, bool paid, Guid? orderId = null)
    {
        var id = orderId ?? Guid.NewGuid();
        yield return EventBuilder.Order(companyId, "OrderPlaced", id, reference, party, confirmedAt.AddMinutes(-30), total, "PendingApproval");
        yield return EventBuilder.Order(companyId, "OrderConfirmed", id, reference, party, confirmedAt, total, "Confirmed");

        if (paid)
        {
            yield return EventBuilder.Order(companyId, "PaymentRecorded", id, reference, party, confirmedAt.AddMinutes(1), total, "Confirmed", paymentAmount: total);
        }
    }

    private static ReportScope WholeCompany(Seed seed) => new(seed.CompanyId, null);
}
