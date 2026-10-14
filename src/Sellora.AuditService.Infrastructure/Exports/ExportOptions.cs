namespace Sellora.AuditService.Infrastructure.Exports;

public sealed class ExportOptions
{
    public const string SectionName = "Exports";

    public int PollSeconds { get; set; } = 5;

    public int ExpiryHours { get; set; } = 24;

    public int CleanupMinutes { get; set; } = 60;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, PollSeconds));

    public TimeSpan Expiry => TimeSpan.FromHours(Math.Max(1, ExpiryHours));

    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(Math.Max(1, CleanupMinutes));
}
