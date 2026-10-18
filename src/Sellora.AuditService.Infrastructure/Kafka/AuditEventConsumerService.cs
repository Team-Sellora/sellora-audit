using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Infrastructure.Persistence;

namespace Sellora.AuditService.Infrastructure.Kafka;

/// <summary>
/// US-E7-1-BE4: one consumer group over the five source topics, with
/// sellora-order's Kafka client settings (KafkaSaslConfigurator, idempotent
/// acks=all producer) and manual offset commits:
///  - the offset is committed only after the database transaction commits,
///    or after the dead-letter topic has acknowledged a poison message;
///  - a poison message (not JSON, no company ID, data PostgreSQL refuses)
///    goes to sellora.audit.dead-letter.v1 so it cannot block the partition;
///  - anything else (database down, timeouts) leaves the record uncommitted,
///    and the consumer rejoins from the committed offset after a short delay.
/// Every event type is stored, including ones Audit does not understand.
/// </summary>
public sealed class AuditEventConsumerService(
    IOptions<AuditKafkaOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<AuditEventConsumerService> logger) : BackgroundService
{
    public const string ReasonHeader = "x-dead-letter-reason";
    public const string SourceTopicHeader = "x-source-topic";
    public const string SourcePartitionHeader = "x-source-partition";
    public const string SourceOffsetHeader = "x-source-offset";
    public const string DeadLetteredAtHeader = "x-dead-lettered-at";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The consumer loop blocks; yield first so the web host can finish starting.
        await Task.Yield();

        var settings = options.Value;
        settings.Validate();

        using var deadLetters = BuildDeadLetterProducer(settings);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var consumer = BuildConsumer(settings);

            try
            {
                var sourceTopics = settings.GetSourceTopics();
                consumer.Subscribe(sourceTopics);
                logger.LogInformation("AuditConsumerStarted: group {GroupId} on {Topics}",
                    settings.ConsumerGroupId, string.Join(", ", sourceTopics));

                while (!stoppingToken.IsCancellationRequested)
                {
                    ConsumeResult<byte[]?, byte[]?>? record;

                    try
                    {
                        record = consumer.Consume(stoppingToken);
                    }
                    catch (ConsumeException exception) when (IsMissingTopic(exception.Error))
                    {
                        // One source topic that does not exist yet must not stop the other four.
                        logger.LogWarning(exception, "AuditTopicMissing: {Reason}. The other topics are still consumed.", exception.Error.Reason);
                        continue;
                    }

                    if (record?.Message is null)
                    {
                        continue;
                    }

                    await HandleAsync(record, deadLetters, settings, stoppingToken);
                    consumer.Commit(record);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Audit event processing interrupted. Rejoining from committed offsets; the failed record stays uncommitted and will be retried.");
            }
            finally
            {
                CloseConsumer(consumer);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.RetryDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task HandleAsync(
        ConsumeResult<byte[]?, byte[]?> record,
        IProducer<byte[]?, byte[]?> deadLetters,
        AuditKafkaOptions settings,
        CancellationToken cancellationToken)
    {
        var message = ToIncomingMessage(record);
        ProcessingOutcome outcome;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IAuditMessageProcessor>();
            outcome = await processor.ProcessAsync(message, cancellationToken);
        }
        catch (Exception exception) when (IsPoison(exception))
        {
            outcome = new ProcessingOutcome(ProcessingStatus.Rejected, null, null, null,
                $"PostgreSQL refused the record: {exception.GetBaseException().Message}");
        }

        switch (outcome.Status)
        {
            case ProcessingStatus.Recorded:
                logger.LogInformation(
                    "AuditEventRecorded: {Topic}/{Partition}/{Offset} {EventType} company {CompanyId} at chain position {ChainPosition}",
                    record.Topic, record.Partition.Value, record.Offset.Value,
                    outcome.Content!.EventType, outcome.Content.CompanyId, outcome.ChainPosition);
                break;
            case ProcessingStatus.Duplicate:
                logger.LogInformation(
                    "AuditEventDuplicate: {Topic}/{Partition}/{Offset} {EventType} {EventId} is already recorded",
                    record.Topic, record.Partition.Value, record.Offset.Value,
                    outcome.Content!.EventType, outcome.Content.EventId);
                break;
            default:
                var reason = outcome.Rejection is { } rejection ? $"{rejection}: {outcome.Detail}" : outcome.Detail ?? "Rejected";

                // Acknowledged before the source offset is committed; if Kafka
                // fails here, the outer loop replays the source record.
                await deadLetters.ProduceAsync(settings.DeadLetterTopic, BuildDeadLetterMessage(record, reason, DateTimeOffset.UtcNow), cancellationToken);

                logger.LogWarning(
                    "AuditEventDeadLettered: {Topic}/{Partition}/{Offset} sent to {DeadLetterTopic}: {Reason}",
                    record.Topic, record.Partition.Value, record.Offset.Value, settings.DeadLetterTopic, reason);
                break;
        }
    }

    internal static bool IsMissingTopic(Error error) =>
        !error.IsFatal && error.Code is ErrorCode.UnknownTopicOrPart or ErrorCode.Local_UnknownTopic;

    /// <summary>
    /// A data error PostgreSQL will refuse on every retry. A unique-index
    /// violation is not one: it means a concurrent append won the race, and
    /// the retry then sees the duplicate or the new chain head.
    /// </summary>
    internal static bool IsPoison(Exception exception) =>
        AuditDatabase.IsPermanentDataError(exception) && !AuditDatabase.IsUniqueViolation(exception);

    internal static IncomingMessage ToIncomingMessage(ConsumeResult<byte[]?, byte[]?> record) => new(
        record.Topic,
        record.Partition.Value,
        record.Offset.Value,
        record.Message.Timestamp.Type == TimestampType.NotAvailable
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(record.Message.Timestamp.UtcDateTime, TimeSpan.Zero),
        record.Message.Key is { } key ? Encoding.UTF8.GetString(key) : null,
        record.Message.Headers?
            .Select(header => new KeyValuePair<string, byte[]?>(header.Key, header.GetValueBytes()))
            .ToList() ?? new List<KeyValuePair<string, byte[]?>>(),
        record.Message.Value);

    /// <summary>
    /// The original key, value and headers, unchanged so the record can be
    /// replayed, plus headers saying where it came from and why it failed.
    /// Documented in docs/contracts/audit-dead-letter.v1.md.
    /// </summary>
    internal static Message<byte[]?, byte[]?> BuildDeadLetterMessage(
        ConsumeResult<byte[]?, byte[]?> record,
        string reason,
        DateTimeOffset deadLetteredAt)
    {
        var headers = new Headers();

        foreach (var header in record.Message.Headers ?? new Headers())
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        headers.Add(ReasonHeader, Encoding.UTF8.GetBytes(reason.Length > 1000 ? reason[..1000] : reason));
        headers.Add(SourceTopicHeader, Encoding.UTF8.GetBytes(record.Topic));
        headers.Add(SourcePartitionHeader, Encoding.UTF8.GetBytes(record.Partition.Value.ToString(CultureInfo.InvariantCulture)));
        headers.Add(SourceOffsetHeader, Encoding.UTF8.GetBytes(record.Offset.Value.ToString(CultureInfo.InvariantCulture)));
        headers.Add(DeadLetteredAtHeader, Encoding.UTF8.GetBytes(deadLetteredAt.ToString("O", CultureInfo.InvariantCulture)));

        return new Message<byte[]?, byte[]?>
        {
            Key = record.Message.Key,
            Value = record.Message.Value,
            Headers = headers
        };
    }

    private IConsumer<byte[]?, byte[]?> BuildConsumer(AuditKafkaOptions settings)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            GroupId = settings.ConsumerGroupId,
            AutoOffsetReset = Enum.Parse<AutoOffsetReset>(settings.AutoOffsetReset, ignoreCase: true),
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            SessionTimeoutMs = settings.SessionTimeoutMs,
            ClientId = "sellora-audit"
        };

        KafkaSaslConfigurator.Apply(config, settings.SaslUsername, settings.SaslPassword);

        return new ConsumerBuilder<byte[]?, byte[]?>(config)
            .SetErrorHandler((_, error) => logger.LogWarning("Kafka consumer error {Code}: {Reason}", error.Code, error.Reason))
            .Build();
    }

    private static IProducer<byte[]?, byte[]?> BuildDeadLetterProducer(AuditKafkaOptions settings)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = settings.MessageTimeoutMs,
            ClientId = "sellora-audit"
        };

        KafkaSaslConfigurator.Apply(config, settings.SaslUsername, settings.SaslPassword);

        return new ProducerBuilder<byte[]?, byte[]?>(config).Build();
    }

    private void CloseConsumer(IConsumer<byte[]?, byte[]?> consumer)
    {
        try
        {
            consumer.Close();
        }
        catch (KafkaException exception)
        {
            logger.LogWarning(exception, "Kafka consumer could not close cleanly.");
        }
    }
}
