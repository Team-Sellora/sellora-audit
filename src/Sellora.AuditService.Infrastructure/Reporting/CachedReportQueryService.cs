using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Sellora.AuditService.Application.Reporting;

namespace Sellora.AuditService.Infrastructure.Reporting;

public sealed class ReportingOptions
{
    public const string SectionName = "Reporting";

    public int CacheSeconds { get; set; } = 60;
}

/// <summary>US-E7-2-BE5: in-process cache of report responses (no Redis), per company, scope and query.</summary>
public sealed class ReportCache(IMemoryCache cache, IOptions<ReportingOptions> options)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _companies = new();

    public async Task<T> GetOrCreateAsync<T>(Guid companyId, string key, Func<Task<T>> factory)
        where T : class
    {
        var seconds = options.Value.CacheSeconds;
        var fullKey = $"report|{key}";

        if (seconds > 0 && cache.TryGetValue(fullKey, out T? cached) && cached is not null)
        {
            return cached;
        }

        var company = _companies.GetOrAdd(companyId, _ => new CancellationTokenSource());
        var value = await factory();

        if (seconds > 0)
        {
            cache.Set(fullKey, value, new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(seconds))
                .AddExpirationToken(new CancellationChangeToken(company.Token)));
        }

        return value;
    }

    public void Invalidate(Guid companyId)
    {
        if (_companies.TryRemove(companyId, out var company))
        {
            // Not disposed: a request may still be reading its token.
            company.Cancel();
        }
    }
}

public sealed class CachedReportQueryService(ReportQueryService inner, ReportCache cache) : IReportQueryService
{
    public Task<SalesSummaryResponse> SummaryAsync(ReportScope scope, SalesSummaryQuery query, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(scope.CompanyId, $"summary|{scope.Key()}|{query}", () => inner.SummaryAsync(scope, query, cancellationToken));

    public Task<SalesBreakdownResponse> BreakdownAsync(ReportScope scope, SalesBreakdownQuery query, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(scope.CompanyId, $"breakdown|{scope.Key()}|{query}", () => inner.BreakdownAsync(scope, query, cancellationToken));
}
