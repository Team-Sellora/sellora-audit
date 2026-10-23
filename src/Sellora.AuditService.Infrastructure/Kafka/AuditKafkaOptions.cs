namespace Sellora.AuditService.Infrastructure.Kafka;

/// <summary>Bound from the "Kafka" section; matches the Sprint 4 Confluent guide.</summary>
public sealed class AuditKafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = "localhost:9092";

    /// <summary>The five source topics (US-E7-1-BE4).</summary>
    public static readonly IReadOnlyList<string> DefaultTopics = new[]
    {
        "sellora.hierarchy.v1",
        "sellora.catalog.v1",
        "sellora.order.v1",
        "sellora.inventory.v1",
        "sellora.delivery.v1"
    };

    /// <summary>
    /// Empty by default on purpose: the configuration binder appends to an
    /// existing array, so a default here plus Kafka:Topics would subscribe
    /// to every topic twice.
    /// </summary>
    public string[] Topics { get; init; } = Array.Empty<string>();

    /// <summary>The configured topics, or <see cref="DefaultTopics"/> when none are configured.</summary>
    public IReadOnlyList<string> GetSourceTopics() =>
        Topics.Length == 0 ? DefaultTopics : Topics.Where(topic => !string.IsNullOrWhiteSpace(topic)).ToArray();

    public string DeadLetterTopic { get; init; } = "sellora.audit.dead-letter.v1";

    public string ConsumerGroupId { get; init; } = "sellora.audit.v1";

    /// <summary>Earliest, so the first deployment backfills whatever the topics still retain.</summary>
    public string AutoOffsetReset { get; init; } = "Earliest";

    public int MessageTimeoutMs { get; init; } = 10_000;

    /// <summary>Confluent Cloud's recommended session timeout.</summary>
    public int SessionTimeoutMs { get; init; } = 45_000;

    public int RetryDelaySeconds { get; init; } = 5;

    /// <summary>Confluent Cloud API key. Empty for a local broker.</summary>
    public string? SaslUsername { get; init; }

    /// <summary>Confluent Cloud API secret.</summary>
    public string? SaslPassword { get; init; }

    public void Validate()
    {
        var topics = GetSourceTopics();

        if (string.IsNullOrWhiteSpace(BootstrapServers) ||
            string.IsNullOrWhiteSpace(ConsumerGroupId) ||
            string.IsNullOrWhiteSpace(DeadLetterTopic) ||
            topics.Count == 0 ||
            topics.Distinct(StringComparer.Ordinal).Count() != topics.Count ||
            topics.Contains(DeadLetterTopic, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Kafka:BootstrapServers, Kafka:ConsumerGroupId, Kafka:DeadLetterTopic and distinct Kafka:Topics must be configured, and the dead-letter topic must not be a source topic.");
        }

        if (!Enum.TryParse<Confluent.Kafka.AutoOffsetReset>(AutoOffsetReset, ignoreCase: true, out _))
        {
            throw new InvalidOperationException($"Kafka:AutoOffsetReset '{AutoOffsetReset}' is not Earliest, Latest or Error.");
        }
    }
}
