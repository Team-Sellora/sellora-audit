namespace Sellora.AuditService.Domain.Audit;

public enum ChainBreakReason
{
    HashMismatch,
    PreviousHashMismatch,
    MissingPosition
}

/// <param name="AuditEntryId">Null for a missing position: there is no row to name.</param>
public sealed record ChainBreak(
    Guid? AuditEntryId,
    long ChainPosition,
    string? EventType,
    ChainBreakReason Reason);

/// <summary>
/// Walks one company's chain in ChainPosition order and stops at the first
/// broken link (US-E7-1-BE8). Feed it records in order, batch by batch.
/// </summary>
public sealed class ChainVerifier
{
    private long _expectedPosition = 1;
    private string _previousHash = AuditHasher.GenesisHash;

    public long EntriesChecked { get; private set; }

    public ChainBreak? FirstBreak { get; private set; }

    public bool IsBroken => FirstBreak is not null;

    /// <returns>False once the chain is broken; later records are ignored.</returns>
    public bool Check(ChainRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (IsBroken)
        {
            return false;
        }

        if (record.ChainPosition != _expectedPosition)
        {
            FirstBreak = new ChainBreak(null, _expectedPosition, null, ChainBreakReason.MissingPosition);
            return false;
        }

        EntriesChecked++;

        if (!string.Equals(record.PreviousHash, _previousHash, StringComparison.Ordinal))
        {
            FirstBreak = new ChainBreak(
                record.AuditEntryId, record.ChainPosition, record.EventType, ChainBreakReason.PreviousHashMismatch);
            return false;
        }

        if (!string.Equals(AuditHasher.Compute(record), record.Hash, StringComparison.Ordinal))
        {
            FirstBreak = new ChainBreak(
                record.AuditEntryId, record.ChainPosition, record.EventType, ChainBreakReason.HashMismatch);
            return false;
        }

        _previousHash = record.Hash;
        _expectedPosition++;
        return true;
    }
}
