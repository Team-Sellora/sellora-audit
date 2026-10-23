using Microsoft.EntityFrameworkCore;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Integration;

/// <summary>US-E7-1-BE5: appending builds one gap-free, verifiable chain per company, once per event.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class AuditStoreTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Every_producer_fixture_is_recorded_and_the_chain_verifies()
    {
        var companyId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        await fixture.ExecuteAsOwnerAsync($"DELETE FROM audit.audit_entry WHERE company_id = '{companyId}'");
        await using var harness = new AuditHarness(fixture, companyId);

        foreach (var producerFixture in ProducerFixtures.All)
        {
            var outcome = await harness.IngestAsync(producerFixture.Message);
            Assert.Equal(ProcessingStatus.Recorded, outcome.Status);
        }

        var entries = await harness.Db.AuditEntries.OrderBy(entry => entry.ChainPosition).ToListAsync();
        Assert.Equal(Enumerable.Range(1, ProducerFixtures.All.Count).Select(position => (long)position), entries.Select(entry => entry.ChainPosition));
        Assert.Equal(5, entries.Select(entry => entry.SourceTopic).Distinct().Count());

        var verified = await harness.Verification.VerifyAsync(companyId, CancellationToken.None);
        Assert.True(verified.Verified);
        Assert.Equal(ProducerFixtures.All.Count, verified.EntriesChecked);
    }

    [Fact]
    public async Task The_stored_payload_and_timestamps_round_trip_exactly()
    {
        var companyId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        await using var harness = new AuditHarness(fixture, companyId);
        var payment = ProducerFixtures.Get("order/PaymentRecorded");
        await harness.IngestAsync(payment.Message);

        var stored = await harness.Db.AuditEntries.AsNoTracking().SingleAsync(entry => entry.EventId == payment.Expected.EventId);

        Assert.Equal(payment.Payload, stored.Payload);
        Assert.Equal(payment.Expected.OccurredAt, AuditHasher.FormatTimestamp(stored.OccurredAt));
        Assert.Equal(AuditHasher.Compute(stored.ToChainRecord()), stored.Hash);
    }

    [Fact]
    public async Task A_redelivered_event_is_recorded_once()
    {
        var companyId = Guid.NewGuid();
        await using var harness = new AuditHarness(fixture, companyId);
        var message = EventBuilder.Order(companyId, "OrderPlaced", Guid.NewGuid(), "ORD-REDELIVERED", Party(), DateTimeOffset.UtcNow, 100m, "Placed");

        var first = await harness.IngestAsync(message);
        var second = await harness.IngestAsync(message);

        Assert.Equal(ProcessingStatus.Recorded, first.Status);
        Assert.Equal(ProcessingStatus.Duplicate, second.Status);
        Assert.Equal(1, await harness.Db.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task The_same_event_id_in_two_companies_is_two_entries()
    {
        var eventId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var harness = new AuditHarness(fixture);

        Assert.Equal(AppendStatus.Recorded, (await harness.Store.AppendAsync(Content(first, eventId), CancellationToken.None)).Status);
        Assert.Equal(AppendStatus.Recorded, (await harness.Store.AppendAsync(Content(second, eventId), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Twenty_concurrent_appends_for_one_company_give_contiguous_positions_and_a_valid_chain()
    {
        var companyId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var harness = new AuditHarness(fixture);
            var outcome = await harness.Store.AppendAsync(Content(companyId, Guid.NewGuid()), CancellationToken.None);
            Assert.Equal(AppendStatus.Recorded, outcome.Status);
        }));

        await using var reader = new AuditHarness(fixture, companyId);
        var positions = await reader.Db.AuditEntries.Select(entry => entry.ChainPosition).OrderBy(position => position).ToListAsync();

        Assert.Equal(Enumerable.Range(1, 20).Select(position => (long)position), positions);
        Assert.True((await reader.Verification.VerifyAsync(companyId, CancellationToken.None)).Verified);
    }

    [Fact]
    public async Task Concurrent_appends_for_different_companies_build_independent_chains()
    {
        var companies = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(companies.SelectMany(company => Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var harness = new AuditHarness(fixture);
            await harness.Store.AppendAsync(Content(company, Guid.NewGuid()), CancellationToken.None);
        })));

        foreach (var company in companies)
        {
            await using var reader = new AuditHarness(fixture, company);
            var verified = await reader.Verification.VerifyAsync(company, CancellationToken.None);
            Assert.True(verified.Verified);
            Assert.Equal(5, verified.EntriesChecked);
        }
    }

    [Fact]
    public async Task A_new_context_continues_the_chain_from_the_stored_last_hash()
    {
        var companyId = Guid.NewGuid();

        await using (var first = new AuditHarness(fixture))
        {
            await first.Store.AppendAsync(Content(companyId, Guid.NewGuid()), CancellationToken.None);
        }

        await using var second = new AuditHarness(fixture, companyId);
        var outcome = await second.Store.AppendAsync(Content(companyId, Guid.NewGuid()), CancellationToken.None);
        var entries = await second.Db.AuditEntries.OrderBy(entry => entry.ChainPosition).ToListAsync();

        Assert.Equal(2, outcome.ChainPosition);
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
    }

    [Fact]
    public async Task A_duplicate_is_detected_under_concurrency_too()
    {
        var companyId = Guid.NewGuid();
        var content = Content(companyId, Guid.NewGuid());

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var harness = new AuditHarness(fixture);
            return (await harness.Store.AppendAsync(content, CancellationToken.None)).Status;
        }));

        Assert.Equal(1, outcomes.Count(status => status == AppendStatus.Recorded));
        Assert.Equal(9, outcomes.Count(status => status == AppendStatus.Duplicate));
    }

    [Fact]
    public async Task A_failed_append_leaves_nothing_behind_and_the_retry_records_it()
    {
        var companyId = Guid.NewGuid();
        await using var harness = new AuditHarness(fixture);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Store.AppendAsync(Content(companyId, Guid.NewGuid()), cancelled.Token));
        var retried = await harness.Store.AppendAsync(Content(companyId, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(1, retried.ChainPosition);
    }

    private static AuditEventContent Content(Guid companyId, Guid eventId) => new(
        companyId, eventId, "OrderPlaced", "1.0", "sellora.order.v1", 0, 1, "Order", Guid.NewGuid(),
        "ORD-STORE", null, "rep-1", "SalesRep", DateTimeOffset.UtcNow, "c-1", "{\"n\":1}");

    private static SaleParty Party() => EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep");
}
