using Testcontainers.Kafka;

namespace Sellora.AuditService.Tests.Support;

/// <summary>One real broker per test class, as sellora-order's Kafka tests use (Testcontainers, no local Kafka needed).</summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.6.1").Build();

    public string BootstrapServers => _kafka.GetBootstrapAddress();

    public Task InitializeAsync() => _kafka.StartAsync();

    public Task DisposeAsync() => _kafka.DisposeAsync().AsTask();
}
