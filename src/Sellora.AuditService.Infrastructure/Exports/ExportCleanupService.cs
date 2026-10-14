using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.AuditService.Domain.Exports;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Exports;

/// <summary>US-E7-3-BE3: drops the file of every export past its expiry; the row stays as Expired.</summary>
public sealed class ExportCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<ExportOptions> options,
    TimeProvider clock,
    ILogger<ExportCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await ExpireAsync(stoppingToken);

                if (expired > 0)
                {
                    logger.LogInformation("ExportsExpired: {Count} export files removed", expired);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Export cleanup failed; it runs again at the next interval");
            }

            try
            {
                await Task.Delay(options.Value.CleanupInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var now = clock.GetUtcNow();

        // Every company at once, so the tenant filter is lifted on purpose.
        return await db.ReportExports
            .IgnoreQueryFilters()
            .Where(export => export.Status == ExportStatus.Ready && export.ExpiresAt <= now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(export => export.Status, ExportStatus.Expired)
                .SetProperty(export => export.Content, (byte[]?)null), cancellationToken);
    }
}
