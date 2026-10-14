using Sellora.AuditService.Api.Identity;
using Sellora.AuditService.Domain.Tenancy;

namespace Sellora.AuditService.Api.Tenancy;

/// <summary>
/// Copied from sellora-order. The tenant is the token's company, or the
/// company of the event the Kafka consumer is handling.
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext, ISystemTenantContext
{
    private Guid? _systemCompanyId;

    public Guid? CompanyId => _systemCompanyId ?? RoleClaims.CompanyId(accessor.HttpContext?.User);

    public IDisposable BeginSystemTenantScope(Guid companyId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(companyId, Guid.Empty);

        var previousCompanyId = _systemCompanyId;
        _systemCompanyId = companyId;

        return new TenantScope(this, previousCompanyId);
    }

    private sealed class TenantScope(HttpTenantContext context, Guid? previousCompanyId) : IDisposable
    {
        public void Dispose() => context._systemCompanyId = previousCompanyId;
    }
}
