using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Infrastructure.Persistence;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Api;

/// <summary>
/// US-E7-1-BE6, the API and code layers: the routes the running host maps
/// are read only, the store can only append, and the DbSet is not exposed.
/// The routes come from the host's own endpoint table, so controllers and
/// minimal APIs are both covered.
/// </summary>
public sealed class AuditRouteImmutabilityTests : IClassFixture<AuditApiFactory>
{
    private readonly AuditApiFactory _api;

    public AuditRouteImmutabilityTests(AuditApiFactory api) => _api = api;

    [Fact]
    public void The_host_maps_the_three_audit_routes()
    {
        var templates = Routes().Select(route => route.Template).ToList();

        Assert.Contains("api/audit", templates);
        Assert.Contains("api/audit/{auditEntryId:guid}", templates);
        Assert.Contains("api/audit/verify", templates);
    }

    [Fact]
    public void Every_route_under_api_audit_is_a_get()
    {
        var offenders = Routes()
            .Where(route => route.Template.StartsWith("api/audit", StringComparison.OrdinalIgnoreCase))
            .Where(route => route.Verbs.Count == 0 || route.Verbs.Any(verb => verb != "GET"))
            .Select(route => $"{route.Name}: {string.Join(',', route.Verbs)} {route.Template}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_only_non_get_routes_are_the_projection_rebuild_and_the_export_submission()
    {
        // US-E7-2-BE6 rebuilds projections and US-E7-3-BE1 queues exports; nothing else may write.
        var allowed = new[] { "api/reports/projections/rebuild", "api/reports/export" };

        var offenders = Routes()
            .Where(route => route.Verbs.Any(verb => verb is not ("GET" or "HEAD")))
            .Where(route => !(allowed.Contains(route.Template, StringComparer.OrdinalIgnoreCase) && route.Verbs.SequenceEqual(new[] { "POST" })))
            .Select(route => $"{route.Name}: {string.Join(',', route.Verbs)} {route.Template}")
            .ToList();

        Assert.Empty(offenders);
        Assert.All(allowed, template =>
            Assert.Contains(Routes(), route => route.Template == template && route.Verbs.SequenceEqual(new[] { "POST" })));
    }

    [Fact]
    public void The_host_maps_the_export_routes()
    {
        var templates = Routes().Select(route => route.Template).ToList();

        Assert.Contains("api/reports/export", templates);
        Assert.Contains("api/reports/export/{exportId:guid}", templates);
        Assert.Contains("api/reports/export/{exportId:guid}/file", templates);
    }

    [Fact]
    public void No_endpoint_maps_put_patch_or_delete_anywhere()
    {
        // An endpoint without verb metadata would answer PUT, PATCH and DELETE too.
        var offenders = Routes()
            .Where(route => route.Verbs.Count == 0 || route.Verbs.Any(verb => verb is "PUT" or "PATCH" or "DELETE"))
            .Select(route => $"{route.Name}: {route.Template}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_audit_store_exposes_append_only()
    {
        var members = typeof(IAuditStore).GetMethods().Select(method => method.Name).ToList();

        Assert.Equal(new[] { nameof(IAuditStore.AppendAsync) }, members);
    }

    [Fact]
    public void The_audit_entries_set_is_not_public()
    {
        var property = typeof(AuditDbContext).GetProperty("AuditEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        Assert.False(property.GetMethod!.IsPublic);
    }

    [Fact]
    public void The_audit_entry_has_no_public_setters_or_public_constructor()
    {
        var setters = typeof(AuditEntry).GetProperties()
            .Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(setters);
        Assert.Empty(typeof(AuditEntry).GetConstructors());
    }

    private sealed record Route(string Name, string Template, IReadOnlyList<string> Verbs);

    private IEnumerable<Route> Routes() =>
        _api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new Route(
                endpoint.DisplayName ?? endpoint.RoutePattern.RawText ?? string.Empty,
                endpoint.RoutePattern.RawText?.Trim('/') ?? string.Empty,
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                    .Select(verb => verb.ToUpperInvariant()).ToList() ?? new List<string>()));
}
