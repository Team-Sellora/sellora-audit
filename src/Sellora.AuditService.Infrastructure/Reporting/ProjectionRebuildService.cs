using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Domain.Tenancy;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Reporting;

/// <summary>
/// US-E7-2-BE6: deletes a company's projections and replays its audit log in
/// chain order. Holds the append lock, so no event slips in between.
/// </summary>
public sealed class ProjectionRebuildService(
    AuditDbContext db,
    ISystemTenantContext tenants,
    IReportingProjector projector,
    ReportCache cache,
    TimeProvider clock,
    ILogger<ProjectionRebuildService> logger) : IProjectionRebuildService
{
    private const int BatchSize = 500;

    public async Task<ProjectionRebuildResponse> RebuildAsync(Guid companyId, CancellationToken cancellationToken)
    {
        using var tenant = tenants.BeginSystemTenantScope(companyId);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({companyId.ToString("D")}))", cancellationToken);

        await db.OrderFactLines.Where(line => line.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.OrderFacts.Where(fact => fact.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.RepDimensions.Where(rep => rep.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.TerritoryDimensions.Where(territory => territory.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.AgencyDimensions.Where(agency => agency.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);

        var replayed = 0L;
        var lastPosition = 0L;

        try
        {
            while (true)
            {
                var batch = await db.AuditEntries
                    .AsNoTracking()
                    .Where(entry => entry.CompanyId == companyId && entry.ChainPosition > lastPosition)
                    .OrderBy(entry => entry.ChainPosition)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);

                if (batch.Count == 0)
                {
                    break;
                }

                await ReplayBatchAsync(transaction, batch, cancellationToken);

                replayed += batch.Count;
                lastPosition = batch[^1].ChainPosition;
            }

            var orders = await db.OrderFacts.CountAsync(fact => fact.CompanyId == companyId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            cache.Invalidate(companyId);

            return new ProjectionRebuildResponse(replayed, orders, clock.GetUtcNow());
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task ReplayBatchAsync(IDbContextTransaction transaction, IReadOnlyList<AuditEntry> batch, CancellationToken cancellationToken)
    {
        await transaction.CreateSavepointAsync("batch", cancellationToken);

        try
        {
            foreach (var entry in batch)
            {
                await projector.ProjectAsync(ToContent(entry), cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync("batch", cancellationToken);
            db.ChangeTracker.Clear();
            return;
        }
        catch (Exception exception) when (ReportingProjector.IsDataError(exception))
        {
            await transaction.RollbackToSavepointAsync("batch", cancellationToken);
            db.ChangeTracker.Clear();
        }

        // One entry at a time, skipping any the append also had to skip.
        foreach (var entry in batch)
        {
            await transaction.CreateSavepointAsync("entry", cancellationToken);

            try
            {
                await projector.ProjectAsync(ToContent(entry), cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.ReleaseSavepointAsync("entry", cancellationToken);
            }
            catch (Exception exception) when (ReportingProjector.IsDataError(exception))
            {
                await transaction.RollbackToSavepointAsync("entry", cancellationToken);
                logger.LogError(exception,
                    "ReportingProjectionSkipped: {EventType} {EventId} for company {CompanyId} could not be projected during a rebuild",
                    entry.EventType, entry.EventId, entry.CompanyId);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static AuditEventContent ToContent(AuditEntry entry) => new(
        entry.CompanyId,
        entry.EventId,
        entry.EventType,
        entry.SchemaVersion,
        entry.SourceTopic,
        entry.Partition,
        entry.Offset,
        entry.AggregateType,
        entry.AggregateId,
        entry.OrderReference,
        entry.ProvinceId,
        entry.ActorUserId,
        entry.ActorRole,
        entry.OccurredAt,
        entry.CorrelationId,
        entry.Payload);
}
