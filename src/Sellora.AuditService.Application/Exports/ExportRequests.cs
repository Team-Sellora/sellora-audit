using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Domain.Exports;

namespace Sellora.AuditService.Application.Exports;

public sealed record ExportCheck(ExportSubmitStatus Status, string? Message, string ReportType, string Format, ExportFilters Filters);

/// <summary>US-E7-3-BE1: role rules and the same filter validation as the on-screen endpoints.</summary>
public static class ExportRequests
{
    public const int RecentLimit = 20;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsAllowed(string role, string reportType) => reportType switch
    {
        ReportTypes.AuditExtract => role == SelloraRoles.CompanyAdmin,
        ReportTypes.SalesSummary or ReportTypes.SalesBreakdown => role is SelloraRoles.CompanyAdmin or SelloraRoles.AreaManager,
        _ => false
    };

    public static ExportCheck Check(string role, CreateExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reportType = request.ReportType?.Trim().ToLowerInvariant() ?? string.Empty;
        var format = request.Format?.Trim().ToLowerInvariant() ?? string.Empty;
        var filters = Normalise(reportType, request.Filters ?? new ExportFilters());

        ExportCheck Result(ExportSubmitStatus status, string? message) => new(status, message, reportType, format, filters);

        if (!ReportTypes.All.Contains(reportType))
        {
            return Result(ExportSubmitStatus.Invalid, "reportType must be sales-summary, sales-breakdown or audit-extract.");
        }

        if (!ExportFormats.All.Contains(format))
        {
            return Result(ExportSubmitStatus.Invalid, "format must be csv or pdf.");
        }

        if (!IsAllowed(role, reportType))
        {
            return Result(ExportSubmitStatus.Forbidden, "This report type cannot be exported with your role.");
        }

        var problem = reportType switch
        {
            ReportTypes.SalesSummary => Summary(filters) is { } summary ? summary.Validate() : "from and to are required (yyyy-MM-dd).",
            ReportTypes.SalesBreakdown => Breakdown(filters) is { } breakdown ? breakdown.Validate() : "groupBy, from and to are required.",
            _ => Audit(filters).Validate()
        };

        return problem is null ? Result(ExportSubmitStatus.Accepted, null) : Result(ExportSubmitStatus.Invalid, problem);
    }

    public static SalesSummaryQuery? Summary(ExportFilters filters) =>
        filters.From is { } from && filters.To is { } to
            ? new SalesSummaryQuery(from, to, filters.CompareFrom, filters.CompareTo)
            : null;

    public static SalesBreakdownQuery? Breakdown(ExportFilters filters) =>
        filters.From is { } from && filters.To is { } to && !string.IsNullOrWhiteSpace(filters.GroupBy)
            ? new SalesBreakdownQuery(filters.GroupBy, from, to, filters.Sort ?? BreakdownSorts.Value, filters.Direction ?? "desc")
            : null;

    public static AuditSearchQuery Audit(ExportFilters filters) => new(
        filters.AggregateType,
        filters.AggregateId,
        filters.OrderReference,
        filters.ActorUserId,
        filters.EventType,
        filters.OccurredFrom,
        filters.OccurredTo);

    public static string Serialise(ExportFilters filters) => JsonSerializer.Serialize(filters, Json);

    public static ExportFilters Deserialise(string json) => JsonSerializer.Deserialize<ExportFilters>(json, Json) ?? new ExportFilters();

    /// <summary>Letters, digits, dash, underscore and dot only; never empty or hidden.</summary>
    public static string SafeFileName(string? name)
    {
        var builder = new StringBuilder();

        foreach (var character in name ?? string.Empty)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                builder.Append(character);
            }
        }

        var safe = builder.ToString().TrimStart('.');
        safe = safe.Length > 100 ? safe[..100] : safe;
        return safe.Length == 0 ? "export" : safe;
    }

    // Keep only the filters the report type uses, with the on-screen defaults applied.
    private static ExportFilters Normalise(string reportType, ExportFilters filters) => reportType switch
    {
        ReportTypes.SalesSummary => new ExportFilters
        {
            From = filters.From,
            To = filters.To,
            CompareFrom = filters.CompareFrom,
            CompareTo = filters.CompareTo
        },
        ReportTypes.SalesBreakdown => new ExportFilters
        {
            From = filters.From,
            To = filters.To,
            GroupBy = Lower(filters.GroupBy),
            Sort = Lower(filters.Sort) ?? BreakdownSorts.Value,
            Direction = Lower(filters.Direction) ?? "desc"
        },
        ReportTypes.AuditExtract => new ExportFilters
        {
            AggregateType = Text(filters.AggregateType),
            AggregateId = filters.AggregateId,
            OrderReference = Text(filters.OrderReference),
            ActorUserId = Text(filters.ActorUserId),
            EventType = Text(filters.EventType),
            OccurredFrom = filters.OccurredFrom,
            OccurredTo = filters.OccurredTo
        },
        _ => filters
    };

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Lower(string? value) => Text(value)?.ToLowerInvariant();
}
