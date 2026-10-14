using System.Security.Claims;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Api.Identity;

/// <summary>
/// WSO2 sends the raw "roles" claim (MapInboundClaims = false). Depending
/// on the client, a value can arrive as "CompanyAdmin" or with WSO2's
/// "Internal/" prefix; both mean the same role.
/// </summary>
public static class RoleClaims
{
    public const string ClaimType = "roles";
    private const string InternalPrefix = "Internal/";

    public static bool Has(ClaimsPrincipal? user, string role) =>
        user?.Claims.Any(claim =>
            claim.Type == ClaimType &&
            (string.Equals(claim.Value, role, StringComparison.Ordinal) ||
             string.Equals(claim.Value, InternalPrefix + role, StringComparison.Ordinal))) == true;

    /// <summary>The broadest Sellora role the caller holds, or null.</summary>
    public static string? Broadest(ClaimsPrincipal? user) =>
        SelloraRoles.ByBreadth.FirstOrDefault(role => Has(user, role));

    /// <summary>The tenant claim. Same name as the other services, with the snake_case fallback some clients use.</summary>
    public static Guid? CompanyId(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst("companyId")?.Value ?? user?.FindFirst("company_id")?.Value;
        return Guid.TryParse(value, out var companyId) && companyId != Guid.Empty ? companyId : null;
    }
}
