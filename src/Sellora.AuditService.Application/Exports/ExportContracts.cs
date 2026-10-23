using Sellora.AuditService.Domain.Exports;

namespace Sellora.AuditService.Application.Exports;

/// <summary>The on-screen filters; which ones apply depends on the report type.</summary>
public sealed record ExportFilters
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public DateOnly? CompareFrom { get; init; }

    public DateOnly? CompareTo { get; init; }

    public string? GroupBy { get; init; }

    public string? Sort { get; init; }

    public string? Direction { get; init; }

    public string? AggregateType { get; init; }

    public Guid? AggregateId { get; init; }

    public string? OrderReference { get; init; }

    public string? ActorUserId { get; init; }

    public string? EventType { get; init; }

    public DateTimeOffset? OccurredFrom { get; init; }

    public DateTimeOffset? OccurredTo { get; init; }
}

public sealed record CreateExportRequest(string? ReportType, string? Format, ExportFilters? Filters);

/// <summary>Who asked, taken from the caller's token at submission.</summary>
public sealed record ExportRequester(Guid CompanyId, string Subject, string Name, string Role, IReadOnlyCollection<Guid>? ProvinceIds);

public sealed record ExportAccepted(Guid ExportId);

public sealed record ExportStatusResponse(
    Guid ExportId,
    string ReportType,
    string Format,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ExpiresAt,
    string? FileName,
    string? DownloadPath,
    string? Error)
{
    public static string FilePath(Guid exportId) => $"/api/reports/export/{exportId}/file";
}

public enum ExportSubmitStatus
{
    Accepted,
    Invalid,
    Forbidden
}

public sealed record ExportSubmitResult(ExportSubmitStatus Status, Guid? ExportId, string? Message);

public enum ExportFileStatus
{
    Ready,
    NotFound,
    NotReady,
    Gone
}

public sealed record ExportFileResult(ExportFileStatus Status, string? FileName = null, string? ContentType = null, byte[]? Content = null);

public interface IExportService
{
    Task<ExportSubmitResult> SubmitAsync(ExportRequester requester, CreateExportRequest request, CancellationToken cancellationToken);

    /// <summary>Null unless the export exists and belongs to the subject.</summary>
    Task<ExportStatusResponse?> GetStatusAsync(Guid companyId, string subject, Guid exportId, CancellationToken cancellationToken);

    Task<ExportFileResult> GetFileAsync(Guid companyId, string subject, Guid exportId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportStatusResponse>> ListMineAsync(Guid companyId, string subject, CancellationToken cancellationToken);
}

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>Builds one claimed export's file; used by the background worker.</summary>
public interface IExportGenerator
{
    Task<ExportFile> GenerateAsync(ReportExport export, CancellationToken cancellationToken);
}
