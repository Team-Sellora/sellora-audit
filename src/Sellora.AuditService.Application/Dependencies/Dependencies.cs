using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Application.Dependencies;

public enum Dependency
{
    Organization
}

/// <summary>A dependency timed out, is failing, or its circuit breaker is open. Mapped to 503.</summary>
public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException(Dependency dependency, Exception? inner = null)
        : base($"{dependency} service is currently unavailable, please retry shortly.", inner)
    {
        Dependency = dependency;
    }

    public Dependency Dependency { get; }
}

/// <summary>Names for IDs that events do not name (territories).</summary>
public sealed record HierarchyNames(IReadOnlyDictionary<Guid, string> Territories)
{
    public static HierarchyNames Empty { get; } = new(new Dictionary<Guid, string>());
}

/// <summary>Calls sellora-organization with the caller's forwarded bearer token.</summary>
public interface IOrganizationClient
{
    /// <summary>GET /api/me/scope. Null when the caller has no profile (404).</summary>
    Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken);

    /// <summary>GET /api/hierarchy, reduced to names. Organization scopes it to the caller.</summary>
    Task<HierarchyNames> GetHierarchyNamesAsync(CancellationToken cancellationToken);
}
