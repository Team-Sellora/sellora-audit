using System.Net.Http.Json;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Integration;

/// <summary>
/// US-E7-2 through the real host and its JSON against PostgreSQL: the
/// company comes from the token, an Area Manager gets only their provinces,
/// and a Company Admin can rebuild the projections.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class ReportsApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string September = "from=2026-09-01&to=2026-09-30";
    private static readonly Guid ProvinceA = Guid.NewGuid();
    private static readonly Guid ProvinceB = Guid.NewGuid();

    private readonly Guid _company = Guid.NewGuid();
    private DatabaseApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new DatabaseApi(fixture.AppConnectionString);
        var partyA = EventBuilder.Party(ProvinceA, "Agency A", "Rep A");
        var partyB = EventBuilder.Party(ProvinceB, "Agency B", "Rep B");
        var confirmed = new DateTimeOffset(2026, 9, 10, 4, 0, 0, TimeSpan.Zero);
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();

        await using var harness = new AuditHarness(fixture);
        await harness.IngestAllAsync(new[]
        {
            EventBuilder.Order(_company, "OrderConfirmed", orderA, "ORD-RPT-A", partyA, confirmed, 600m, "Confirmed"),
            EventBuilder.Order(_company, "OrderConfirmed", orderB, "ORD-RPT-B", partyB, confirmed, 400m, "Confirmed"),
            EventBuilder.Order(Guid.NewGuid(), "OrderConfirmed", Guid.NewGuid(), "ORD-RPT-X", partyA, confirmed, 9999m, "Confirmed")
        });
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task A_company_admin_sees_the_whole_company_and_never_another_one()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company);

        var summary = await client.GetFromJsonAsync<SalesSummaryResponse>($"/api/reports/sales-summary?{September}");

        Assert.Equal(1000m, summary!.Totals.SalesValue);
        Assert.Equal(2, summary.Totals.OrderCount);
        Assert.Equal(30, summary.Daily.Count);
    }

    [Fact]
    public async Task An_area_manager_sees_only_their_province()
    {
        _api.Organization.Scope = new CallerScope(null, null, null, new[] { ProvinceA });
        using var client = _api.CreateClientAs("AreaManager", _company, subject: $"am-{Guid.NewGuid():N}");

        var summary = await client.GetFromJsonAsync<SalesSummaryResponse>($"/api/reports/sales-summary?{September}");
        var agencies = await client.GetFromJsonAsync<SalesBreakdownResponse>($"/api/reports/sales-breakdown?groupBy=agency&{September}");

        Assert.Equal(600m, summary!.Totals.SalesValue);
        Assert.Equal("Agency A", Assert.Single(agencies!.Rows).Name);
    }

    [Fact]
    public async Task A_company_admin_rebuilds_the_projections()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company);

        var response = await client.PostAsync("/api/reports/projections/rebuild", null);
        var rebuilt = await response.Content.ReadFromJsonAsync<ProjectionRebuildResponse>();

        response.EnsureSuccessStatusCode();
        Assert.Equal(2, rebuilt!.EntriesReplayed);
        Assert.Equal(2, rebuilt.OrdersProjected);
    }

    private sealed class DatabaseApi(string connectionString) : AuditApiFactory(connectionString);
}
