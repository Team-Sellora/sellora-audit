using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.AuditService.Api.Authorization;
using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Api.Controllers;

/// <summary>
/// US-E7-1-BE7/BE8. The audit log is read only through the API: there is
/// intentionally no POST, PUT, PATCH or DELETE here, and
/// AuditRouteImmutabilityTests fails CI if one is added.
/// </summary>
[ApiController]
[Route("api/audit")]
public sealed class AuditController(
    IAuditReadService reads,
    IChainVerificationService verification,
    ICurrentUserContext caller) : ControllerBase
{
    /// <summary>
    /// Search. No company parameter: the company is the token's. Filtered by
    /// order reference or aggregate, entries come oldest first (a timeline);
    /// otherwise newest first.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = RolePolicies.RequireAuditReader)]
    [ProducesResponseType(typeof(PagedResponse<AuditEntrySummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery] string? aggregateType,
        [FromQuery] Guid? aggregateId,
        [FromQuery] string? orderReference,
        [FromQuery] string? actorUserId,
        [FromQuery] string? eventType,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AuditSearchQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        var query = new AuditSearchQuery(aggregateType, aggregateId, orderReference, actorUserId, eventType, from, to, page, pageSize);

        if (query.Validate() is { } problem)
        {
            return CallerScopes.Problem(StatusCodes.Status400BadRequest, "Invalid audit search", problem);
        }

        return Ok(await reads.SearchAsync(new AuditReadScope(access.CompanyId, access.ProvinceIds), query, cancellationToken));
    }

    /// <summary>
    /// One entry with its full payload, exactly as received. Another
    /// company's entry and an entry outside an Area Manager's provinces both
    /// answer 404, so IDs cannot be probed.
    /// </summary>
    [HttpGet("{auditEntryId:guid}")]
    [Authorize(Policy = RolePolicies.RequireAuditReader)]
    [ProducesResponseType(typeof(AuditEntryDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid auditEntryId, CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        var entry = await reads.GetAsync(new AuditReadScope(access.CompanyId, access.ProvinceIds), auditEntryId, cancellationToken);

        return entry is null
            ? CallerScopes.Problem(StatusCodes.Status404NotFound, "Audit entry not found", $"No audit entry {auditEntryId} is visible to the caller.")
            : Ok(entry);
    }

    /// <summary>Recomputes the caller's company chain and reports the first broken link.</summary>
    [HttpGet("verify")]
    [Authorize(Policy = RolePolicies.RequireCompanyAdmin)]
    [ProducesResponseType(typeof(VerifyChainResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        return Ok(await verification.VerifyAsync(access.CompanyId, cancellationToken));
    }
}
