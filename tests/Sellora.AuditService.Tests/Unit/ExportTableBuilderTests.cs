using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Application.Reporting;

namespace Sellora.AuditService.Tests.Unit;

public sealed class ExportTableBuilderTests
{
    private static readonly ExportStamp Stamp = new("Asha", DateTimeOffset.UtcNow, "none", "Company-wide");
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 2);

    [Fact]
    public void A_breakdown_has_one_row_per_on_screen_row_and_a_total()
    {
        var report = new SalesBreakdownResponse("agency", From, To, "value", "desc", "LKR",
            new[]
            {
                new BreakdownRow(Guid.NewGuid(), "Kandy Agency", 2, 1500m, 750m),
                new BreakdownRow(Guid.NewGuid(), "=Galle", 1, 400.5m, 400.5m)
            },
            3, 1900.5m);

        var table = ExportTableBuilder.SalesBreakdown(report, Stamp);

        Assert.Equal(new[] { "Agency", "Orders", "Sales (LKR)", "Average order (LKR)" }, table.Columns);
        Assert.Equal(new[] { "Kandy Agency", "2", "1500.00", "750.00" }, table.Rows[0].Select(cell => cell.Text));
        Assert.Equal(new[] { "=Galle", "1", "400.50", "400.50" }, table.Rows[1].Select(cell => cell.Text));
        Assert.Equal(new[] { "Total", "3", "1900.50", "633.50" }, table.Rows[2].Select(cell => cell.Text));
        Assert.All(table.Rows, row => Assert.True(row[2].IsNumber));
    }

    [Fact]
    public void A_summary_holds_the_totals_statuses_days_and_comparison()
    {
        var figures = new SalesFigures(4500m, 3, 1500m, 4500m, 1000m, 3500m);
        var previous = new SalesFigures(700m, 1, 700m, 700m, 0m, 700m);
        var report = new SalesSummaryResponse(From, To, "LKR", figures,
            new[] { new StatusCount("Confirmed", 3) },
            new[] { new DailySales(From, 4500m, 3), new DailySales(To, 0m, 0) },
            new SalesComparison(From.AddMonths(-1), To.AddMonths(-1), previous, ReportMath.Deltas(figures, previous)));

        var rows = ExportTableBuilder.SalesSummary(report, Stamp).Rows.Select(row => string.Join("|", row.Select(cell => cell.Text))).ToList();

        Assert.Contains("Totals|Sales 2026-09-01 to 2026-09-02|3|4500.00", rows);
        Assert.Contains("Totals|Outstanding||3500.00", rows);
        Assert.Contains("Orders by status|Confirmed|3|", rows);
        Assert.Contains("Daily|2026-09-02|0|0.00", rows);
        Assert.Contains("Comparison|Sales 2026-08-01 to 2026-08-02|1|700.00", rows);
        Assert.Contains("Change|Sales||3800.00", rows);
    }

    [Fact]
    public void An_audit_row_keeps_the_payload_exactly()
    {
        var entry = new AuditEntryDetail(Guid.NewGuid(), 7, Guid.NewGuid(), "OrderConfirmed", "Order", null, "ORD-1", "user-1", "SalesRep",
            new DateTimeOffset(2026, 9, 5, 9, 30, 0, TimeSpan.FromHours(5.5)), DateTimeOffset.UtcNow, null, "sellora.order.v1",
            "1", null, 0, 1, "prev", "hash", "{\"a\": 1}");

        var row = ExportTableBuilder.AuditRow(entry);

        Assert.Equal(ExportTableBuilder.AuditColumns.Count, row.Count);
        Assert.Equal("7", row[0].Text);
        Assert.True(row[0].IsNumber);
        Assert.Equal("2026-09-05 04:00:00.000000", row[1].Text);
        Assert.Equal("{\"a\": 1}", row[^1].Text);
    }

    [Fact]
    public void Scope_and_filters_are_described_for_the_header()
    {
        var province = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Assert.Equal("Company-wide", ExportTableBuilder.Scope(null));
        Assert.Equal($"Provinces: {province:D}", ExportTableBuilder.Scope(new[] { province }));
        Assert.Equal("from: 2026-09-01; to: 2026-09-02; groupBy: rep", ExportTableBuilder.Describe(new ExportFilters { From = From, To = To, GroupBy = "rep" }));
        Assert.Equal("none", ExportTableBuilder.Describe(new ExportFilters()));
    }
}
