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

/// <summary>Calls sellora-organization with the caller's forwarded bearer token.</summary>
public interface IOrganizationClient
{
    /// <summary>GET /api/me/scope. Null when the caller has no profile (404).</summary>
    Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken);
}
