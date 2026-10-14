using System.Net;
using Sellora.AuditService.Application.Dependencies;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Infrastructure.Dependencies;

/// <summary>
/// sellora-organization. GET /api/me/scope is [Authorize], so the caller's
/// bearer token is forwarded by <see cref="ForwardBearerTokenHandler"/>.
/// </summary>
public sealed class OrganizationClient(HttpClient http) : IOrganizationClient
{
    public async Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/me/scope");
        using var response = await DependencyHttp.SendAsync(http, request, Dependency.Organization, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DependencyRejectedException(
                Dependency.Organization, response.StatusCode, await DependencyHttp.ReadErrorAsync(response, cancellationToken));
        }

        var scope = await DependencyHttp.ReadAsync<CallerScopeDto>(response, Dependency.Organization, cancellationToken);

        return new CallerScope(scope.SalesRepId, scope.AgencyId, scope.ShopId, scope.ProvinceIds ?? new List<Guid>());
    }

    private sealed record CallerScopeDto(Guid? SalesRepId, Guid? AgencyId, Guid? ShopId, List<Guid>? ProvinceIds);
}
