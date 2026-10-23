using Sellora.AuditService.Application.Reporting;

namespace Sellora.AuditService.Tests.Unit;

/// <summary>US-E7-2-BE2: the dashboard arithmetic and the request rules.</summary>
public sealed class ReportMathTests
{
    [Fact]
    public void Figures_follow_the_sales_definition()
    {
        var figures = ReportMath.Figures(salesValue: 4500m, orderCount: 3, collected: 1000m);

        Assert.Equal(4500m, figures.SalesValue);
        Assert.Equal(4500m, figures.Expected);
        Assert.Equal(1000m, figures.Collected);
        Assert.Equal(3500m, figures.Outstanding);
        Assert.Equal(1500m, figures.AverageOrderValue);
    }

    [Theory]
    [InlineData("10.01", 2, "5.01")]
    [InlineData("-10.01", 2, "-5.01")]
    [InlineData("100", 3, "33.33")]
    [InlineData("200", 3, "66.67")]
    [InlineData("0", 0, "0")]
    public void The_average_order_value_rounds_half_away_from_zero_to_2_dp(string value, int count, string expected)
    {
        Assert.Equal(decimal.Parse(expected), ReportMath.Average(decimal.Parse(value), count));
    }

    [Fact]
    public void A_delta_has_an_absolute_value_and_a_percent_to_1_dp()
    {
        var delta = ReportMath.Delta(700m, 4500m);

        Assert.Equal(-3800m, delta.Absolute);
        Assert.Equal(-84.4m, delta.Percent);
    }

    [Fact]
    public void The_percent_rounds_half_away_from_zero()
    {
        Assert.Equal(0.1m, ReportMath.Delta(1000.5m, 1000m).Percent);
        Assert.Equal(-0.1m, ReportMath.Delta(999.5m, 1000m).Percent);
    }

    [Fact]
    public void The_percent_is_null_when_the_comparison_value_is_zero()
    {
        var delta = ReportMath.Delta(500m, 0m);

        Assert.Equal(500m, delta.Absolute);
        Assert.Null(delta.Percent);
    }

    [Fact]
    public void Deltas_cover_sales_orders_aov_and_collected()
    {
        var deltas = ReportMath.Deltas(ReportMath.Figures(300m, 3, 100m), ReportMath.Figures(0m, 0, 0m));

        Assert.Equal(3m, deltas.OrderCount.Absolute);
        Assert.Null(deltas.OrderCount.Percent);
        Assert.Equal(100m, deltas.AverageOrderValue.Absolute);
        Assert.Equal(100m, deltas.Collected.Absolute);
    }

    [Fact]
    public void The_daily_series_has_one_point_per_day_with_zero_where_nothing_sold()
    {
        var from = new DateOnly(2026, 9, 28);
        var to = new DateOnly(2026, 10, 2);
        var sold = new Dictionary<DateOnly, (decimal Value, int Count)> { [new DateOnly(2026, 9, 30)] = (1500m, 1) };

        var days = ReportMath.FillDaily(from, to, sold);

        Assert.Equal(5, days.Count);
        Assert.Equal(from, days[0].Date);
        Assert.Equal(to, days[^1].Date);
        Assert.Equal(new[] { 0m, 0m, 1500m, 0m, 0m }, days.Select(day => day.SalesValue));
        Assert.Equal(new[] { 0, 0, 1, 0, 0 }, days.Select(day => day.OrderCount));
    }

    [Fact]
    public void Known_members_without_sales_are_listed_at_zero()
    {
        var seller = Guid.NewGuid();
        var idle = Guid.NewGuid();

        var rows = ReportMath.WithZeroRows(
            new Dictionary<Guid, (string? Name, int Count, decimal Value)> { [seller] = ("Order name", 2, 300m) },
            new Dictionary<Guid, string?> { [seller] = "Nimal Perera", [idle] = null },
            id => ReportMath.ShortName("Rep", id));

        Assert.Contains(rows, row => row.Key == seller && row.Name == "Nimal Perera" && row.AverageOrderValue == 150m);
        Assert.Contains(rows, row => row.Key == idle && row.OrderCount == 0 && row.SalesValue == 0m && row.Name == $"Rep {idle:N}"[..12]);
    }

    [Theory]
    [InlineData("value", "desc", "B,A,C")]
    [InlineData("value", "asc", "C,A,B")]
    [InlineData("count", "desc", "C,B,A")]
    [InlineData("name", "asc", "A,B,C")]
    [InlineData("name", "desc", "C,B,A")]
    public void Rows_sort_by_value_count_or_name_both_ways(string sort, string direction, string expected)
    {
        var rows = new[]
        {
            new BreakdownRow(Guid.NewGuid(), "A", 1, 200m, 200m),
            new BreakdownRow(Guid.NewGuid(), "B", 2, 300m, 150m),
            new BreakdownRow(Guid.NewGuid(), "C", 3, 0m, 0m)
        };

        var sorted = ReportMath.Sort(rows, sort, direction);

        Assert.Equal(expected, string.Join(',', sorted.Select(row => row.Name)));
    }

    [Fact]
    public void From_later_than_to_is_rejected()
    {
        Assert.NotNull(new SalesSummaryQuery(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1)).Validate());
    }

    [Fact]
    public void A_range_may_cover_at_most_1100_days_counting_both_ends()
    {
        var from = new DateOnly(2024, 1, 1);

        Assert.Null(new SalesSummaryQuery(from, from.AddDays(1099)).Validate());
        Assert.NotNull(new SalesSummaryQuery(from, from.AddDays(1100)).Validate());
        Assert.Null(new SalesSummaryQuery(from, from).Validate());
    }

    [Fact]
    public void The_comparison_range_needs_both_ends_and_follows_the_same_rules()
    {
        var day = new DateOnly(2026, 9, 1);

        Assert.NotNull(new SalesSummaryQuery(day, day, CompareFrom: day).Validate());
        Assert.NotNull(new SalesSummaryQuery(day, day, day.AddDays(1), day).Validate());
        Assert.Null(new SalesSummaryQuery(day, day.AddDays(29), day.AddDays(-30), day.AddDays(-1)).Validate());
    }

    [Theory]
    [InlineData("shop", "value", "desc")]
    [InlineData("rep", "total", "desc")]
    [InlineData("rep", "value", "down")]
    public void A_breakdown_rejects_unknown_grouping_sort_or_direction(string groupBy, string sort, string direction)
    {
        var day = new DateOnly(2026, 9, 1);

        Assert.NotNull(new SalesBreakdownQuery(groupBy, day, day, sort, direction).Validate());
    }

    [Fact]
    public void The_scope_key_ignores_province_order()
    {
        var company = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(new ReportScope(company, new[] { a, b }).Key(), new ReportScope(company, new[] { b, a }).Key());
        Assert.NotEqual(new ReportScope(company, null).Key(), new ReportScope(company, Array.Empty<Guid>()).Key());
    }
}
