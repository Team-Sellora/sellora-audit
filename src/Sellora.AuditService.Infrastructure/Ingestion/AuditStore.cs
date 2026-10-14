using System.Data;
using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Domain.Tenancy;
using Sellora.AuditService.Infrastructure.Persistence;
using Sellora.AuditService.Infrastructure.Persistence.Configurations;

namespace Sellora.AuditService.Infrastructure.Ingestion;

/// <summary>
/// US-E7-1-BE5: appends to a company's hash chain. In one transaction, in
/// this order: take the company's advisory lock, check (company, event ID)
/// for a duplicate, read the last hash and position, insert the new row,
/// commit.
/// </summary>
public sealed class AuditStore(AuditDbContext db, ISystemTenantContext tenants, TimeProvider clock) : IAuditStore
{
    public async Task<AppendOutcome> AppendAsync(AuditEventContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var tenant = tenants.BeginSystemTenantScope(content.CompanyId);

        try
        {
            return await AppendOnceAsync(content, cancellationToken);
        }
        catch (Exception exception) when (AuditDatabase.IsUniqueViolation(exception, AuditEntryConfiguration.CompanyEventUniqueIndex))
        {
            // Only reachable if something appended without the advisory lock.
            db.ChangeTracker.Clear();
            return new AppendOutcome(AppendStatus.Duplicate, null);
        }
    }

    private async Task<AppendOutcome> AppendOnceAsync(AuditEventContent content, CancellationToken cancellationToken)
    {
        // READ COMMITTED on purpose: each statement after the lock sees the
        // previous lock holder's committed row. REPEATABLE READ would read a
        // snapshot taken before the lock and reuse an old previous hash.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Five topics are consumed at once and a staging slot swap briefly
        // runs two instances, so two events of one company can arrive
        // together. The lock needs no UPDATE grant, unlike SELECT FOR UPDATE.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({content.CompanyId.ToString("D")}))",
            cancellationToken);

        var duplicate = await db.AuditEntries
            .AsNoTracking()
            .AnyAsync(entry => entry.CompanyId == content.CompanyId && entry.EventId == content.EventId, cancellationToken);

        if (duplicate)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AppendOutcome(AppendStatus.Duplicate, null);
        }

        var last = await db.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.CompanyId == content.CompanyId)
            .OrderByDescending(entry => entry.ChainPosition)
            .Select(entry => new { entry.ChainPosition, entry.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        var appended = AuditEntry.Append(
            content,
            (last?.ChainPosition ?? 0) + 1,
            last?.Hash ?? AuditHasher.GenesisHash,
            clock.GetUtcNow());

        db.AuditEntries.Add(appended);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        return new AppendOutcome(AppendStatus.Recorded, appended.ChainPosition);
    }
}
