namespace Sellora.AuditService.Domain.Reporting;

/// <summary>US-E7-2-BE2: sales are counted on their Asia/Colombo date.</summary>
public static class SalesCalendar
{
    // Sri Lanka has used UTC+05:30 all year since 2006, so a fixed offset is exact without tz data.
    public static readonly TimeSpan ColomboOffset = new(5, 30, 0);

    public static DateOnly ToSalesDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(ColomboOffset).DateTime);
}
