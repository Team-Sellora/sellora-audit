using Sellora.AuditService.Domain.Audit;

namespace Sellora.AuditService.Application.Audit;

public sealed record ChainBreakResponse(Guid? AuditEntryId, long ChainPosition, string? EventType, string Reason);

public sealed record VerifyChainResponse(bool Verified, long EntriesChecked, ChainBreakResponse? FirstBroken)
{
    public static VerifyChainResponse From(ChainVerifier verifier) => new(
        !verifier.IsBroken,
        verifier.EntriesChecked,
        verifier.FirstBreak is { } broken
            ? new ChainBreakResponse(broken.AuditEntryId, broken.ChainPosition, broken.EventType, broken.Reason.ToString())
            : null);
}

public interface IChainVerificationService
{
    Task<VerifyChainResponse> VerifyAsync(Guid companyId, CancellationToken cancellationToken);
}
