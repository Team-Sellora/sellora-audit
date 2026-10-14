using Sellora.AuditService.Domain.Exports;

namespace Sellora.AuditService.Tests.Unit;

public sealed class ReportExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Company = Guid.NewGuid();

    [Fact]
    public void A_new_export_is_queued_with_the_scope_snapshot()
    {
        var province = Guid.NewGuid();

        var export = Queue(new[] { province, province });

        Assert.Equal(ExportStatus.Queued, export.Status);
        Assert.Equal(new[] { province }, export.ScopeProvinceIds);
        Assert.Equal(Now, export.CreatedAt);
        Assert.Equal(ExportDownload.NotReady, export.DownloadAt(Now));
    }

    [Fact]
    public void A_company_admin_snapshot_is_null_and_a_missing_name_falls_back_to_the_subject()
    {
        var export = ReportExport.Queue(Company, "sub-1", " ", "CompanyAdmin", null, ReportTypes.SalesSummary, "{}", ExportFormats.Csv, Now);

        Assert.Null(export.ScopeProvinceIds);
        Assert.Equal("sub-1", export.RequesterName);
    }

    [Theory]
    [InlineData("payroll", "csv")]
    [InlineData("sales-summary", "xlsx")]
    public void Unknown_types_and_formats_are_rejected(string type, string format)
    {
        Assert.Throws<ArgumentException>(() =>
            ReportExport.Queue(Company, "sub", "Name", "CompanyAdmin", null, type, "{}", format, Now));
    }

    [Fact]
    public void A_completed_export_expires_after_the_configured_hours()
    {
        var export = Queue();
        export.Start(Now);

        export.Complete("a.csv", "text/csv", new byte[] { 1 }, Now.AddMinutes(1), TimeSpan.FromHours(24));

        Assert.Equal(ExportStatus.Ready, export.Status);
        Assert.Equal(Now.AddMinutes(1).AddHours(24), export.ExpiresAt);
        Assert.Equal(ExportDownload.Ready, export.DownloadAt(export.ExpiresAt!.Value.AddTicks(-1)));
        Assert.Equal(ExportDownload.Gone, export.DownloadAt(export.ExpiresAt.Value));
    }

    [Fact]
    public void Expiring_drops_the_content_and_keeps_the_row()
    {
        var export = Queue();
        export.Start(Now);
        export.Complete("a.csv", "text/csv", new byte[] { 1 }, Now, TimeSpan.FromHours(1));

        export.Expire();

        Assert.Equal(ExportStatus.Expired, export.Status);
        Assert.Null(export.Content);
        Assert.Equal("a.csv", export.FileName);
        Assert.Equal(ExportDownload.Gone, export.DownloadAt(Now));
    }

    [Fact]
    public void A_failure_keeps_a_short_error()
    {
        var export = Queue();
        export.Start(Now);

        export.Fail(new string('x', 5000), Now);

        Assert.Equal(ExportStatus.Failed, export.Status);
        Assert.Equal(ReportExport.MaxErrorLength, export.Error!.Length);
        Assert.Equal(ExportDownload.NotReady, export.DownloadAt(Now));
    }

    [Fact]
    public void Transitions_out_of_order_are_refused()
    {
        var export = Queue();

        Assert.Throws<InvalidOperationException>(() => export.Complete("a.csv", "text/csv", Array.Empty<byte>(), Now, TimeSpan.FromHours(1)));
        Assert.Throws<InvalidOperationException>(() => export.Fail("x", Now));
        Assert.Throws<InvalidOperationException>(export.Expire);

        export.Start(Now);
        Assert.Throws<InvalidOperationException>(() => export.Start(Now));
    }

    private static ReportExport Queue(Guid[]? provinces = null) =>
        ReportExport.Queue(Company, "sub", "Asha", "AreaManager", provinces ?? new[] { Guid.NewGuid() },
            ReportTypes.SalesSummary, "{}", ExportFormats.Csv, Now);
}
