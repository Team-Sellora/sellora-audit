using Sellora.AuditService.Application.Dependencies;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Tests.Support;

public sealed class OrganizationStub : IOrganizationClient
{
    public CallerScope? Scope { get; set; } = CallerScope.Empty;

    public bool Unavailable { get; set; }

    public int Calls { get; private set; }

    public Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Unavailable ? throw new DependencyUnavailableException(Dependency.Organization) : Task.FromResult(Scope);
    }
}
