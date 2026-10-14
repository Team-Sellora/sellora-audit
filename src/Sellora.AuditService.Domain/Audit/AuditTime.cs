namespace Sellora.AuditService.Domain.Audit;

public static class AuditTime
{
    /// <summary>
    /// UTC, truncated to whole microseconds. PostgreSQL timestamptz keeps
    /// microseconds while .NET keeps 100 ns ticks, so a value is truncated
    /// before it is hashed or stored; otherwise recomputing the hash from the
    /// stored row would not match.
    /// </summary>
    public static DateTimeOffset Normalise(DateTimeOffset value)
    {
        var ticks = value.UtcTicks;
        return new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero);
    }
}
