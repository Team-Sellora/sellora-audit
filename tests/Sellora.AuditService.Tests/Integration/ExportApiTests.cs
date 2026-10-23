using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Domain.Exports;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Integration;

/// <summary>US-E7-3-BE1/BE3 on PostgreSQL: token scope, requester-only access and expiry.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class ExportApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string Export = "/api/reports/export";
    private readonly Guid _company = Guid.NewGuid();
    private readonly Guid _myProvince = Guid.NewGuid();
    private readonly Guid _otherProvince = Guid.NewGuid();
    private DatabaseApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new DatabaseApi(fixture.AppConnectionString);
        var mine = EventBuilder.Party(_myProvince, "Kandy Agency", "Ravi Perera");
        var other = EventBuilder.Party(_otherProvince, "Jaffna Agency", "Kumar Selvam");
        var confirmed = new DateTimeOffset(2026, 9, 5, 4, 0, 0, TimeSpan.Zero);

        await using var harness = new AuditHarness(fixture);
        await harness.IngestAllAsync(new[]
        {
            EventBuilder.AgencyRegistered(_company, mine),
            EventBuilder.AgencyRegistered(_company, other),
            EventBuilder.SalesRepAssigned(_company, mine),
            EventBuilder.SalesRepAssigned(_company, other),
            EventBuilder.Order(_company, "OrderConfirmed", Guid.NewGuid(), "ORD-EXP-K", mine, confirmed, 900m, "Confirmed"),
            EventBuilder.Order(_company, "OrderConfirmed", Guid.NewGuid(), "ORD-EXP-J", other, confirmed, 400m, "Confirmed")
        });
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Theory]
    [InlineData("sales-summary", "")]
    [InlineData("sales-breakdown", "agency")]
    [InlineData("sales-breakdown", "rep")]
    [InlineData("sales-breakdown", "territory")]
    public async Task An_area_manager_export_holds_only_their_provinces(string reportType, string groupBy)
    {
        var subject = $"am-{Guid.NewGuid():N}";
        _api.Organization.Scope = new CallerScope(null, null, null, new[] { _myProvince });
        using var client = _api.CreateClientAs("AreaManager", _company, subject);

        var exportId = await SubmitAsync(client, new
        {
            reportType,
            format = "csv",
            provinceIds = new[] { _otherProvince },
            filters = new { from = "2026-09-01", to = "2026-09-30", groupBy = groupBy.Length == 0 ? null : groupBy }
        });

        await using (var owner = fixture.CreateOwnerContext(new TenantStub(_company)))
        {
            var row = await owner.ReportExports.AsNoTracking().SingleAsync(export => export.ExportId == exportId);
            Assert.Equal(new[] { _myProvince }, row.ScopeProvinceIds);
            Assert.Equal(subject, row.RequesterSub);
            Assert.Equal(ExportStatus.Queued, row.Status);
        }

        await ExportRunner.DrainAsync(ExportRunner.Worker(_api.Services));

        var status = await client.GetFromJsonAsync<ExportStatusResponse>($"{Export}/{exportId}");
        Assert.Equal("Ready", status!.Status);
        Assert.Equal($"/api/reports/export/{exportId}/file", status.DownloadPath);

        var response = await client.GetAsync(status.DownloadPath);
        var text = string.Join("\n", ExportRunner.CsvLines(await response.Content.ReadAsByteArrayAsync()));
        var fileName = response.Content.Headers.ContentDisposition!.FileName!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"Scope,Provinces: {_myProvince:D}", text);
        Assert.Contains("900.00", text);
        Assert.DoesNotContain("Jaffna", text);
        Assert.DoesNotContain("Kumar", text);
        Assert.DoesNotContain("400.00", text);
        Assert.DoesNotContain("1300.00", text);
        Assert.DoesNotContain(_otherProvince.ToString("D"), text);
        Assert.DoesNotContain(_otherProvince.ToString("D"), fileName);
        Assert.DoesNotContain(_myProvince.ToString("D"), fileName);
    }

    [Fact]
    public async Task Only_the_requester_sees_the_status_the_file_and_the_list()
    {
        using var owner = _api.CreateClientAs("CompanyAdmin", _company, "admin-owner");
        using var colleague = _api.CreateClientAs("CompanyAdmin", _company, "admin-colleague");
        using var otherCompany = _api.CreateClientAs("CompanyAdmin", Guid.NewGuid(), "admin-owner");

        var exportId = await SubmitAsync(owner, new { reportType = "sales-summary", format = "pdf", filters = new { from = "2026-09-01", to = "2026-09-30" } });
        await ExportRunner.DrainAsync(ExportRunner.Worker(_api.Services));

        Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync($"{Export}/{exportId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync($"{Export}/{exportId}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherCompany.GetAsync($"{Export}/{exportId}/file")).StatusCode);
        Assert.DoesNotContain(await colleague.GetFromJsonAsync<List<ExportStatusResponse>>(Export) ?? new(), export => export.ExportId == exportId);

        var file = await owner.GetAsync($"{Export}/{exportId}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await file.Content.ReadAsByteArrayAsync())[..4]));
    }

    [Fact]
    public async Task My_exports_lists_the_latest_twenty_newest_first()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company, $"lister-{Guid.NewGuid():N}");
        var ids = new List<Guid>();

        for (var index = 0; index < 22; index++)
        {
            ids.Add(await SubmitAsync(client, new { reportType = "sales-summary", format = "csv", filters = new { from = "2026-09-01", to = "2026-09-30" } }));
        }

        var mine = await client.GetFromJsonAsync<List<ExportStatusResponse>>(Export);

        Assert.Equal(20, mine!.Count);
        Assert.Equal(ids[^1], mine[0].ExportId);
        Assert.DoesNotContain(mine, export => export.ExportId == ids[0] || export.ExportId == ids[1]);
        Assert.All(mine, export => Assert.Equal("Queued", export.Status));

        await ExportRunner.DrainAsync(ExportRunner.Worker(_api.Services));
    }

    [Fact]
    public async Task An_expired_file_is_410_and_the_cleanup_drops_its_content()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company, "admin-expiry");
        var exportId = await SubmitAsync(client, new { reportType = "sales-summary", format = "csv", filters = new { from = "2026-09-01", to = "2026-09-30" } });
        await ExportRunner.DrainAsync(ExportRunner.Worker(_api.Services));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Export}/{exportId}/file")).StatusCode);

        await fixture.ExecuteAsOwnerAsync($"UPDATE reporting.report_export SET expires_at = now() - interval '1 minute' WHERE export_id = '{exportId}'");

        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"{Export}/{exportId}/file")).StatusCode);
        Assert.Equal("Expired", (await client.GetFromJsonAsync<ExportStatusResponse>($"{Export}/{exportId}"))!.Status);

        Assert.True(await ExportRunner.Cleanup(_api.Services).ExpireAsync(CancellationToken.None) >= 1);

        await using var owner = fixture.CreateOwnerContext(new TenantStub(_company));
        var row = await owner.ReportExports.AsNoTracking().SingleAsync(export => export.ExportId == exportId);
        Assert.Equal(ExportStatus.Expired, row.Status);
        Assert.Null(row.Content);
        Assert.NotNull(row.FileName);
        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"{Export}/{exportId}/file")).StatusCode);
    }

    [Fact]
    public async Task A_queued_export_is_not_ready_yet()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company, "admin-early");
        var exportId = await SubmitAsync(client, new { reportType = "sales-summary", format = "csv", filters = new { from = "2026-09-01", to = "2026-09-30" } });

        var status = await client.GetFromJsonAsync<ExportStatusResponse>($"{Export}/{exportId}");

        Assert.Equal("Queued", status!.Status);
        Assert.Null(status.DownloadPath);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"{Export}/{exportId}/file")).StatusCode);

        await ExportRunner.DrainAsync(ExportRunner.Worker(_api.Services));
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Export, body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExportAccepted>())!.ExportId;
    }

    private sealed class DatabaseApi(string connectionString) : AuditApiFactory(connectionString);
}
