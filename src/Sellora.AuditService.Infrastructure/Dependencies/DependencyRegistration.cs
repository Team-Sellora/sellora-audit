using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Sellora.AuditService.Application.Dependencies;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Infrastructure.Dependencies;

public sealed class DependencyOptions
{
    public const string Section = "Dependencies";

    public EndpointOptions Organization { get; set; } = new();

    public CircuitBreakerSettings CircuitBreaker { get; set; } = new();
}

public sealed class EndpointOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public double TimeoutSeconds { get; set; } = 2;
}

public sealed class CircuitBreakerSettings
{
    public double FailureRatio { get; set; } = 0.5;

    public int MinimumThroughput { get; set; } = 5;

    public double SamplingDurationSeconds { get; set; } = 30;

    public double BreakDurationSeconds { get; set; } = 30;
}

/// <summary>Copied from sellora-order, the Organization client only (US-E7-1-D3).</summary>
public sealed class ForwardBearerTokenHandler(IAccessTokenAccessor tokens) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = tokens.GetBearerToken();

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class ForwardCorrelationIdHandler(ICorrelationIdAccessor correlation) : DelegatingHandler
{
    public const string HeaderName = "X-Correlation-ID";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, correlation.GetCorrelationId());

        return base.SendAsync(request, cancellationToken);
    }
}

public static class DependencyRegistration
{
    private const char PathSeparator = '/';

    /// <summary>Nothing here contacts Organization, so the app starts healthy when it is unreachable.</summary>
    public static IServiceCollection AddAuditDependencies(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DependencyOptions.Section).Get<DependencyOptions>() ?? new DependencyOptions();

        services.AddTransient<ForwardBearerTokenHandler>();
        services.AddTransient<ForwardCorrelationIdHandler>();

        var organization = services.AddHttpClient<IOrganizationClient, OrganizationClient>(client =>
            client.BaseAddress = BaseAddress(options.Organization, "Organization"));

        organization.AddResilienceHandler("organization-pipeline", pipeline =>
        {
            // Outer: the breaker counts timeouts, network errors and 5xx, then fails fast while open.
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = options.CircuitBreaker.FailureRatio,
                MinimumThroughput = options.CircuitBreaker.MinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(Math.Max(
                    options.CircuitBreaker.SamplingDurationSeconds, options.Organization.TimeoutSeconds * 2)),
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreaker.BreakDurationSeconds)
            });

            // Inner: each attempt gets its own time budget.
            pipeline.AddTimeout(TimeSpan.FromSeconds(options.Organization.TimeoutSeconds));
        });

        organization
            .AddHttpMessageHandler<ForwardBearerTokenHandler>()
            .AddHttpMessageHandler<ForwardCorrelationIdHandler>();

        return services;
    }

    private static Uri BaseAddress(EndpointOptions endpoint, string name)
    {
        if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Dependencies:{name}:BaseUrl must be an absolute URL (got '{endpoint.BaseUrl}').");
        }

        return uri.AbsoluteUri.EndsWith(PathSeparator) ? uri : new Uri(uri.AbsoluteUri + PathSeparator);
    }
}
