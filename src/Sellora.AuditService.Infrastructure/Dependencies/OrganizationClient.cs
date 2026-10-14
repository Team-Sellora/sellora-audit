using System.Net;
using Sellora.AuditService.Application.Dependencies;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Infrastructure.Dependencies;

/// <summary>
/// sellora-organization. Both endpoints are [Authorize], so the caller's
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

    public async Task<HierarchyNames> GetHierarchyNamesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/hierarchy");
        using var response = await DependencyHttp.SendAsync(http, request, Dependency.Organization, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return HierarchyNames.Empty;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DependencyRejectedException(
                Dependency.Organization, response.StatusCode, await DependencyHttp.ReadErrorAsync(response, cancellationToken));
        }

        var tree = await DependencyHttp.ReadAsync<HierarchyTreeDto>(response, Dependency.Organization, cancellationToken);
        var territories = new Dictionary<Guid, string>();

        foreach (var province in tree.Provinces ?? new List<ProvinceDto>())
        {
            var assigned = (province.Agencies ?? new List<AgencyDto>()).SelectMany(agency => agency.Territories ?? new List<TerritoryDto>());

            foreach (var territory in assigned.Concat(province.UnassignedTerritories ?? new List<TerritoryDto>()))
            {
                territories[territory.TerritoryId] = territory.Name;
            }
        }

        return new HierarchyNames(territories);
    }

    private sealed record CallerScopeDto(Guid? SalesRepId, Guid? AgencyId, Guid? ShopId, List<Guid>? ProvinceIds);

    // sellora-organization: Application/Hierarchy/HierarchyTreeResponse.cs, names only.
    private sealed record HierarchyTreeDto(List<ProvinceDto>? Provinces);

    private sealed record ProvinceDto(List<AgencyDto>? Agencies, List<TerritoryDto>? UnassignedTerritories);

    private sealed record AgencyDto(List<TerritoryDto>? Territories);

    private sealed record TerritoryDto(Guid TerritoryId, string Name);
}
