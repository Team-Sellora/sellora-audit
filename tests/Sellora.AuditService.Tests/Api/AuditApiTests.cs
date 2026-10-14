using System.Net;
using System.Net.Http.Json;
using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Api;

/// <summary>
/// US-E7-1 AC3/AC5 through the real host, its JSON and its middleware,
/// against PostgreSQL as audit_app: the company always comes from the
/// token, another company's entry is a 404, and an Area Manager sees only
/// their provinces.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class AuditApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly Guid ProvinceA = Guid.NewGuid();
    private static readonly Guid ProvinceB = Guid.NewGuid();

    private readonly Guid _company = Guid.NewGuid();
    private DatabaseApi _api = null!;
    private string _reference = null!;

    public async Task InitializeAsync()
    {
        _api = new DatabaseApi(fixture.AppConnectionString);
        _reference = $"ORD-API-{Guid.NewGuid():N}"[..20];
        var orderId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var partyA = EventBuilder.Party(ProvinceA, "Agency A", "Rep A");

        await using var harness = new AuditHarness(fixture);
        await harness.IngestAllAsync(new[]
        {
            EventBuilder.Order(_company, "OrderPlaced", orderId, _reference, partyA, start, 500m, "Placed"),
            EventBuilder.Order(_company, "OrderConfirmed", orderId, _reference, partyA, start.AddMinutes(1), 500m, "Confirmed"),
            EventBuilder.Order(_company, "OrderPlaced", Guid.NewGuid(), "ORD-API-B", EventBuilder.Party(ProvinceB, "Agency B", "Rep B"), start.AddMinutes(2), 10m, "Placed"),
            EventBuilder.Raw("sellora.catalog.v1", $$"""{"companyId":"{{_company}}","eventType":"PriceChanged","productId":"{{Guid.NewGuid()}}"}""")
        });
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task A_company_admin_searches_an_order_as_a_timeline()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company);

        var page = await client.GetFromJsonAsync<PagedResponse<AuditEntrySummary>>($"/api/audit?orderReference={_reference}");

        Assert.Equal(new[] { "OrderPlaced", "OrderConfirmed" }, page!.Items.Select(item => item.EventType));
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task The_unfiltered_list_is_newest_first_and_the_page_size_is_capped()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company);

        var page = await client.GetFromJsonAsync<PagedResponse<AuditEntrySummary>>("/api/audit?pageSize=1000");

        Assert.Equal(AuditSearchQuery.MaxPageSize, page!.PageSize);
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(page.Items.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.ChainPosition), page.Items);
    }

    [Fact]
    public async Task The_detail_returns_hashes_and_the_payload_exactly_as_received()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company);
        var page = await client.GetFromJsonAsync<PagedResponse<AuditEntrySummary>>($"/api/audit?orderReference={_reference}");

        var detail = await client.GetFromJsonAsync<AuditEntryDetail>($"/api/audit/{page!.Items[0].AuditEntryId}");

        Assert.Equal(64, detail!.Hash.Length);
        Assert.Equal(new string('0', 64), detail.PreviousHash);
        Assert.StartsWith("{\"eventId\":", detail.Payload);
    }

    [Fact]
    public async Task Another_company_gets_404_for_an_entry_and_an_empty_search()
    {
        using var owner = _api.CreateClientAs("CompanyAdmin", _company);
        var entryId = (await owner.GetFromJsonAsync<PagedResponse<AuditEntrySummary>>("/api/audit"))!.Items[0].AuditEntryId;
        using var other = _api.CreateClientAs("CompanyAdmin", Guid.NewGuid());

        var detail = await other.GetAsync($"/api/audit/{entryId}");
        var search = await other.GetFromJsonAsync<PagedResponse<AuditEntrySummary>>($"/api/audit?orderReference={_reference}");

        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Empty(search!.Items);
    }

    [Fact]
    public async Task An_area_manager_sees_only_their_provinces_and_gets_404_outside_them()
    {
        _api.Organization.Scope = new CallerScope(null, null, null, new[] { ProvinceA });
        using var admin = _api.CreateClientAs("CompanyAdmin", _company);
        var outside = (await admin.GetFromJsonAsync<PagedResponse<AuditEntrySummary>>("/api/audit?orderReference=ORD-API-B"))!.Items.Single();
        using var areaManager = _api.CreateClientAs("Internal/AreaManager", _company, subject: $"am-{Guid.NewGuid():N}");

        var page = await areaManager.GetFromJsonAsync<PagedResponse<AuditEntrySummary>>("/api/audit");
        var hidden = await areaManager.GetAsync($"/api/audit/{outside.AuditEntryId}");

        Assert.Equal(2, page!.TotalCount);
        Assert.All(page.Items, item => Assert.Equal(_reference, item.OrderReference));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task The_area_manager_scope_is_cached()
    {
        _api.Organization.Scope = new CallerScope(null, null, null, new[] { ProvinceA });
        using var areaManager = _api.CreateClientAs("AreaManager", _company, subject: $"am-{Guid.NewGuid():N}");
        var before = _api.Organization.Calls;

        await areaManager.GetAsync("/api/audit");
        await areaManager.GetAsync("/api/audit");

        Assert.Equal(1, _api.Organization.Calls - before);
    }

    [Fact]
    public async Task A_company_admin_verifies_the_chain()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", _company);

        var result = await client.GetFromJsonAsync<VerifyChainResponse>("/api/audit/verify");

        Assert.True(result!.Verified);
        Assert.Equal(4, result.EntriesChecked);
        Assert.Null(result.FirstBroken);
    }

    [Fact]
    public async Task Verify_names_the_tampered_entry()
    {
        await fixture.ExecuteAsOwnerAsync(
            $"UPDATE audit.audit_entry SET event_type = 'OrderCancelled' WHERE company_id = '{_company}' AND chain_position = 2");
        using var client = _api.CreateClientAs("CompanyAdmin", _company);

        var json = await client.GetStringAsync("/api/audit/verify");

        Assert.Contains("\"verified\":false", json);
        Assert.Contains("\"chainPosition\":2", json);
        Assert.Contains("\"reason\":\"HashMismatch\"", json);
    }

    private sealed class DatabaseApi(string connectionString) : AuditApiFactory(connectionString);
}
