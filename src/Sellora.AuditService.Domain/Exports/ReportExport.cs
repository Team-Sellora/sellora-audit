using System.Diagnostics.CodeAnalysis;
using Sellora.AuditService.Domain.Tenancy;

namespace Sellora.AuditService.Domain.Exports;

public enum ExportStatus
{
    Queued,
    Running,
    Ready,
    Failed,
    Expired
}

public enum ExportDownload
{
    Ready,
    NotReady,
    Gone
}

public static class ReportTypes
{
    public const string SalesSummary = "sales-summary";
    public const string SalesBreakdown = "sales-breakdown";
    public const string AuditExtract = "audit-extract";

    public static readonly IReadOnlyList<string> All = new[] { SalesSummary, SalesBreakdown, AuditExtract };
}

public static class ExportFormats
{
    public const string Csv = "csv";
    public const string Pdf = "pdf";

    public static readonly IReadOnlyList<string> All = new[] { Csv, Pdf };
}

/// <summary>US-E7-3-BE1: an export job; requester and scope are copied from the token at submission.</summary>
public sealed class ReportExport : ITenantScoped
{
    public const int MaxErrorLength = 200;

    private ReportExport()
    {
        RequesterSub = string.Empty;
        RequesterName = string.Empty;
        RequesterRole = string.Empty;
        ReportType = string.Empty;
        FiltersJson = "{}";
        Format = ExportFormats.Csv;
    }

    public Guid ExportId { get; private set; }

    public Guid CompanyId { get; private set; }

    public string RequesterSub { get; private set; }

    public string RequesterName { get; private set; }

    public string RequesterRole { get; private set; }

    /// <summary>Null means the whole company (Company Admin).</summary>
    public Guid[]? ScopeProvinceIds { get; private set; }

    public string ReportType { get; private set; }

    public string FiltersJson { get; private set; }

    public string Format { get; private set; }

    public ExportStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public string? FileName { get; private set; }

    public string? ContentType { get; private set; }

    public byte[]? Content { get; private set; }

    public string? Error { get; private set; }

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
        Justification = "Factory for the persisted export request; every parameter is a required column of the row.")]
    public static ReportExport Queue(
        Guid companyId,
        string requesterSub,
        string requesterName,
        string requesterRole,
        IReadOnlyCollection<Guid>? scopeProvinceIds,
        string reportType,
        string filtersJson,
        string format,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(companyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterSub);
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterRole);

        if (!ReportTypes.All.Contains(reportType))
        {
            throw new ArgumentException($"Unknown report type '{reportType}'.", nameof(reportType));
        }

        if (!ExportFormats.All.Contains(format))
        {
            throw new ArgumentException($"Unknown export format '{format}'.", nameof(format));
        }

        return new ReportExport
        {
            ExportId = Guid.NewGuid(),
            CompanyId = companyId,
            RequesterSub = requesterSub,
            RequesterName = string.IsNullOrWhiteSpace(requesterName) ? requesterSub : requesterName,
            RequesterRole = requesterRole,
            ScopeProvinceIds = scopeProvinceIds?.Distinct().ToArray(),
            ReportType = reportType,
            FiltersJson = filtersJson,
            Format = format,
            Status = ExportStatus.Queued,
            CreatedAt = now
        };
    }

    public void Start(DateTimeOffset now)
    {
        Require(ExportStatus.Queued);
        Status = ExportStatus.Running;
        StartedAt = now;
    }

    public void Complete(string fileName, string contentType, byte[] content, DateTimeOffset now, TimeSpan expiry)
    {
        Require(ExportStatus.Running);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiry, TimeSpan.Zero);

        Status = ExportStatus.Ready;
        FileName = fileName;
        ContentType = contentType;
        Content = content;
        CompletedAt = now;
        ExpiresAt = now + expiry;
        Error = null;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Require(ExportStatus.Running);

        var text = string.IsNullOrWhiteSpace(error) ? "The export failed." : error.Trim();
        Status = ExportStatus.Failed;
        Error = text.Length > MaxErrorLength ? text[..MaxErrorLength] : text;
        CompletedAt = now;
    }

    /// <summary>US-E7-3-BE3: the file is dropped; the row stays so "My exports" can show it.</summary>
    public void Expire()
    {
        Require(ExportStatus.Ready);
        Status = ExportStatus.Expired;
        Content = null;
    }

    public ExportDownload DownloadAt(DateTimeOffset now)
    {
        if (Status == ExportStatus.Expired || (ExpiresAt is { } expires && now >= expires))
        {
            return ExportDownload.Gone;
        }

        if (Status != ExportStatus.Ready)
        {
            return ExportDownload.NotReady;
        }

        return Content is null ? ExportDownload.Gone : ExportDownload.Ready;
    }

    private void Require(ExportStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Export {ExportId} is {Status}, not {expected}.");
        }
    }
}
