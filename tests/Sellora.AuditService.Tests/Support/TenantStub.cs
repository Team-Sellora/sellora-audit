using Sellora.AuditService.Domain.Tenancy;

namespace Sellora.AuditService.Tests.Support;

/// <summary>A tenant context the tests control; background services set it per company.</summary>
public sealed class TenantStub(Guid? companyId = null) : ITenantContext, ISystemTenantContext
{
    private Guid? _system;

    public Guid? CompanyId => _system ?? companyId;

    public IDisposable BeginSystemTenantScope(Guid company)
    {
        var previous = _system;
        _system = company;
        return new Restore(() => _system = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
