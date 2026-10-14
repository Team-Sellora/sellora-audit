using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Audit;

/// <summary>US-E7-1-BE7: search and detail, read only, scoped by company and province.</summary>
public sealed class AuditReadService(AuditDbContext db) : IAuditReadService
{
    public async Task<PagedResponse<AuditEntrySummary>> SearchAsync(
        AuditReadScope scope, AuditSearchQuery query, CancellationToken cancellationToken)
    {
        var filtered = Filter(Scoped(scope), query);
        var total = await filtered.CountAsync(cancellationToken);

        var items = await Order(filtered, query)
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .Select(entry => new AuditEntrySummary(
                entry.AuditEntryId,
                entry.ChainPosition,
                entry.EventId,
                entry.EventType,
                entry.AggregateType,
                entry.AggregateId,
                entry.OrderReference,
                entry.ActorUserId,
                entry.ActorRole,
                entry.OccurredAt,
                entry.RecordedAt,
                entry.CorrelationId,
                entry.SourceTopic))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AuditEntrySummary>(items, query.SafePage, query.SafePageSize, total);
    }

    public async Task<AuditEntryDetail?> GetAsync(AuditReadScope scope, Guid auditEntryId, CancellationToken cancellationToken)
    {
        var entry = await Scoped(scope).FirstOrDefaultAsync(candidate => candidate.AuditEntryId == auditEntryId, cancellationToken);
        return entry is null ? null : ToDetail(entry);
    }

    private IQueryable<AuditEntry> Scoped(AuditReadScope scope)
    {
        var entries = db.AuditEntries.AsNoTracking().Where(entry => entry.CompanyId == scope.CompanyId);

        if (scope.IsWholeCompany)
        {
            return entries;
        }

        // Entries without a province are hidden from Area Managers.
        var provinces = scope.ProvinceIds!.ToList();
        return entries.Where(entry => entry.ProvinceId != null && provinces.Contains(entry.ProvinceId.Value));
    }

    private static IQueryable<AuditEntry> Filter(IQueryable<AuditEntry> entries, AuditSearchQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.AggregateType))
        {
            entries = entries.Where(entry => entry.AggregateType == query.AggregateType);
        }

        if (query.AggregateId is { } aggregateId)
        {
            entries = entries.Where(entry => entry.AggregateId == aggregateId);
        }

        if (!string.IsNullOrWhiteSpace(query.OrderReference))
        {
            var reference = query.OrderReference.Trim();
            entries = entries.Where(entry => entry.OrderReference == reference);
        }

        if (!string.IsNullOrWhiteSpace(query.ActorUserId))
        {
            var actor = query.ActorUserId.Trim();
            entries = entries.Where(entry => entry.ActorUserId == actor);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            entries = entries.Where(entry => entry.EventType == query.EventType);
        }

        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            entries = entries.Where(entry => entry.OccurredAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            entries = entries.Where(entry => entry.OccurredAt <= toUtc);
        }

        return entries;
    }

    private static IQueryable<AuditEntry> Order(IQueryable<AuditEntry> entries, AuditSearchQuery query) =>
        query.IsTimeline
            ? entries.OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.ChainPosition)
            : entries.OrderByDescending(entry => entry.OccurredAt).ThenByDescending(entry => entry.ChainPosition);

    private static AuditEntryDetail ToDetail(AuditEntry entry) => new(
        entry.AuditEntryId,
        entry.ChainPosition,
        entry.EventId,
        entry.EventType,
        entry.AggregateType,
        entry.AggregateId,
        entry.OrderReference,
        entry.ActorUserId,
        entry.ActorRole,
        entry.OccurredAt,
        entry.RecordedAt,
        entry.CorrelationId,
        entry.SourceTopic,
        entry.SchemaVersion,
        entry.ProvinceId,
        entry.Partition,
        entry.Offset,
        entry.PreviousHash,
        entry.Hash,
        entry.Payload);
}
