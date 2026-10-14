using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Sellora.AuditService.Api.Authorization;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Api;

/// <summary>
/// US-E7-2-BE3..BE6: who may call the report routes and which requests are
/// refused. The host points at an unreachable database, so none of these
/// answers can come from data.
/// </summary>
public sealed class ReportsApiSecurityTests : IClassFixture<ReportsApiSecurityTests.DownOrganizationApi>
{
    private const string Summary = "/api/reports/sales-summary?from=2026-09-01&to=2026-09-30";
    private const string Breakdown = "/api/reports/sales-breakdown?groupBy=rep&from=2026-09-01&to=2026-09-30";
    private const string Rebuild = "/api/reports/projections/rebuild";
    private static readonly Guid Company = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private readonly DownOrganizationApi _api;

    public ReportsApiSecurityTests(DownOrganizationApi api) => _api = api;

    [Theory]
    [InlineData("CompanyAdmin", true)]
    [InlineData("Internal/CompanyAdmin", true)]
    [InlineData("AreaManager", true)]
    [InlineData("Internal/AreaManager", true)]
    [InlineData("AgencyOperator", false)]
    [InlineData("SalesRep", false)]
    [InlineData("ShopOwner", false)]
    public async Task Only_admins_and_area_managers_read_reports(string role, bool allowed)
    {
        Assert.Equal(allowed, await AllowedAsync(RolePolicies.RequireReportReader, role));
    }

    [Theory]
    [InlineData("GET", Summary)]
    [InlineData("GET", Breakdown)]
    [InlineData("POST", Rebuild)]
    public async Task An_anonymous_caller_is_unauthorised(string verb, string path)
    {
        using var client = _api.CreateClientAs(roles: null);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(verb), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("SalesRep", "GET", Summary)]
    [InlineData("AgencyOperator", "GET", Breakdown)]
    [InlineData("ShopOwner", "GET", Summary)]
    [InlineData("AreaManager", "POST", Rebuild)]
    [InlineData("SalesRep", "POST", Rebuild)]
    public async Task A_caller_without_the_role_is_forbidden(string role, string verb, string path)
    {
        using var client = _api.CreateClientAs(role, Company);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(verb), path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_a_company_is_unauthorised()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", companyId: null);

        var response = await client.GetAsync(Summary);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/reports/sales-summary?from=2026-10-02&to=2026-10-01")]
    [InlineData("/api/reports/sales-summary?from=2024-01-01&to=2027-01-05")]
    [InlineData("/api/reports/sales-summary?from=2026-09-01")]
    [InlineData("/api/reports/sales-summary?from=2026-09-31&to=2026-10-01")]
    [InlineData("/api/reports/sales-summary?from=2026-09-01&to=2026-09-30&compareFrom=2026-08-01")]
    [InlineData("/api/reports/sales-summary?from=2026-09-01&to=2026-09-30&compareFrom=2026-08-31&compareTo=2026-08-01")]
    [InlineData("/api/reports/sales-breakdown?groupBy=shop&from=2026-09-01&to=2026-09-30")]
    [InlineData("/api/reports/sales-breakdown?from=2026-09-01&to=2026-09-30")]
    [InlineData("/api/reports/sales-breakdown?groupBy=rep&from=2026-10-02&to=2026-10-01")]
    [InlineData("/api/reports/sales-breakdown?groupBy=rep&from=2024-01-01&to=2027-01-05")]
    [InlineData("/api/reports/sales-breakdown?groupBy=rep&from=2026-09-01&to=2026-09-30&sort=total")]
    [InlineData("/api/reports/sales-breakdown?groupBy=rep&from=2026-09-01&to=2026-09-30&direction=up")]
    public async Task An_invalid_request_is_a_bad_request(string path)
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Organization_being_down_is_a_503_for_an_area_manager_never_an_unscoped_report()
    {
        using var client = _api.CreateClientAs("AreaManager", Company, subject: $"am-{Guid.NewGuid():N}");

        var response = await client.GetAsync(Summary);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static async Task<bool> AllowedAsync(string policy, string role)
    {
        var authorization = new ServiceCollection()
            .AddLogging()
            .AddAuthorizationCore(options => options.AddSelloraRolePolicies())
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("roles", role) }, "test"));
        return (await authorization.AuthorizeAsync(user, null, policy)).Succeeded;
    }

    public sealed class DownOrganizationApi : AuditApiFactory
    {
        public DownOrganizationApi() => Organization.Unavailable = true;
    }
}
