using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.AuditService.Api.Authorization;
using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Api.Controllers;

/// <summary>US-E7-3: async exports, visible only to the requester (404 otherwise), gone after expiry (410).</summary>
[ApiController]
[Route("api/reports/export")]
public sealed class ReportExportsController(IExportService exports, ICurrentUserContext caller) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = RolePolicies.RequireReportReader)]
    [ProducesResponseType(typeof(ExportAccepted), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Submit(CreateExportRequest body, CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        // Identity and scope come from the token now; the worker has no token.
        var requester = new ExportRequester(access.CompanyId, access.Subject, access.Name, access.Role, access.ProvinceIds);
        var result = await exports.SubmitAsync(requester, body, cancellationToken);

        return result.Status switch
        {
            ExportSubmitStatus.Accepted => AcceptedAtAction(nameof(GetStatus), new { exportId = result.ExportId }, new ExportAccepted(result.ExportId!.Value)),
            ExportSubmitStatus.Forbidden => CallerScopes.Problem(StatusCodes.Status403Forbidden, "Export not allowed", result.Message),
            _ => CallerScopes.Problem(StatusCodes.Status400BadRequest, "Invalid export", result.Message)
        };
    }

    /// <summary>The caller's latest exports, newest first, without content.</summary>
    [HttpGet]
    [Authorize(Policy = RolePolicies.RequireReportReader)]
    [ProducesResponseType(typeof(IReadOnlyList<ExportStatusResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMine(CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        return Ok(await exports.ListMineAsync(access.CompanyId, access.Subject, cancellationToken));
    }

    [HttpGet("{exportId:guid}")]
    [Authorize(Policy = RolePolicies.RequireReportReader)]
    [ProducesResponseType(typeof(ExportStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(Guid exportId, CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        var status = await exports.GetStatusAsync(access.CompanyId, access.Subject, exportId, cancellationToken);
        return status is null ? ExportNotFound(exportId) : Ok(status);
    }

    [HttpGet("{exportId:guid}/file")]
    [Authorize(Policy = RolePolicies.RequireReportReader)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<IActionResult> Download(Guid exportId, CancellationToken cancellationToken)
    {
        if (CallerScopes.Resolve(caller) is not { } access)
        {
            return CallerScopes.TenantMissing();
        }

        var file = await exports.GetFileAsync(access.CompanyId, access.Subject, exportId, cancellationToken);

        switch (file.Status)
        {
            case ExportFileStatus.Ready:
                Response.Headers.CacheControl = "no-store";
                return File(file.Content!, file.ContentType!, ExportRequests.SafeFileName(file.FileName));
            case ExportFileStatus.Gone:
                return CallerScopes.Problem(StatusCodes.Status410Gone, "Export expired",
                    "This export has expired and its file was removed. Export the report again.");
            case ExportFileStatus.NotReady:
                return CallerScopes.Problem(StatusCodes.Status409Conflict, "Export not ready",
                    "The file is not ready. Check the export status.");
            default:
                return ExportNotFound(exportId);
        }
    }

    private static ObjectResult ExportNotFound(Guid exportId) =>
        CallerScopes.Problem(StatusCodes.Status404NotFound, "Export not found", $"No export {exportId} is visible to the caller.");
}
