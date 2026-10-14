namespace Sellora.AuditService.Application.Reporting;

/// <summary>Null province IDs mean the whole company; otherwise only those provinces (US-E7-2-BE5).</summary>
public sealed record ReportScope(Guid CompanyId, IReadOnlyCollection<Guid>? ProvinceIds)
{
    public bool IsWholeCompany => ProvinceIds is null;

    public string Key() => IsWholeCompany
        ? $"{CompanyId:D}|company"
        : $"{CompanyId:D}|" + string.Join(",", ProvinceIds!.Distinct().OrderBy(id => id).Select(id => id.ToString("D")));
}

public sealed record SalesSummaryQuery(DateOnly From, DateOnly To, DateOnly? CompareFrom = null, DateOnly? CompareTo = null)
{
    public string? Validate()
    {
        if (ReportValidation.Range(From, To, "from", "to") is { } range)
        {
            return range;
        }

        if (CompareFrom is null != CompareTo is null)
        {
            return "compareFrom and compareTo must be given together.";
        }

        return CompareFrom is { } compareFrom && CompareTo is { } compareTo
            ? ReportValidation.Range(compareFrom, compareTo, "compareFrom", "compareTo")
            : null;
    }
}

public static class BreakdownGroupings
{
    public const string Rep = "rep";
    public const string Territory = "territory";
    public const string Agency = "agency";
    public const string Product = "product";

    public static readonly IReadOnlyList<string> All = new[] { Rep, Territory, Agency, Product };
}

public static class BreakdownSorts
{
    public const string Value = "value";
    public const string Count = "count";
    public const string Name = "name";

    public static readonly IReadOnlyList<string> All = new[] { Value, Count, Name };
}

public sealed record SalesBreakdownQuery(
    string GroupBy,
    DateOnly From,
    DateOnly To,
    string Sort = BreakdownSorts.Value,
    string Direction = "desc")
{
    public string? Validate()
    {
        if (!BreakdownGroupings.All.Contains(GroupBy))
        {
            return "groupBy must be rep, territory, agency or product.";
        }

        if (!BreakdownSorts.All.Contains(Sort))
        {
            return "sort must be value, count or name.";
        }

        if (Direction is not ("asc" or "desc"))
        {
            return "direction must be asc or desc.";
        }

        return ReportValidation.Range(From, To, "from", "to");
    }
}

public static class ReportValidation
{
    public const int MaxRangeDays = 1100;

    /// <summary>Both ends inclusive, so the range covers to - from + 1 days.</summary>
    public static string? Range(DateOnly from, DateOnly to, string fromName, string toName)
    {
        if (from > to)
        {
            return $"{fromName} must not be later than {toName}.";
        }

        return to.DayNumber - from.DayNumber + 1 > MaxRangeDays
            ? $"The range from {fromName} to {toName} may span at most {MaxRangeDays} days."
            : null;
    }
}

public sealed record SalesFigures(
    decimal SalesValue,
    int OrderCount,
    decimal AverageOrderValue,
    decimal Expected,
    decimal Collected,
    decimal Outstanding);

public sealed record StatusCount(string Status, int Count);

public sealed record DailySales(DateOnly Date, decimal SalesValue, int OrderCount);

/// <param name="Percent">Null when the comparison value is zero.</param>
public sealed record Delta(decimal Absolute, decimal? Percent);

public sealed record SalesDeltas(Delta SalesValue, Delta OrderCount, Delta AverageOrderValue, Delta Collected);

public sealed record SalesComparison(DateOnly From, DateOnly To, SalesFigures Totals, SalesDeltas Deltas);

public sealed record SalesSummaryResponse(
    DateOnly From,
    DateOnly To,
    string Currency,
    SalesFigures Totals,
    IReadOnlyList<StatusCount> StatusBreakdown,
    IReadOnlyList<DailySales> Daily,
    SalesComparison? Comparison);

/// <param name="Key">The rep, territory, agency or product ID.</param>
public sealed record BreakdownRow(Guid Key, string Name, int OrderCount, decimal SalesValue, decimal AverageOrderValue);

public sealed record SalesBreakdownResponse(
    string GroupBy,
    DateOnly From,
    DateOnly To,
    string Sort,
    string Direction,
    string Currency,
    IReadOnlyList<BreakdownRow> Rows,
    int TotalOrderCount,
    decimal TotalSalesValue);

public sealed record ProjectionRebuildResponse(long EntriesReplayed, int OrdersProjected, DateTimeOffset CompletedAt);

public interface IReportQueryService
{
    Task<SalesSummaryResponse> SummaryAsync(ReportScope scope, SalesSummaryQuery query, CancellationToken cancellationToken);

    Task<SalesBreakdownResponse> BreakdownAsync(ReportScope scope, SalesBreakdownQuery query, CancellationToken cancellationToken);
}

public interface IProjectionRebuildService
{
    Task<ProjectionRebuildResponse> RebuildAsync(Guid companyId, CancellationToken cancellationToken);
}
