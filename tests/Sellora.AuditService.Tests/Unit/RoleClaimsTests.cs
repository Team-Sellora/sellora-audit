using System.Security.Claims;
using Sellora.AuditService.Api.Identity;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Tests.Unit;

/// <summary>Roles and the tenant come from WSO2's raw claims (MapInboundClaims = false).</summary>
public sealed class RoleClaimsTests
{
    [Theory]
    [InlineData("AreaManager")]
    [InlineData("Internal/AreaManager")]
    public void A_role_is_read_with_or_without_the_internal_prefix(string value)
    {
        Assert.True(RoleClaims.Has(User(("roles", value)), SelloraRoles.AreaManager));
    }

    [Theory]
    [InlineData("areamanager")]
    [InlineData("Application/AreaManager")]
    [InlineData("AreaManagers")]
    public void Anything_else_is_not_the_role(string value)
    {
        Assert.False(RoleClaims.Has(User(("roles", value)), SelloraRoles.AreaManager));
    }

    [Fact]
    public void The_mapped_role_claim_type_is_ignored()
    {
        Assert.False(RoleClaims.Has(User((ClaimTypes.Role, "CompanyAdmin")), SelloraRoles.CompanyAdmin));
    }

    [Fact]
    public void A_user_with_several_roles_gets_the_broadest()
    {
        var user = User(("roles", "SalesRep"), ("roles", "Internal/AreaManager"), ("roles", "CompanyAdmin"));

        Assert.Equal(SelloraRoles.CompanyAdmin, RoleClaims.Broadest(user));
    }

    [Fact]
    public void No_sellora_role_is_null()
    {
        Assert.Null(RoleClaims.Broadest(User(("roles", "Internal/everyone"))));
        Assert.Null(RoleClaims.Broadest(null));
    }

    [Theory]
    [InlineData("companyId")]
    [InlineData("company_id")]
    public void The_company_comes_from_the_tenant_claim(string claim)
    {
        var company = Guid.NewGuid();

        Assert.Equal(company, RoleClaims.CompanyId(User((claim, company.ToString()))));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void A_missing_or_empty_company_is_null(string value)
    {
        Assert.Null(RoleClaims.CompanyId(User(("companyId", value))));
        Assert.Null(RoleClaims.CompanyId(User()));
    }

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), "test"));
}
