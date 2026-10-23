namespace Sellora.AuditService.Domain.Tenancy;

public interface ITenantScoped
{
    Guid CompanyId { get; }
}
