using Sellora.AuditService.Application.Dependencies;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Tests.Support;

public sealed class OrganizationStub : IOrganizationClient
{
    public CallerScope? Scope { get; set; } = CallerScope.Empty;

    public bool Unavailable { get; set; }

    public HierarchyNames Names { get; set; } = HierarchyNames.Empty;

    public int Calls { get; private set; }

    public int NameCalls { get; private set; }

    public Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Unavailable ? throw new DependencyUnavailableException(Dependency.Organization) : Task.FromResult(Scope);
    }

    public Task<HierarchyNames> GetHierarchyNamesAsync(CancellationToken cancellationToken)
    {
        NameCalls++;
        return Unavailable ? throw new DependencyUnavailableException(Dependency.Organization) : Task.FromResult(Names);
    }
}

public sealed class TokenStub : IAccessTokenAccessor
{
    public string? Token { get; set; } = "caller-token";

    public string? GetBearerToken() => Token;
}
