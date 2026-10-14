using System.Text;
using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Infrastructure.Exports;

namespace Sellora.AuditService.Tests.Unit;

public sealed class PdfExportWriterTests
{
    public PdfExportWriterTests() => PdfExportWriter.UseCommunityLicence();

    [Fact]
    public void The_writer_returns_a_pdf()
    {
        var table = new ExportTable(
            "Sales summary",
            new ExportStamp("Asha", DateTimeOffset.UtcNow, "from: 2026-09-01; to: 2026-09-30", "Whole company"),
            new[] { "Measure", "Value" },
            Enumerable.Range(1, 200).Select(day => (IReadOnlyList<ExportCell>)new ExportCell[] { $"Day {day}", day * 10m }).ToList());

        var bytes = PdfExportWriter.Write(table);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void An_empty_table_still_renders()
    {
        var table = new ExportTable("Audit extract", new ExportStamp("Asha", DateTimeOffset.UtcNow, "none", "Whole company"),
            new[] { "Event" }, Array.Empty<IReadOnlyList<ExportCell>>());

        Assert.Equal("%PDF", Encoding.ASCII.GetString(PdfExportWriter.Write(table), 0, 4));
    }
}
