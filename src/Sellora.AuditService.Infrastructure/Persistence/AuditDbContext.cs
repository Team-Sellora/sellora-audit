using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Domain.Exports;
using Sellora.AuditService.Domain.Reporting;
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

    public DbSet<OrderFact> OrderFacts => Set<OrderFact>();

    public DbSet<OrderFactLine> OrderFactLines => Set<OrderFactLine>();

    public DbSet<RepDimension> RepDimensions => Set<RepDimension>();

    public DbSet<TerritoryDimension> TerritoryDimensions => Set<TerritoryDimension>();

    public DbSet<AgencyDimension> AgencyDimensions => Set<AgencyDimension>();

    public DbSet<ReportExport> ReportExports => Set<ReportExport>();

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
        modelBuilder.Entity<OrderFact>().HasQueryFilter(fact =>
            _tenantContext.CompanyId != null && fact.CompanyId == _tenantContext.CompanyId);
        modelBuilder.Entity<OrderFactLine>().HasQueryFilter(line =>
            _tenantContext.CompanyId != null && line.CompanyId == _tenantContext.CompanyId);
        modelBuilder.Entity<RepDimension>().HasQueryFilter(rep =>
            _tenantContext.CompanyId != null && rep.CompanyId == _tenantContext.CompanyId);
        modelBuilder.Entity<TerritoryDimension>().HasQueryFilter(territory =>
            _tenantContext.CompanyId != null && territory.CompanyId == _tenantContext.CompanyId);
        modelBuilder.Entity<AgencyDimension>().HasQueryFilter(agency =>
            _tenantContext.CompanyId != null && agency.CompanyId == _tenantContext.CompanyId);
        modelBuilder.Entity<ReportExport>().HasQueryFilter(export =>
            _tenantContext.CompanyId != null && export.CompanyId == _tenantContext.CompanyId);
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
