using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests;

public class HealthEndpointTests : IClassFixture<AuditApiFactory>
{
    private readonly AuditApiFactory _factory;

    public HealthEndpointTests(AuditApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_health_returns_200_with_healthy_status()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Healthy", body?.Status);
    }

    [Fact]
    public async Task The_host_stays_healthy_with_the_consumer_running_and_kafka_unreachable()
    {
        await using var factory = new ConsumerRunningApi();
        using var client = factory.CreateClient();

        // Long enough for the consumer to bind its options and try the broker.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record HealthResponse(string Status);

    /// <summary>The shipped appsettings.json in a non-test environment, so the Kafka consumer starts.</summary>
    private sealed class ConsumerRunningApi : AuditApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Container");
            builder.UseSetting("Kafka:BootstrapServers", "localhost:1");
        }
    }
}
