using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Domain.Exports;
using Sellora.AuditService.Domain.Tenancy;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Exports;

/// <summary>US-E7-3-BE2: claims queued exports with FOR UPDATE SKIP LOCKED and builds them one at a time.</summary>
public sealed class ExportWorkerService(
    IServiceScopeFactory scopeFactory,
    IOptions<ExportOptions> options,
    TimeProvider clock,
    ILogger<ExportWorkerService> logger) : BackgroundService
{
    public const string FailureMessage = "The export could not be generated. Try again or narrow the filters.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;

            try
            {
                worked = await RunNextAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Export worker could not claim a job; retrying");
            }

            if (!worked)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <returns>True when a job was claimed, so the next one is tried at once.</returns>
    internal async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        if (await ClaimAsync(cancellationToken) is not { } claimed)
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ISystemTenantContext>().BeginSystemTenantScope(claimed.CompanyId);
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<IExportGenerator>();

        var export = await db.ReportExports.SingleAsync(candidate => candidate.ExportId == claimed.ExportId, cancellationToken);

        try
        {
            var file = await generator.GenerateAsync(export, cancellationToken);
            export.Complete(file.FileName, file.ContentType, file.Content, clock.GetUtcNow(), options.Value.Expiry);

            logger.LogInformation("ExportReady: {ExportId} {ReportType} {Format}, {Bytes} bytes",
                export.ExportId, export.ReportType, export.Format, file.Content.Length);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The full exception goes to the log; the user sees a short message, never a stack trace.
            logger.LogError(exception, "ExportFailed: {ExportId} {ReportType}", export.ExportId, export.ReportType);
            export.Fail(FailureMessage, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    private async Task<ReportExport?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // A row another worker has locked is skipped, so two workers never claim the same job.
        var export = (await db.ReportExports
                .FromSql($"""
                    SELECT * FROM reporting.report_export
                    WHERE status = 'Queued'
                    ORDER BY created_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (export is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        export.Start(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return export;
    }
}
