using Microsoft.AspNetCore.Authorization;
using Sellora.AuditService.Api.Identity;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Api.Authorization;

/// <summary>
/// Same pattern as the other services' RolePolicies. The audit log and the
/// reports are for Company Admins and Area Managers only (US-E7-1-BE7,
/// US-E7-2-BE5); chain verification and projection rebuilds are Company
/// Admin only.
/// </summary>
public static class RolePolicies
{
    public const string RequireCompanyAdmin = "RequireCompanyAdmin";
    public const string RequireAuditReader = "RequireAuditReader";
    public const string RequireReportReader = "RequireReportReader";

    public static void AddSelloraRolePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(RequireCompanyAdmin, policy => policy.RequireAssertion(HasAnyRole(SelloraRoles.CompanyAdmin)));
        options.AddPolicy(RequireAuditReader, policy => policy.RequireAssertion(HasAnyRole(SelloraRoles.CompanyAdmin, SelloraRoles.AreaManager)));
        options.AddPolicy(RequireReportReader, policy => policy.RequireAssertion(HasAnyRole(SelloraRoles.CompanyAdmin, SelloraRoles.AreaManager)));
    }

    private static Func<AuthorizationHandlerContext, bool> HasAnyRole(params string[] roles) =>
        context => roles.Any(role => RoleClaims.Has(context.User, role));
}
