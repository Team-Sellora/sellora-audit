using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Infrastructure.Audit;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Integration;

/// <summary>
/// US-E7-1 AC3 and AC4: search by order returns that order's events in time
/// order and never another company's; tampering by an elevated user is found
/// at the right entry.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class AuditReadAndVerifyTests(PostgreSqlFixture fixture)
{
    private static readonly Guid ProvinceA = Guid.NewGuid();
    private static readonly Guid ProvinceB = Guid.NewGuid();

    [Fact]
    public async Task Searching_by_order_reference_returns_every_event_of_that_order_in_time_order()
    {
        var (companyId, reference, _) = await SeedOrderAsync();
        await using var harness = new AuditHarness(fixture, companyId);

        var page = await Reads(harness).SearchAsync(WholeCompany(companyId), new AuditSearchQuery(OrderReference: reference), CancellationToken.None);

        Assert.Equal(new[] { "OrderPlaced", "OrderConfirmed", "PaymentRecorded" }, page.Items.Select(item => item.EventType));
        Assert.All(page.Items, item => Assert.Equal(reference, item.OrderReference));
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task Another_company_never_sees_the_entries_even_with_the_same_reference()
    {
        var (_, reference, entryId) = await SeedOrderAsync();
        var otherCompany = Guid.NewGuid();
        await using var harness = new AuditHarness(fixture, otherCompany);

        var page = await Reads(harness).SearchAsync(WholeCompany(otherCompany), new AuditSearchQuery(OrderReference: reference), CancellationToken.None);
        var detail = await Reads(harness).GetAsync(WholeCompany(otherCompany), entryId, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Null(detail);
    }

    [Fact]
    public async Task An_area_manager_sees_only_their_provinces_and_never_entries_without_a_province()
    {
        var companyId = Guid.NewGuid();
        await using var harness = new AuditHarness(fixture, companyId);
        await harness.IngestAllAsync(new[]
        {
            EventBuilder.Order(companyId, "OrderPlaced", Guid.NewGuid(), "ORD-PROV-A", EventBuilder.Party(ProvinceA, "A", "Rep A"), DateTimeOffset.UtcNow, 10m, "Placed"),
            EventBuilder.Order(companyId, "OrderPlaced", Guid.NewGuid(), "ORD-PROV-B", EventBuilder.Party(ProvinceB, "B", "Rep B"), DateTimeOffset.UtcNow, 10m, "Placed"),
            EventBuilder.Raw("sellora.catalog.v1", $$"""{"companyId":"{{companyId}}","productId":"{{Guid.NewGuid()}}"}""")
        });

        var areaManager = await Reads(harness).SearchAsync(new AuditReadScope(companyId, new[] { ProvinceA }), new AuditSearchQuery(), CancellationToken.None);
        var nobody = await Reads(harness).SearchAsync(new AuditReadScope(companyId, Array.Empty<Guid>()), new AuditSearchQuery(), CancellationToken.None);
        var admin = await Reads(harness).SearchAsync(WholeCompany(companyId), new AuditSearchQuery(), CancellationToken.None);

        Assert.Equal("ORD-PROV-A", Assert.Single(areaManager.Items).OrderReference);
        Assert.Empty(nobody.Items);
        Assert.Equal(3, admin.TotalCount);
    }

    [Fact]
    public async Task Each_filter_narrows_the_search_and_the_unfiltered_list_is_newest_first()
    {
        var (companyId, reference, _) = await SeedOrderAsync();
        await using var harness = new AuditHarness(fixture, companyId);
        var scope = WholeCompany(companyId);

        var byType = await Reads(harness).SearchAsync(scope, new AuditSearchQuery(EventType: "PaymentRecorded"), CancellationToken.None);
        var byAggregate = await Reads(harness).SearchAsync(scope, new AuditSearchQuery(AggregateType: "Order"), CancellationToken.None);
        var byActor = await Reads(harness).SearchAsync(scope, new AuditSearchQuery(ActorUserId: "nobody"), CancellationToken.None);
        var future = await Reads(harness).SearchAsync(scope, new AuditSearchQuery(From: DateTimeOffset.UtcNow.AddDays(1)), CancellationToken.None);
        var newestFirst = await Reads(harness).SearchAsync(scope, new AuditSearchQuery(), CancellationToken.None);
        var paged = await Reads(harness).SearchAsync(scope, new AuditSearchQuery(Page: 2, PageSize: 2), CancellationToken.None);

        Assert.Single(byType.Items);
        Assert.Equal(3, byAggregate.TotalCount);
        Assert.Empty(byActor.Items);
        Assert.Empty(future.Items);
        Assert.Equal("PaymentRecorded", newestFirst.Items[0].EventType);
        Assert.Single(paged.Items);
        Assert.Equal(reference, paged.Items[0].OrderReference);
    }

    [Fact]
    public async Task The_detail_returns_the_full_payload_and_both_hashes()
    {
        var (companyId, _, entryId) = await SeedOrderAsync();
        await using var harness = new AuditHarness(fixture, companyId);

        var detail = await Reads(harness).GetAsync(WholeCompany(companyId), entryId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Contains("\"payment\"", detail!.Payload);
        Assert.Equal(64, detail.Hash.Length);
        Assert.Equal(64, detail.PreviousHash.Length);
    }

    [Fact]
    public async Task A_payload_changed_by_the_owner_at_position_5_is_a_hash_mismatch_there()
    {
        var companyId = await SeedChainAsync(10);
        await fixture.ExecuteAsOwnerAsync(
            $"UPDATE audit.audit_entry SET payload = '{{\"tampered\":true}}' WHERE company_id = '{companyId}' AND chain_position = 5");
        await using var harness = new AuditHarness(fixture, companyId);

        var result = await harness.Verification.VerifyAsync(companyId, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(5, result.FirstBroken!.ChainPosition);
        Assert.Equal(nameof(ChainBreakReason.HashMismatch), result.FirstBroken.Reason);
        Assert.Equal("OrderPlaced", result.FirstBroken.EventType);
        Assert.NotNull(result.FirstBroken.AuditEntryId);
        Assert.Equal(5, result.EntriesChecked);
    }

    [Fact]
    public async Task A_row_deleted_by_the_owner_at_position_8_is_a_missing_position()
    {
        var companyId = await SeedChainAsync(10);
        await fixture.ExecuteAsOwnerAsync($"DELETE FROM audit.audit_entry WHERE company_id = '{companyId}' AND chain_position = 8");
        await using var harness = new AuditHarness(fixture, companyId);

        var result = await harness.Verification.VerifyAsync(companyId, CancellationToken.None);

        Assert.Equal(new ChainBreakResponse(null, 8, null, nameof(ChainBreakReason.MissingPosition)), result.FirstBroken);
        Assert.Equal(7, result.EntriesChecked);
    }

    [Fact]
    public async Task A_forged_hash_column_is_a_hash_mismatch_at_that_entry()
    {
        var companyId = await SeedChainAsync(4);
        await fixture.ExecuteAsOwnerAsync(
            $"UPDATE audit.audit_entry SET hash = repeat('a', 64) WHERE company_id = '{companyId}' AND chain_position = 2");
        await using var harness = new AuditHarness(fixture, companyId);

        var result = await harness.Verification.VerifyAsync(companyId, CancellationToken.None);

        Assert.Equal(2, result.FirstBroken!.ChainPosition);
        Assert.Equal(nameof(ChainBreakReason.HashMismatch), result.FirstBroken.Reason);
    }

    [Fact]
    public async Task A_long_chain_verifies_across_several_batches()
    {
        var companyId = Guid.NewGuid();
        var previous = AuditHasher.GenesisHash;

        await using (var owner = fixture.CreateOwnerContext(new TenantStub(companyId)))
        {
            for (var position = 1; position <= 2_500; position++)
            {
                var entry = AuditEntry.Append(Content(companyId, position), position, previous, DateTimeOffset.UtcNow);
                previous = entry.Hash;
                owner.AuditEntries.Add(entry);
            }

            await owner.SaveChangesAsync();
        }

        await using var harness = new AuditHarness(fixture, companyId);
        var result = await harness.Verification.VerifyAsync(companyId, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(2_500, result.EntriesChecked);
    }

    [Fact]
    public async Task Verifying_another_company_is_unaffected_by_tampering_elsewhere()
    {
        var tampered = await SeedChainAsync(3);
        var healthy = await SeedChainAsync(3);
        await fixture.ExecuteAsOwnerAsync($"UPDATE audit.audit_entry SET event_type = 'Forged' WHERE company_id = '{tampered}' AND chain_position = 1");
        await using var harness = new AuditHarness(fixture, healthy);

        Assert.True((await harness.Verification.VerifyAsync(healthy, CancellationToken.None)).Verified);
    }

    private async Task<(Guid CompanyId, string Reference, Guid PaymentEntryId)> SeedOrderAsync()
    {
        var companyId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var reference = $"ORD-{Guid.NewGuid():N}"[..20];
        var party = EventBuilder.Party(ProvinceA, "Agency", "Rep");
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);

        await using var harness = new AuditHarness(fixture, companyId);
        await harness.IngestAllAsync(new[]
        {
            EventBuilder.Order(companyId, "OrderPlaced", orderId, reference, party, start, 500m, "Placed", fulfilmentType: "ImmediateCashSale"),
            EventBuilder.Order(companyId, "OrderConfirmed", orderId, reference, party, start.AddMinutes(1), 500m, "Confirmed", fulfilmentType: "ImmediateCashSale"),
            EventBuilder.Order(companyId, "PaymentRecorded", orderId, reference, party, start.AddMinutes(2), 500m, "Confirmed", paymentAmount: 500m, fulfilmentType: "ImmediateCashSale")
        });

        var payment = await Reads(harness).SearchAsync(WholeCompany(companyId), new AuditSearchQuery(EventType: "PaymentRecorded"), CancellationToken.None);
        return (companyId, reference, payment.Items.Single().AuditEntryId);
    }

    private async Task<Guid> SeedChainAsync(int length)
    {
        var companyId = Guid.NewGuid();
        await using var harness = new AuditHarness(fixture);

        for (var position = 1; position <= length; position++)
        {
            await harness.Store.AppendAsync(Content(companyId, position), CancellationToken.None);
        }

        return companyId;
    }

    private static AuditEventContent Content(Guid companyId, int n) => new(
        companyId, Guid.NewGuid(), "OrderPlaced", "1.0", "sellora.order.v1", 0, n, "Order", Guid.NewGuid(),
        $"ORD-CHAIN-{n}", null, "rep-1", "SalesRep", DateTimeOffset.UtcNow, "c-1", $"{{\"n\":{n}}}");

    private static AuditReadScope WholeCompany(Guid companyId) => new(companyId, null);

    private static AuditReadService Reads(AuditHarness harness) => new(harness.Db);
}
