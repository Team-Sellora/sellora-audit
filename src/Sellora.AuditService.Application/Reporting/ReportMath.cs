namespace Sellora.AuditService.Application.Reporting;

/// <summary>US-E7-2-BE2: the arithmetic behind the dashboards, kept pure so it is tested on its own.</summary>
public static class ReportMath
{
    public static decimal Average(decimal value, int count) =>
        count == 0 ? 0m : Math.Round(value / count, 2, MidpointRounding.AwayFromZero);

    public static SalesFigures Figures(decimal salesValue, int orderCount, decimal collected) => new(
        salesValue,
        orderCount,
        Average(salesValue, orderCount),
        Expected: salesValue,
        Collected: collected,
        Outstanding: salesValue - collected);

    public static Delta Delta(decimal current, decimal previous) => new(
        current - previous,
        previous == 0m ? null : Math.Round((current - previous) / previous * 100m, 1, MidpointRounding.AwayFromZero));

    public static SalesDeltas Deltas(SalesFigures current, SalesFigures previous) => new(
        Delta(current.SalesValue, previous.SalesValue),
        Delta(current.OrderCount, previous.OrderCount),
        Delta(current.AverageOrderValue, previous.AverageOrderValue),
        Delta(current.Collected, previous.Collected));

    /// <summary>One point per day of the range, zero where nothing sold.</summary>
    public static IReadOnlyList<DailySales> FillDaily(
        DateOnly from,
        DateOnly to,
        IReadOnlyDictionary<DateOnly, (decimal Value, int Count)> byDay)
    {
        var days = new List<DailySales>(to.DayNumber - from.DayNumber + 1);

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var (value, count) = byDay.TryGetValue(day, out var found) ? found : (0m, 0);
            days.Add(new DailySales(day, value, count));
        }

        return days;
    }

    public static IReadOnlyList<BreakdownRow> Sort(IEnumerable<BreakdownRow> rows, string sort, string direction)
    {
        var descending = direction == "desc";

        var ordered = sort switch
        {
            BreakdownSorts.Count => descending ? rows.OrderByDescending(row => row.OrderCount) : rows.OrderBy(row => row.OrderCount),
            BreakdownSorts.Name => descending
                ? rows.OrderByDescending(row => row.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            _ => descending ? rows.OrderByDescending(row => row.SalesValue) : rows.OrderBy(row => row.SalesValue)
        };

        return ordered.ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Key).ToList();
    }

    /// <summary>Members with sales, plus every known member in scope with none, at zero.</summary>
    public static IReadOnlyList<BreakdownRow> WithZeroRows(
        IReadOnlyDictionary<Guid, (string? Name, int Count, decimal Value)> sales,
        IReadOnlyDictionary<Guid, string?> knownMembers,
        Func<Guid, string> fallbackName)
    {
        var rows = new List<BreakdownRow>();

        foreach (var (key, figures) in sales)
        {
            var name = knownMembers.TryGetValue(key, out var known) && !string.IsNullOrWhiteSpace(known)
                ? known
                : figures.Name ?? fallbackName(key);
            rows.Add(new BreakdownRow(key, name, figures.Count, figures.Value, Average(figures.Value, figures.Count)));
        }

        foreach (var (key, name) in knownMembers)
        {
            if (!sales.ContainsKey(key))
            {
                rows.Add(new BreakdownRow(key, string.IsNullOrWhiteSpace(name) ? fallbackName(key) : name, 0, 0m, 0m));
            }
        }

        return rows;
    }

    public static string ShortName(string prefix, Guid id) => $"{prefix} {id.ToString("N")[..8]}";
}
