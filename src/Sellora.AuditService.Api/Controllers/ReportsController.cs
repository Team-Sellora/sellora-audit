using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.AuditService.Api.Authorization;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Application.Reporting;

namespace Sellora.AuditService.Api.Controllers;

/// <summary>
/// US-E7-2: read-only dashboards over the reporting projections. Dates are
/// Asia/Colombo days (yyyy-MM-dd), both ends inclusive. The only write is
/// the projection rebuild, which never touches the audit log.
/// </summary>
[ApiController]
[Route("api/reports")]
public sealed class ReportsController(
    IReportQueryService reports,
    IProjectionRebuildService rebuild,
    ICurrentUserContext caller) : ControllerBase
{
    private const string InvalidReport = "Invalid report";

    [HttpGet("sales-summary")]
    [Authorize(Policy = RolePolicies.RequireReportReader)]
    [ProducesResponseType(typeof(SalesSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SalesSummary(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] DateOnly? compareFrom,
        [FromQuery] DateOnly? compareTo,
        CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        if (from is not { } start || to is not { } end)
        {
            return CallerScopes.Problem(StatusCodes.Status400BadRequest, InvalidReport, "from and to are required (yyyy-MM-dd).");
        }

        var query = new SalesSummaryQuery(start, end, compareFrom, compareTo);

        if (query.Validate() is { } problem)
        {
            return CallerScopes.Problem(StatusCodes.Status400BadRequest, InvalidReport, problem);
        }

        return Ok(await reports.SummaryAsync(new ReportScope(access.CompanyId, access.ProvinceIds), query, cancellationToken));
    }

    [HttpGet("sales-breakdown")]
    [Authorize(Policy = RolePolicies.RequireReportReader)]
    [ProducesResponseType(typeof(SalesBreakdownResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SalesBreakdown(
        [FromQuery] string? groupBy,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? sort,
        [FromQuery] string? direction,
        CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        if (from is not { } start || to is not { } end || string.IsNullOrWhiteSpace(groupBy))
        {
            return CallerScopes.Problem(StatusCodes.Status400BadRequest, InvalidReport, "groupBy, from and to are required.");
        }

        var query = new SalesBreakdownQuery(
            groupBy.Trim().ToLowerInvariant(),
            start,
            end,
            string.IsNullOrWhiteSpace(sort) ? BreakdownSorts.Value : sort.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(direction) ? "desc" : direction.Trim().ToLowerInvariant());

        if (query.Validate() is { } problem)
        {
            return CallerScopes.Problem(StatusCodes.Status400BadRequest, InvalidReport, problem);
        }

        return Ok(await reports.BreakdownAsync(new ReportScope(access.CompanyId, access.ProvinceIds), query, cancellationToken));
    }

    /// <summary>US-E7-2-BE6: rebuilds the caller's company projections from its audit log.</summary>
    [HttpPost("projections/rebuild")]
    [Authorize(Policy = RolePolicies.RequireCompanyAdmin)]
    [ProducesResponseType(typeof(ProjectionRebuildResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RebuildProjections(CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        return Ok(await rebuild.RebuildAsync(access.CompanyId, cancellationToken));
    }
}
