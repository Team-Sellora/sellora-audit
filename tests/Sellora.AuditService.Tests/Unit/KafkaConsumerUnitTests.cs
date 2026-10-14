using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Infrastructure.Kafka;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Tests.Unit;

/// <summary>US-E7-1-BE4: the consumer's settings, record mapping and dead-letter message, without a broker.</summary>
public sealed class KafkaConsumerUnitTests
{
    [Fact]
    public void The_default_settings_match_the_confluent_guide()
    {
        var options = new AuditKafkaOptions();

        options.Validate();

        Assert.Equal("sellora.audit.v1", options.ConsumerGroupId);
        Assert.Equal("sellora.audit.dead-letter.v1", options.DeadLetterTopic);
        Assert.Equal("Earliest", options.AutoOffsetReset);
        Assert.Equal(AuditKafkaOptions.DefaultTopics, options.SourceTopics);
    }

    [Fact]
    public void The_shipped_appsettings_bind_to_the_five_topics_once_each()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
        var options = configuration.GetSection(AuditKafkaOptions.SectionName).Get<AuditKafkaOptions>()!;

        options.Validate();

        Assert.Equal(AuditKafkaOptions.DefaultTopics, options.SourceTopics);
        Assert.Equal("sellora.audit.v1", options.ConsumerGroupId);
    }

    [Fact]
    public void Configured_topics_replace_the_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kafka:Topics:0"] = "sellora.order.v1" })
            .Build();

        var options = configuration.GetSection(AuditKafkaOptions.SectionName).Get<AuditKafkaOptions>()!;

        Assert.Equal(new[] { "sellora.order.v1" }, options.SourceTopics);
    }

    [Fact]
    public void The_dead_letter_topic_may_not_also_be_a_source_topic()
    {
        var options = new AuditKafkaOptions { Topics = new[] { "sellora.order.v1", "sellora.audit.dead-letter.v1" } };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Duplicate_source_topics_are_refused()
    {
        var options = new AuditKafkaOptions { Topics = new[] { "sellora.order.v1", "sellora.order.v1" } };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Sasl_is_applied_only_when_a_username_is_configured()
    {
        var local = new ConsumerConfig();
        var cloud = new ConsumerConfig();

        KafkaSaslConfigurator.Apply(local, "", "");
        KafkaSaslConfigurator.Apply(cloud, "key", "secret");

        Assert.Null(local.SecurityProtocol);
        Assert.Equal(SecurityProtocol.SaslSsl, cloud.SecurityProtocol);
        Assert.Equal(SaslMechanism.Plain, cloud.SaslMechanism);
    }

    [Fact]
    public void A_dead_lettered_record_keeps_its_original_bytes_and_says_where_it_came_from()
    {
        var record = Record(Encoding.UTF8.GetBytes("not-json"));

        var message = AuditEventConsumerService.BuildDeadLetterMessage(record, "NotJson: bad", new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("not-json", Encoding.UTF8.GetString(message.Value!));
        Assert.Equal("ORD-1", Encoding.UTF8.GetString(message.Key!));
        Assert.Equal("OrderPlaced", Encoding.UTF8.GetString(message.Headers.GetLastBytes("event-type")));
        Assert.Equal("NotJson: bad", Encoding.UTF8.GetString(message.Headers.GetLastBytes(AuditEventConsumerService.ReasonHeader)));
        Assert.Equal("sellora.order.v1", Encoding.UTF8.GetString(message.Headers.GetLastBytes(AuditEventConsumerService.SourceTopicHeader)));
        Assert.Equal("2", Encoding.UTF8.GetString(message.Headers.GetLastBytes(AuditEventConsumerService.SourcePartitionHeader)));
        Assert.Equal("77", Encoding.UTF8.GetString(message.Headers.GetLastBytes(AuditEventConsumerService.SourceOffsetHeader)));
    }

    [Fact]
    public void A_kafka_record_maps_to_an_incoming_message()
    {
        var incoming = AuditEventConsumerService.ToIncomingMessage(Record(Encoding.UTF8.GetBytes("{}")));

        Assert.Equal("sellora.order.v1", incoming.Topic);
        Assert.Equal(2, incoming.Partition);
        Assert.Equal(77, incoming.Offset);
        Assert.Equal("ORD-1", incoming.Key);
        Assert.Equal("event-type", Assert.Single(incoming.Headers).Key);
    }

    [Fact]
    public void A_record_without_a_kafka_timestamp_maps_to_now()
    {
        var record = Record(Encoding.UTF8.GetBytes("{}"));
        record.Message.Timestamp = Timestamp.Default;

        var incoming = AuditEventConsumerService.ToIncomingMessage(record);

        Assert.True(DateTimeOffset.UtcNow - incoming.Timestamp < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Only_data_errors_from_postgresql_count_as_permanent()
    {
        Assert.False(AuditDatabase.IsPermanentDataError(new TimeoutException()));
        Assert.False(AuditDatabase.IsPermanentDataError(new InvalidOperationException("connection lost")));
        Assert.True(AuditDatabase.IsPermanentDataError(Postgres("22021")));
        Assert.True(AuditDatabase.IsPermanentDataError(Postgres("54000")));
        Assert.False(AuditDatabase.IsPermanentDataError(Postgres("40P01")));
        Assert.False(AuditDatabase.IsPermanentDataError(Postgres("08006")));
    }

    [Fact]
    public void A_unique_violation_is_retried_not_dead_lettered()
    {
        Assert.True(AuditEventConsumerService.IsPoison(new InvalidOperationException("wrapped", Postgres("22P05"))));
        Assert.False(AuditEventConsumerService.IsPoison(Postgres("23505")));
    }

    [Fact]
    public async Task A_dead_letter_topic_that_cannot_be_reached_throws_so_the_source_offset_is_not_committed()
    {
        var settings = new AuditKafkaOptions { BootstrapServers = "localhost:1", MessageTimeoutMs = 1_000 };
        var services = new ServiceCollection()
            .AddScoped<IAuditStore, NeverCalledStore>()
            .AddScoped<IAuditMessageProcessor, AuditMessageProcessor>()
            .BuildServiceProvider();
        var consumer = new AuditEventConsumerService(
            Options.Create(settings), services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditEventConsumerService>.Instance);
        using var deadLetters = new ProducerBuilder<byte[]?, byte[]?>(new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            MessageTimeoutMs = settings.MessageTimeoutMs
        }).Build();

        await Assert.ThrowsAsync<ProduceException<byte[]?, byte[]?>>(() =>
            consumer.HandleAsync(Record(Encoding.UTF8.GetBytes("not-json")), deadLetters, settings, CancellationToken.None));
    }

    private static PostgresException Postgres(string sqlState) =>
        new("message", "ERROR", "ERROR", sqlState);

    private sealed class NeverCalledStore : IAuditStore
    {
        public Task<AppendOutcome> AppendAsync(AuditEventContent content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rejected record must not reach the store.");
    }

    private static ConsumeResult<byte[]?, byte[]?> Record(byte[] value) => new()
    {
        Topic = "sellora.order.v1",
        Partition = new Partition(2),
        Offset = new Offset(77),
        Message = new Message<byte[]?, byte[]?>
        {
            Key = Encoding.UTF8.GetBytes("ORD-1"),
            Value = value,
            Headers = new Headers { { "event-type", Encoding.UTF8.GetBytes("OrderPlaced") } },
            Timestamp = new Timestamp(new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc))
        }
    };
}
