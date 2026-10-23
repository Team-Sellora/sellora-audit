namespace Sellora.AuditService.Application.Identity;

public static class SelloraRoles
{
    public const string CompanyAdmin = "CompanyAdmin";
    public const string AreaManager = "AreaManager";
    public const string AgencyOperator = "AgencyOperator";
    public const string SalesRep = "SalesRep";
    public const string ShopOwner = "ShopOwner";

    /// <summary>Broadest scope first; a multi-role user gets the widest view they hold.</summary>
    public static readonly string[] ByBreadth =
    {
        CompanyAdmin, AreaManager, AgencyOperator, SalesRep, ShopOwner
    };
}

/// <summary>The caller's place in the hierarchy, from Organization's GET /api/me/scope.</summary>
public sealed record CallerScope(
    Guid? SalesRepId,
    Guid? AgencyId,
    Guid? ShopId,
    IReadOnlyList<Guid> ProvinceIds)
{
    /// <summary>No profile in Organization: the caller sees nothing.</summary>
    public static CallerScope Empty { get; } = new(null, null, null, Array.Empty<Guid>());
}

public interface ICurrentUserContext
{
    string? Subject { get; }

    /// <summary>The display name from the token, for export headers.</summary>
    string? Name { get; }

    /// <summary>The broadest Sellora role the caller holds, or null.</summary>
    string? Role { get; }

    Guid? CompanyId { get; }

    IReadOnlyCollection<Guid> ProvinceIds { get; }
}

/// <summary>The caller's raw bearer token, for forwarding to Organization.</summary>
public interface IAccessTokenAccessor
{
    string? GetBearerToken();
}

public interface ICorrelationIdAccessor
{
    string GetCorrelationId();
}
