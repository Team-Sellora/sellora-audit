using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sellora.AuditService.Domain.Tenancy;

namespace Sellora.AuditService.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef` build the context without starting the API. Migrations
/// must run as audit_owner, so the design-time connection reads
/// ConnectionStrings__Owner first.
/// </summary>
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Owner")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5438;Database=audit_db;Username=audit_owner;Password=audit_owner_dev_pw";

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString, AuditDatabase.ConfigureNpgsql)
            .Options;

        return new AuditDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? CompanyId => null;
    }
}
