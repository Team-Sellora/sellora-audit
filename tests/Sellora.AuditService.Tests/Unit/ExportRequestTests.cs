using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Domain.Exports;

namespace Sellora.AuditService.Tests.Unit;

public sealed class ExportRequestTests
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);

    [Theory]
    [InlineData("CompanyAdmin", "sales-summary", true)]
    [InlineData("CompanyAdmin", "sales-breakdown", true)]
    [InlineData("CompanyAdmin", "audit-extract", true)]
    [InlineData("AreaManager", "sales-summary", true)]
    [InlineData("AreaManager", "sales-breakdown", true)]
    [InlineData("AreaManager", "audit-extract", false)]
    [InlineData("AgencyOperator", "sales-summary", false)]
    [InlineData("SalesRep", "sales-breakdown", false)]
    [InlineData("ShopOwner", "audit-extract", false)]
    public void Role_rules(string role, string reportType, bool allowed)
    {
        Assert.Equal(allowed, ExportRequests.IsAllowed(role, reportType));
    }

    [Fact]
    public void An_area_manager_audit_extract_is_forbidden()
    {
        var check = ExportRequests.Check("AreaManager", new CreateExportRequest("audit-extract", "csv", null));

        Assert.Equal(ExportSubmitStatus.Forbidden, check.Status);
    }

    [Theory]
    [InlineData("payroll", "csv")]
    [InlineData(null, "csv")]
    [InlineData("sales-summary", "xlsx")]
    [InlineData("sales-summary", null)]
    public void Unknown_types_and_formats_are_invalid(string? type, string? format)
    {
        var check = ExportRequests.Check("CompanyAdmin", new CreateExportRequest(type, format, new ExportFilters { From = From, To = To }));

        Assert.Equal(ExportSubmitStatus.Invalid, check.Status);
    }

    public static TheoryData<string, ExportFilters> InvalidFilters => new()
    {
        { "sales-summary", new ExportFilters { From = From } },
        { "sales-summary", new ExportFilters { From = To, To = From } },
        { "sales-summary", new ExportFilters { From = new DateOnly(2024, 1, 1), To = new DateOnly(2027, 1, 5) } },
        { "sales-summary", new ExportFilters { From = From, To = To, CompareFrom = From } },
        { "sales-summary", new ExportFilters { From = From, To = To, CompareFrom = To, CompareTo = From } },
        { "sales-breakdown", new ExportFilters { From = From, To = To } },
        { "sales-breakdown", new ExportFilters { GroupBy = "shop", From = From, To = To } },
        { "sales-breakdown", new ExportFilters { GroupBy = "rep", From = From, To = To, Sort = "total" } },
        { "sales-breakdown", new ExportFilters { GroupBy = "rep", From = From, To = To, Direction = "up" } },
        { "sales-breakdown", new ExportFilters { GroupBy = "rep", From = To, To = From } },
        { "audit-extract", new ExportFilters { OccurredFrom = DateTimeOffset.UtcNow, OccurredTo = DateTimeOffset.UtcNow.AddDays(-1) } }
    };

    [Theory]
    [MemberData(nameof(InvalidFilters))]
    public void Filters_are_validated_like_the_on_screen_endpoints(string type, ExportFilters filters)
    {
        var check = ExportRequests.Check("CompanyAdmin", new CreateExportRequest(type, "csv", filters));

        Assert.Equal(ExportSubmitStatus.Invalid, check.Status);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
    }

    [Fact]
    public void The_longest_allowed_range_is_accepted()
    {
        var check = ExportRequests.Check("CompanyAdmin", new CreateExportRequest("sales-summary", "pdf",
            new ExportFilters { From = From, To = From.AddDays(1099) }));

        Assert.Equal(ExportSubmitStatus.Accepted, check.Status);
    }

    [Fact]
    public void A_breakdown_gets_the_on_screen_defaults_and_drops_unrelated_filters()
    {
        var check = ExportRequests.Check("AreaManager", new CreateExportRequest(" Sales-Breakdown ", "CSV",
            new ExportFilters { GroupBy = " Agency ", From = From, To = To, OrderReference = "ORD-1", CompareFrom = From }));

        Assert.Equal(ExportSubmitStatus.Accepted, check.Status);
        Assert.Equal(ReportTypes.SalesBreakdown, check.ReportType);
        Assert.Equal(ExportFormats.Csv, check.Format);
        Assert.Equal(new ExportFilters { GroupBy = "agency", From = From, To = To, Sort = "value", Direction = "desc" }, check.Filters);
    }

    [Fact]
    public void Filters_round_trip_through_json()
    {
        var filters = new ExportFilters { From = From, To = To, CompareFrom = From.AddMonths(-1), CompareTo = To.AddMonths(-1) };

        Assert.Equal(filters, ExportRequests.Deserialise(ExportRequests.Serialise(filters)));
    }

    [Theory]
    [InlineData("sales-summary-20261014-090000.csv", "sales-summary-20261014-090000.csv")]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("report\"; filename=evil.exe", "reportfilenameevil.exe")]
    [InlineData("සාරාංශය.csv", "csv")]
    [InlineData("...", "export")]
    [InlineData(null, "export")]
    public void File_names_are_sanitised(string? name, string expected)
    {
        Assert.Equal(expected, ExportRequests.SafeFileName(name));
    }
}
