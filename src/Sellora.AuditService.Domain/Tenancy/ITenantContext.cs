namespace Sellora.AuditService.Domain.Tenancy;

public interface ITenantContext
{
    Guid? CompanyId { get; }
}
