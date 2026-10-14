using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Audit;

/// <summary>
/// US-E7-1-BE8: recomputes one company's chain in ChainPosition order, in
/// batches of 1,000 read by keyset, and stops at the first broken link.
/// Read only, so it takes no lock and never blocks new events.
/// </summary>
public sealed class ChainVerificationService(AuditDbContext db) : IChainVerificationService
{
    public const int BatchSize = 1_000;

    public async Task<VerifyChainResponse> VerifyAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var verifier = new ChainVerifier();
        var lastPosition = 0L;

        while (true)
        {
            var batch = await db.AuditEntries
                .AsNoTracking()
                .Where(entry => entry.CompanyId == companyId && entry.ChainPosition > lastPosition)
                .OrderBy(entry => entry.ChainPosition)
                .Take(BatchSize)
                .Select(entry => new ChainRecord(
                    entry.AuditEntryId,
                    entry.ChainPosition,
                    entry.CompanyId,
                    entry.EventId,
                    entry.EventType,
                    entry.AggregateType,
                    entry.AggregateId,
                    entry.OccurredAt,
                    entry.CorrelationId,
                    entry.Payload,
                    entry.PreviousHash,
                    entry.Hash))
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                return VerifyChainResponse.From(verifier);
            }

            foreach (var record in batch)
            {
                if (!verifier.Check(record))
                {
                    return VerifyChainResponse.From(verifier);
                }
            }

            lastPosition = batch[^1].ChainPosition;
        }
    }
}
