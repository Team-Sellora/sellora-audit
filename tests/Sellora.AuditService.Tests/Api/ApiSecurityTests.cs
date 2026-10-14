using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Sellora.AuditService.Api.Authorization;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Api;

/// <summary>
/// Who may call what (US-E7-1-Q6): only Company Admins and Area Managers
/// read the audit log, only Company Admins verify the chain, and edit verbs
/// on an audit entry are not routes at all. The host points at an
/// unreachable database, so none of these answers can come from data.
/// </summary>
public sealed class ApiSecurityTests : IClassFixture<ApiSecurityTests.DownOrganizationApi>
{
    private static readonly Guid Company = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private readonly DownOrganizationApi _api;

    public ApiSecurityTests(DownOrganizationApi api) => _api = api;

    [Theory]
    [InlineData("CompanyAdmin", true)]
    [InlineData("Internal/CompanyAdmin", true)]
    [InlineData("AreaManager", true)]
    [InlineData("Internal/AreaManager", true)]
    [InlineData("AgencyOperator", false)]
    [InlineData("SalesRep", false)]
    [InlineData("ShopOwner", false)]
    [InlineData("companyadmin", false)]
    public async Task Only_admins_and_area_managers_read_the_audit_log(string role, bool allowed)
    {
        Assert.Equal(allowed, await AllowedAsync(RolePolicies.RequireAuditReader, role));
    }

    [Theory]
    [InlineData("CompanyAdmin", true)]
    [InlineData("Internal/CompanyAdmin", true)]
    [InlineData("AreaManager", false)]
    [InlineData("SalesRep", false)]
    public async Task Only_company_admins_verify_the_chain(string role, bool allowed)
    {
        Assert.Equal(allowed, await AllowedAsync(RolePolicies.RequireCompanyAdmin, role));
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    public async Task Edit_verbs_on_an_audit_entry_are_not_allowed(string verb)
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company);
        using var request = new HttpRequestMessage(new HttpMethod(verb), $"/api/audit/{Guid.NewGuid()}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/audit")]
    [InlineData("/api/audit/verify")]
    [InlineData("/api/audit/9d7c1ca0-0000-0000-0000-000000000001")]
    public async Task An_anonymous_caller_is_unauthorised(string path)
    {
        using var client = _api.CreateClientAs(roles: null);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("SalesRep", "/api/audit")]
    [InlineData("AgencyOperator", "/api/audit/9d7c1ca0-0000-0000-0000-000000000001")]
    [InlineData("ShopOwner", "/api/audit/verify")]
    [InlineData("AreaManager", "/api/audit/verify")]
    public async Task A_caller_without_the_role_is_forbidden(string role, string path)
    {
        using var client = _api.CreateClientAs(role, Company);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_a_company_is_unauthorised()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", companyId: null);

        var response = await client.GetAsync("/api/audit");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task From_later_than_to_is_a_bad_request()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company);

        var response = await client.GetAsync("/api/audit?from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_filter_is_a_bad_request()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company);

        var response = await client.GetAsync("/api/audit?aggregateId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Organization_being_down_is_a_503_for_an_area_manager_never_an_unscoped_answer()
    {
        using var client = _api.CreateClientAs("AreaManager", Company, subject: $"am-{Guid.NewGuid():N}");

        var response = await client.GetAsync("/api/audit");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Organization", await response.Content.ReadAsStringAsync());
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
