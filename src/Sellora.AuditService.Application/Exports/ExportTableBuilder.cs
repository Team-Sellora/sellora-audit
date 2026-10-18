using System.Globalization;
using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Application.Reporting;

namespace Sellora.AuditService.Application.Exports;

/// <summary>US-E7-3-BE2: turns the on-screen responses into export tables, figure for figure.</summary>
public static class ExportTableBuilder
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static readonly IReadOnlyList<string> AuditColumns = new[]
    {
        "Chain position", "Occurred at (UTC)", "Event type", "Aggregate type", "Aggregate ID", "Order reference",
        "Actor", "Actor role", "Correlation ID", "Source topic", "Hash", "Previous hash", "Payload"
    };

    public static ExportTable SalesSummary(SalesSummaryResponse report, ExportStamp stamp)
    {
        var rows = new List<IReadOnlyList<ExportCell>>();
        AddFigures(rows, "Totals", report.From, report.To, report.Totals);

        rows.AddRange(report.StatusBreakdown.Select(status => Row("Orders by status", status.Status, status.Count, null)));
        rows.AddRange(report.Daily.Select(day => Row("Daily", Date(day.Date), day.OrderCount, day.SalesValue)));

        if (report.Comparison is { } comparison)
        {
            AddFigures(rows, "Comparison", comparison.From, comparison.To, comparison.Totals);
            AddDelta(rows, "Sales", comparison.Deltas.SalesValue);
            AddDelta(rows, "Orders", comparison.Deltas.OrderCount);
            AddDelta(rows, "Average order value", comparison.Deltas.AverageOrderValue);
            AddDelta(rows, "Collected", comparison.Deltas.Collected);
        }

        return new ExportTable("Sales summary", stamp, new[] { "Section", "Item", "Orders", $"Value ({report.Currency})" }, rows);
    }

    public static ExportTable SalesBreakdown(SalesBreakdownResponse report, ExportStamp stamp)
    {
        var rows = report.Rows
            .Select(row => (IReadOnlyList<ExportCell>)new ExportCell[] { row.Name, row.OrderCount, row.SalesValue, row.AverageOrderValue })
            .Append(new ExportCell[] { "Total", report.TotalOrderCount, report.TotalSalesValue, ReportMath.Average(report.TotalSalesValue, report.TotalOrderCount) })
            .ToList();

        return new ExportTable(
            $"Sales breakdown by {report.GroupBy}",
            stamp,
            new[] { Label(report.GroupBy), "Orders", $"Sales ({report.Currency})", $"Average order ({report.Currency})" },
            rows);
    }

    public static IReadOnlyList<ExportCell> AuditRow(AuditEntryDetail entry) => new ExportCell[]
    {
        entry.ChainPosition,
        entry.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", Invariant),
        entry.EventType,
        entry.AggregateType,
        entry.AggregateId?.ToString("D"),
        entry.OrderReference,
        entry.ActorUserId,
        entry.ActorRole,
        entry.CorrelationId,
        entry.SourceTopic,
        entry.Hash,
        entry.PreviousHash,
        entry.Payload
    };

    public static string Scope(IReadOnlyCollection<Guid>? provinceIds) => provinceIds switch
    {
        null => "Company-wide",
        { Count: 0 } => "No provinces",
        _ => "Provinces: " + string.Join(", ", provinceIds.OrderBy(id => id).Select(id => id.ToString("D")))
    };

    public static string Describe(ExportFilters filters)
    {
        var parts = new (string Name, string? Value)[]
            {
                ("from", filters.From is { } from ? Date(from) : null),
                ("to", filters.To is { } to ? Date(to) : null),
                ("compareFrom", filters.CompareFrom is { } compareFrom ? Date(compareFrom) : null),
                ("compareTo", filters.CompareTo is { } compareTo ? Date(compareTo) : null),
                ("groupBy", filters.GroupBy),
                ("sort", filters.Sort),
                ("direction", filters.Direction),
                ("aggregateType", filters.AggregateType),
                ("aggregateId", filters.AggregateId?.ToString("D")),
                ("orderReference", filters.OrderReference),
                ("actorUserId", filters.ActorUserId),
                ("eventType", filters.EventType),
                ("occurredFrom", filters.OccurredFrom?.ToString("O", Invariant)),
                ("occurredTo", filters.OccurredTo?.ToString("O", Invariant))
            }
            .Where(part => !string.IsNullOrWhiteSpace(part.Value))
            .Select(part => $"{part.Name}: {part.Value}")
            .ToList();

        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    private static void AddFigures(List<IReadOnlyList<ExportCell>> rows, string section, DateOnly from, DateOnly to, SalesFigures figures)
    {
        rows.Add(Row(section, $"Sales {Date(from)} to {Date(to)}", figures.OrderCount, figures.SalesValue));
        rows.Add(Row(section, "Average order value", null, figures.AverageOrderValue));
        rows.Add(Row(section, "Expected", null, figures.Expected));
        rows.Add(Row(section, "Collected", null, figures.Collected));
        rows.Add(Row(section, "Outstanding", null, figures.Outstanding));
    }

    private static void AddDelta(List<IReadOnlyList<ExportCell>> rows, string item, Delta delta)
    {
        rows.Add(new ExportCell[] { "Change", item, string.Empty, delta.Absolute });
        rows.Add(new ExportCell[] { "Change %", item, string.Empty, ExportCell.Percent(delta.Percent) });
    }

    private static ExportCell[] Row(string section, string item, int? orders, decimal? value) => new[]
    {
        (ExportCell)section,
        item,
        orders is { } count ? count : string.Empty,
        value is { } amount ? amount : string.Empty
    };

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", Invariant);

    private static string Label(string groupBy) => groupBy switch
    {
        BreakdownGroupings.Rep => "Sales rep",
        BreakdownGroupings.Territory => "Territory",
        BreakdownGroupings.Agency => "Agency",
        BreakdownGroupings.Product => "Product",
        _ => groupBy
    };
}
