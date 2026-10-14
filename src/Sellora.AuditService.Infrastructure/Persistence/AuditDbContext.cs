using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Domain.Tenancy;

namespace Sellora.AuditService.Infrastructure.Persistence;

public class AuditDbContext : DbContext
{
    public const string MigrationsHistorySchema = "migrations";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    private readonly ITenantContext _tenantContext;

    public AuditDbContext(DbContextOptions<AuditDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>Internal: the audit log is only written through IAuditStore.AppendAsync.</summary>
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    /// <summary>
    /// US-E7-1-BE6, the service layer of the three immutability layers: an
    /// audit entry can be added, never modified or deleted, whatever the
    /// calling code does.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectAuditChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectAuditChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuditDbContext).Assembly);

        // Company boundary: no tenant means no rows.
        modelBuilder.Entity<AuditEntry>().HasQueryFilter(entry =>
            _tenantContext.CompanyId != null && entry.CompanyId == _tenantContext.CompanyId);
    }

    private void RejectAuditChanges()
    {
        var altered = ChangeTracker.Entries<AuditEntry>()
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            .Select(entry => $"{entry.Entity.AuditEntryId} ({entry.State})")
            .ToList();

        if (altered.Count > 0)
        {
            throw new InvalidOperationException(
                $"Audit entries are append-only and cannot be modified or deleted: {string.Join(", ", altered)}.");
        }
    }
}
