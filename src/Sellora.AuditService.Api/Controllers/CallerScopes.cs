using Microsoft.AspNetCore.Mvc;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Api.Controllers;

/// <summary>What the caller may read: the whole company, or an Area Manager's provinces.</summary>
public sealed record CallerAccess(Guid CompanyId, string Subject, string Role, IReadOnlyCollection<Guid>? ProvinceIds);

public static class CallerScopes
{
    /// <summary>Null when the token carries no tenant or subject; the caller gets a 401 problem.</summary>
    public static CallerAccess? Resolve(ICurrentUserContext caller)
    {
        if (caller.CompanyId is not { } companyId || string.IsNullOrWhiteSpace(caller.Subject) || caller.Role is not { } role)
        {
            return null;
        }

        // Company Admins see the whole company; an Area Manager only their provinces.
        IReadOnlyCollection<Guid>? provinces = role == SelloraRoles.CompanyAdmin ? null : caller.ProvinceIds.ToList();

        return new CallerAccess(companyId, caller.Subject, role, provinces);
    }

    public static ObjectResult Problem(int status, string title, string? detail) =>
        new(new ProblemDetails { Status = status, Title = title, Detail = detail }) { StatusCode = status };

    public static ObjectResult TenantMissing() =>
        Problem(StatusCodes.Status401Unauthorized, "Tenant not available", "The access token has no company or subject.");
}
