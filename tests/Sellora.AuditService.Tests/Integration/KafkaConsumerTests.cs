using System.Collections.Concurrent;
using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Domain.Reporting;
using Sellora.AuditService.Domain.Tenancy;
using Sellora.AuditService.Infrastructure.Ingestion;
using Sellora.AuditService.Infrastructure.Kafka;
using Sellora.AuditService.Infrastructure.Reporting;
using Sellora.AuditService.Tests.Support;
using Xunit.Abstractions;

namespace Sellora.AuditService.Tests.Integration;

/// <summary>
/// US-E7-1-BE4 against a real broker and a real database: records from all
/// five topics are stored, a non-JSON record goes to the dead-letter topic
/// and the next valid record is still stored (Q8), a failure before or
/// after the database commit replays the record without a duplicate, and
/// a consumer reset to the earliest offset changes nothing (Q5).
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class KafkaConsumerTests(PostgreSqlFixture fixture, KafkaFixture kafka, ITestOutputHelper output) : IClassFixture<KafkaFixture>
{
    private static readonly Guid FixtureCompany = Guid.Parse("30000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Records_from_all_five_topics_are_stored_and_poison_is_dead_lettered()
    {
        await fixture.ExecuteAsOwnerAsync($"DELETE FROM audit.audit_entry WHERE company_id = '{FixtureCompany}'");
        await fixture.ExecuteAsOwnerAsync(
            $"DELETE FROM reporting.order_fact WHERE company_id = '{FixtureCompany}'; " +
            $"DELETE FROM reporting.agency_dimension WHERE company_id = '{FixtureCompany}'; " +
            $"DELETE FROM reporting.rep_dimension WHERE company_id = '{FixtureCompany}'; " +
            $"DELETE FROM reporting.territory_dimension WHERE company_id = '{FixtureCompany}'");
        var options = Options(deadLetterTopic: $"sellora.audit.dead-letter.test.{Guid.NewGuid():N}");
        using var producer = Producer();

        foreach (var producerFixture in ProducerFixtures.All)
        {
            await producer.ProduceAsync(producerFixture.Message.Topic, ToKafka(producerFixture.Message));
        }

        await producer.ProduceAsync("sellora.order.v1", new Message<byte[]?, byte[]?>
        {
            Value = Encoding.UTF8.GetBytes("not-json"),
            Headers = new Headers { { "event-type", Encoding.UTF8.GetBytes("OrderPlaced") } }
        });
        // The same record again (a redelivery) and one more valid record after the poison.
        await producer.ProduceAsync("sellora.order.v1", ToKafka(ProducerFixtures.Get("order/OrderPlaced").Message));
        var afterPoison = EventBuilder.Order(FixtureCompany, "OrderPlaced", Guid.NewGuid(), "ORD-AFTER-POISON",
            EventBuilder.Party(Guid.NewGuid(), "A", "R"), DateTimeOffset.UtcNow, 10m, "Placed");
        await producer.ProduceAsync("sellora.order.v1", ToKafka(afterPoison));

        var expected = ProducerFixtures.All.Count + 1;
        await RunUntilAsync(options, Services(), FixtureCompany, expected);

        await using var harness = new AuditHarness(fixture, FixtureCompany);
        Assert.Equal(expected, await harness.Db.AuditEntries.CountAsync());
        Assert.Equal(5, await harness.Db.AuditEntries.Select(entry => entry.SourceTopic).Distinct().CountAsync());
        Assert.True((await harness.Verification.VerifyAsync(FixtureCompany, CancellationToken.None)).Verified);

        // US-E7-2-BE1: the same consumer fills the projections.
        var fixtureOrder = await harness.Db.OrderFacts.SingleAsync(fact => fact.OrderReference == "ORD-260924-K7P2QM");
        Assert.Equal(OrderFactStatus.Cancelled, fixtureOrder.Status);
        Assert.Equal(1255.00m, fixtureOrder.PaidAmount);
        Assert.Equal(2, await harness.Db.OrderFacts.CountAsync());
        Assert.Equal("Colombo Distribution Agency", (await harness.Db.AgencyDimensions.SingleAsync(agency => agency.AgencyId == Guid.Parse("41000000-0000-0000-0000-000000000001"))).Name);
        Assert.Equal("Nimal Perera", (await harness.Db.RepDimensions.SingleAsync(rep => rep.SalesRepId == Guid.Parse("43000000-0000-0000-0000-000000000001"))).Name);

        var deadLetter = ReadOne(options.Value.DeadLetterTopic);
        Assert.Equal("not-json", Encoding.UTF8.GetString(deadLetter.Message.Value!));
        Assert.Equal("OrderPlaced", Header(deadLetter, "event-type"));
        Assert.Equal("sellora.order.v1", Header(deadLetter, AuditEventConsumerService.SourceTopicHeader));
        Assert.StartsWith("NotJson", Header(deadLetter, AuditEventConsumerService.ReasonHeader));
    }

    [Fact]
    public async Task A_failure_before_or_after_the_database_commit_replays_the_record_without_a_duplicate()
    {
        var company = Guid.NewGuid();
        var party = EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep");
        var messages = Enumerable.Range(1, 6)
            .Select(n => EventBuilder.Order(company, "OrderPlaced", Guid.NewGuid(), $"ORD-REPLAY-{n}", party, DateTimeOffset.UtcNow, n, "Placed"))
            .ToList();

        // Record 2: the database is down, nothing is stored. Record 4: stored, then the process dies before the offset commit.
        var flaky = new FlakyProcessor(
            failBefore: EventId(messages[1]),
            failAfter: EventId(messages[3]));
        var options = Options();

        using (var producer = Producer())
        {
            foreach (var message in messages)
            {
                await producer.ProduceAsync(message.Topic, ToKafka(message));
            }
        }

        await RunUntilAsync(options, Services(flaky), company, messages.Count);

        Assert.Equal(2, flaky.Attempts[EventId(messages[1])]);
        Assert.Equal(2, flaky.Attempts[EventId(messages[3])]);
        Assert.Equal(ProcessingStatus.Duplicate, flaky.LastStatus[EventId(messages[3])]);

        await using var harness = new AuditHarness(fixture, company);
        Assert.Equal(messages.Count, await harness.Db.AuditEntries.CountAsync());
        Assert.True((await harness.Verification.VerifyAsync(company, CancellationToken.None)).Verified);

        // Same group again: every offset was committed, so nothing is processed twice.
        var attemptsBefore = flaky.TotalAttempts;
        await RunForAsync(options, Services(flaky), TimeSpan.FromSeconds(10));
        Assert.Equal(attemptsBefore, flaky.TotalAttempts);

        // A new group from the earliest offset (the Q5 offset reset): every record is a duplicate.
        await RunForAsync(Options(), Services(), TimeSpan.FromSeconds(15));
        Assert.Equal(messages.Count, await harness.Db.AuditEntries.CountAsync());
        Assert.True((await harness.Verification.VerifyAsync(company, CancellationToken.None)).Verified);
    }

    [Fact]
    public async Task A_source_topic_that_does_not_exist_does_not_stop_the_others()
    {
        var company = Guid.NewGuid();
        var message = EventBuilder.Order(company, "OrderPlaced", Guid.NewGuid(), "ORD-MISSING-TOPIC",
            EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep"), DateTimeOffset.UtcNow, 1m, "Placed");

        using (var producer = Producer())
        {
            await producer.ProduceAsync(message.Topic, ToKafka(message));
        }

        var options = Microsoft.Extensions.Options.Options.Create(new AuditKafkaOptions
        {
            BootstrapServers = kafka.BootstrapServers,
            ConsumerGroupId = $"sellora.audit.test.{Guid.NewGuid():N}",
            Topics = new[] { "sellora.order.v1", $"sellora.not-created.{Guid.NewGuid():N}" },
            RetryDelaySeconds = 1
        });

        await RunUntilAsync(options, Services(), company, 1);

        await using var harness = new AuditHarness(fixture, company);
        Assert.Equal(1, await harness.Db.AuditEntries.CountAsync());
    }

    private IOptions<AuditKafkaOptions> Options(string? deadLetterTopic = null) =>
        Microsoft.Extensions.Options.Options.Create(new AuditKafkaOptions
        {
            BootstrapServers = kafka.BootstrapServers,
            ConsumerGroupId = $"sellora.audit.test.{Guid.NewGuid():N}",
            DeadLetterTopic = deadLetterTopic ?? new AuditKafkaOptions().DeadLetterTopic,
            RetryDelaySeconds = 1
        });

    private async Task RunUntilAsync(IOptions<AuditKafkaOptions> options, IServiceScopeFactory services, Guid company, int expected)
    {
        var consumer = new AuditEventConsumerService(options, services, new TestOutputLogger<AuditEventConsumerService>(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await consumer.StartAsync(timeout.Token);

        try
        {
            await using var harness = new AuditHarness(fixture, company);

            while (await harness.Db.AuditEntries.CountAsync() < expected)
            {
                await Task.Delay(250, timeout.Token);
            }

            // Let the consumer commit the last offset before it stops.
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    private async Task RunForAsync(IOptions<AuditKafkaOptions> options, IServiceScopeFactory services, TimeSpan duration)
    {
        var consumer = new AuditEventConsumerService(options, services, new TestOutputLogger<AuditEventConsumerService>(output));
        await consumer.StartAsync(CancellationToken.None);
        await Task.Delay(duration);
        await consumer.StopAsync(CancellationToken.None);
    }

    private IProducer<byte[]?, byte[]?> Producer() =>
        new ProducerBuilder<byte[]?, byte[]?>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build();

    private ConsumeResult<byte[]?, byte[]?> ReadOne(string topic)
    {
        using var reader = new ConsumerBuilder<byte[]?, byte[]?>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = $"test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();

        reader.Subscribe(topic);
        var record = reader.Consume(TimeSpan.FromSeconds(30));
        Assert.NotNull(record);
        return record!;
    }

    private static Guid EventId(IncomingMessage message) =>
        AuditEnvelopeNormaliser.Normalise(message).Content!.EventId;

    private static string Header(ConsumeResult<byte[]?, byte[]?> record, string name) =>
        Encoding.UTF8.GetString(record.Message.Headers.GetLastBytes(name));

    private static Message<byte[]?, byte[]?> ToKafka(IncomingMessage message)
    {
        var headers = new Headers();

        foreach (var header in message.Headers)
        {
            headers.Add(header.Key, header.Value);
        }

        return new Message<byte[]?, byte[]?>
        {
            Key = message.Key is null ? null : Encoding.UTF8.GetBytes(message.Key),
            Value = message.Value,
            Headers = headers
        };
    }

    private IServiceScopeFactory Services(FlakyProcessor? flaky = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantStub>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantStub>());
        services.AddScoped<ISystemTenantContext>(provider => provider.GetRequiredService<TenantStub>());
        services.AddScoped(provider => fixture.CreateAppContext(provider.GetRequiredService<TenantStub>()));
        services.AddScoped<IReportingProjector, ReportingProjector>();
        services.AddScoped<IAuditStore, AuditStore>();
        services.AddScoped<AuditMessageProcessor>();

        if (flaky is null)
        {
            services.AddScoped<IAuditMessageProcessor>(provider => provider.GetRequiredService<AuditMessageProcessor>());
        }
        else
        {
            services.AddScoped<IAuditMessageProcessor>(provider => flaky.Wrap(provider.GetRequiredService<AuditMessageProcessor>()));
        }

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Fails the first attempt of two chosen events, once before and once after the real append.</summary>
    private sealed class FlakyProcessor(Guid failBefore, Guid failAfter)
    {
        public Guid FailBefore { get; } = failBefore;

        public Guid FailAfter { get; } = failAfter;

        public ConcurrentDictionary<Guid, int> Attempts { get; } = new();

        public ConcurrentDictionary<Guid, ProcessingStatus> LastStatus { get; } = new();

        public int TotalAttempts => Attempts.Values.Sum();

        public IAuditMessageProcessor Wrap(IAuditMessageProcessor inner) => new Wrapper(this, inner);

        private sealed class Wrapper(FlakyProcessor owner, IAuditMessageProcessor inner) : IAuditMessageProcessor
        {
            public async Task<ProcessingOutcome> ProcessAsync(IncomingMessage message, CancellationToken cancellationToken)
            {
                var eventId = EventId(message);
                var attempt = owner.Attempts.AddOrUpdate(eventId, 1, (_, count) => count + 1);

                if (eventId == owner.FailBefore && attempt == 1)
                {
                    throw new TimeoutException("Simulated database outage before the append.");
                }

                var outcome = await inner.ProcessAsync(message, cancellationToken);
                owner.LastStatus[eventId] = outcome.Status;

                if (eventId == owner.FailAfter && attempt == 1)
                {
                    throw new TimeoutException("Simulated crash after the database commit, before the offset commit.");
                }

                return outcome;
            }
        }
    }
}
