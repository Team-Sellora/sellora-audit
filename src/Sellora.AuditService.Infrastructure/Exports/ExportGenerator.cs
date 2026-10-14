using System.Globalization;
using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Domain.Exports;

namespace Sellora.AuditService.Infrastructure.Exports;

/// <summary>US-E7-3-BE2: the on-screen query services, run with the scope snapshot taken at submission.</summary>
public sealed class ExportGenerator(IReportQueryService reports, IAuditReadService audit, TimeProvider clock) : IExportGenerator
{
    public const int AuditPageSize = 1_000;
    public const int MaxAuditRows = 50_000;

    public async Task<ExportFile> GenerateAsync(ReportExport export, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);

        var filters = ExportRequests.Deserialise(export.FiltersJson);
        var now = clock.GetUtcNow();
        var stamp = new ExportStamp(export.RequesterName, now, ExportTableBuilder.Describe(filters), ExportTableBuilder.Scope(export.ScopeProvinceIds));
        var scope = new ReportScope(export.CompanyId, export.ScopeProvinceIds);

        var table = export.ReportType switch
        {
            ReportTypes.SalesSummary => ExportTableBuilder.SalesSummary(
                await reports.SummaryAsync(scope, Required(ExportRequests.Summary(filters)), cancellationToken), stamp),
            ReportTypes.SalesBreakdown => ExportTableBuilder.SalesBreakdown(
                await reports.BreakdownAsync(scope, Required(ExportRequests.Breakdown(filters)), cancellationToken), stamp),
            ReportTypes.AuditExtract => await AuditExtractAsync(export, filters, stamp, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown report type '{export.ReportType}'.")
        };

        var name = $"{export.ReportType}-{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

        return export.Format == ExportFormats.Pdf
            ? new ExportFile(ExportRequests.SafeFileName($"{name}.pdf"), PdfExportWriter.ContentType, PdfExportWriter.Write(table))
            : new ExportFile(ExportRequests.SafeFileName($"{name}.csv"), CsvExportWriter.ContentType, CsvExportWriter.Write(table));
    }

    private async Task<ExportTable> AuditExtractAsync(ReportExport export, ExportFilters filters, ExportStamp stamp, CancellationToken cancellationToken)
    {
        var scope = new AuditReadScope(export.CompanyId, export.ScopeProvinceIds);
        var query = ExportRequests.Audit(filters);
        var rows = new List<IReadOnlyList<ExportCell>>();
        AuditExtractCursor? cursor = null;

        // Read in pages so a long range never loads every entity at once.
        while (rows.Count < MaxAuditRows)
        {
            var take = Math.Min(AuditPageSize, MaxAuditRows - rows.Count);
            var page = await audit.ExtractPageAsync(scope, query, cursor, take, cancellationToken);
            rows.AddRange(page.Select(ExportTableBuilder.AuditRow));

            if (page.Count < take)
            {
                break;
            }

            cursor = new AuditExtractCursor(page[^1].OccurredAt, page[^1].ChainPosition);
        }

        var truncated = rows.Count == MaxAuditRows &&
                        (await audit.ExtractPageAsync(scope, query, cursor, 1, cancellationToken)).Count > 0;

        var notes = new[]
        {
            new KeyValuePair<string, string>("Rows", truncated
                ? $"{rows.Count} (limit reached; narrow the filters for the rest)"
                : rows.Count.ToString(CultureInfo.InvariantCulture))
        };

        return new ExportTable("Audit extract", stamp, ExportTableBuilder.AuditColumns, rows, notes);
    }

    private static T Required<T>(T? query)
        where T : class =>
        query ?? throw new InvalidOperationException("The stored filters are incomplete.");
}
