using Sellora.AuditService.Domain.Audit;

namespace Sellora.AuditService.Application.Ingestion;

public enum AppendStatus
{
    Recorded,
    Duplicate
}

public sealed record AppendOutcome(AppendStatus Status, long? ChainPosition);

/// <summary>
/// The only way into the audit log. There is deliberately no update or
/// delete member; AuditRouteImmutabilityTests pins this interface's shape.
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// Appends the event to its company's chain, or reports a duplicate when
    /// (company, event ID) is already recorded.
    /// </summary>
    Task<AppendOutcome> AppendAsync(AuditEventContent content, CancellationToken cancellationToken);
}

public enum ProcessingStatus
{
    Recorded,
    Duplicate,
    Rejected
}

public sealed record ProcessingOutcome(
    ProcessingStatus Status,
    AuditEventContent? Content,
    long? ChainPosition,
    RejectionReason? Rejection,
    string? Detail);

public interface IAuditMessageProcessor
{
    Task<ProcessingOutcome> ProcessAsync(IncomingMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Normalises a record and appends it. A rejection is returned, not thrown,
/// so the consumer can dead-letter it and move on; database failures are
/// thrown, so the consumer retries the same record.
/// </summary>
public sealed class AuditMessageProcessor(IAuditStore store) : IAuditMessageProcessor
{
    public async Task<ProcessingOutcome> ProcessAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        var result = AuditEnvelopeNormaliser.Normalise(message);

        if (result.Content is not { } content)
        {
            return new ProcessingOutcome(ProcessingStatus.Rejected, null, null, result.Rejection, result.Detail);
        }

        var appended = await store.AppendAsync(content, cancellationToken);

        return new ProcessingOutcome(
            appended.Status == AppendStatus.Recorded ? ProcessingStatus.Recorded : ProcessingStatus.Duplicate,
            content,
            appended.ChainPosition,
            null,
            null);
    }
}
