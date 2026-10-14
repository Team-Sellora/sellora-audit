using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Domain.Exports;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Exports;

/// <summary>US-E7-3-BE1: queues exports and serves them only to the user who asked.</summary>
public sealed class ExportService(AuditDbContext db, TimeProvider clock) : IExportService
{
    public async Task<ExportSubmitResult> SubmitAsync(ExportRequester requester, CreateExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(request);

        var check = ExportRequests.Check(requester.Role, request);

        if (check.Status != ExportSubmitStatus.Accepted)
        {
            return new ExportSubmitResult(check.Status, null, check.Message);
        }

        var export = ReportExport.Queue(
            requester.CompanyId,
            requester.Subject,
            requester.Name,
            requester.Role,
            requester.ProvinceIds,
            check.ReportType,
            ExportRequests.Serialise(check.Filters),
            check.Format,
            clock.GetUtcNow());

        db.ReportExports.Add(export);
        await db.SaveChangesAsync(cancellationToken);

        return new ExportSubmitResult(ExportSubmitStatus.Accepted, export.ExportId, null);
    }

    public async Task<ExportStatusResponse?> GetStatusAsync(Guid companyId, string subject, Guid exportId, CancellationToken cancellationToken)
    {
        var export = await WithoutContent(Mine(companyId, subject).Where(candidate => candidate.ExportId == exportId))
            .FirstOrDefaultAsync(cancellationToken);

        return export is null ? null : export.ToResponse(clock.GetUtcNow());
    }

    public async Task<ExportFileResult> GetFileAsync(Guid companyId, string subject, Guid exportId, CancellationToken cancellationToken)
    {
        var export = await Mine(companyId, subject).FirstOrDefaultAsync(candidate => candidate.ExportId == exportId, cancellationToken);

        if (export is null)
        {
            return new ExportFileResult(ExportFileStatus.NotFound);
        }

        return export.DownloadAt(clock.GetUtcNow()) switch
        {
            ExportDownload.Ready => new ExportFileResult(ExportFileStatus.Ready, export.FileName, export.ContentType, export.Content),
            ExportDownload.Gone => new ExportFileResult(ExportFileStatus.Gone),
            _ => new ExportFileResult(ExportFileStatus.NotReady)
        };
    }

    public async Task<IReadOnlyList<ExportStatusResponse>> ListMineAsync(Guid companyId, string subject, CancellationToken cancellationToken)
    {
        var exports = await WithoutContent(Mine(companyId, subject)
                .OrderByDescending(export => export.CreatedAt)
                .ThenByDescending(export => export.ExportId)
                .Take(ExportRequests.RecentLimit))
            .ToListAsync(cancellationToken);

        var now = clock.GetUtcNow();
        return exports.Select(export => export.ToResponse(now)).ToList();
    }

    private IQueryable<ReportExport> Mine(Guid companyId, string subject) =>
        db.ReportExports.AsNoTracking().Where(export => export.CompanyId == companyId && export.RequesterSub == subject);

    // The file can be several megabytes; status and list never load it.
    private static IQueryable<ExportRow> WithoutContent(IQueryable<ReportExport> exports) =>
        exports.Select(export => new ExportRow(
            export.ExportId,
            export.ReportType,
            export.Format,
            export.Status,
            export.CreatedAt,
            export.StartedAt,
            export.CompletedAt,
            export.ExpiresAt,
            export.FileName,
            export.Error,
            export.Content != null));

    private sealed record ExportRow(
        Guid ExportId,
        string ReportType,
        string Format,
        ExportStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        DateTimeOffset? ExpiresAt,
        string? FileName,
        string? Error,
        bool HasContent)
    {
        public ExportStatusResponse ToResponse(DateTimeOffset now)
        {
            // Past its expiry it reads as Expired even before the cleanup runs.
            var expired = Status == ExportStatus.Expired ||
                          (Status == ExportStatus.Ready && (!HasContent || (ExpiresAt is { } expires && now >= expires)));

            return new ExportStatusResponse(
                ExportId,
                ReportType,
                Format,
                (expired ? ExportStatus.Expired : Status).ToString(),
                CreatedAt,
                StartedAt,
                CompletedAt,
                ExpiresAt,
                FileName,
                Status == ExportStatus.Ready && !expired ? ExportStatusResponse.FilePath(ExportId) : null,
                Error);
        }
    }
}
