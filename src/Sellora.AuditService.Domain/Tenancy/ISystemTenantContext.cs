namespace Sellora.AuditService.Domain.Tenancy;

/// <summary>
/// Explicit tenant scope for trusted background work: the Kafka consumer
/// handles one company per message.
/// </summary>
public interface ISystemTenantContext
{
    IDisposable BeginSystemTenantScope(Guid companyId);
}
