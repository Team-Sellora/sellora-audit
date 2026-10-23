using System.Text;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Unit;

/// <summary>
/// US-E7-1-BE3: one real-shaped message from each of the five producers,
/// whose headers and casing all differ, normalises to the expected envelope.
/// </summary>
public sealed class EnvelopeNormaliserTests
{
    private static readonly Guid Company = Guid.Parse("30000000-0000-0000-0000-000000000001");

    public static TheoryData<string> Fixtures() => ProducerFixtures.Names();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_producer_fixture_normalises_to_its_expected_envelope(string name)
    {
        var fixture = ProducerFixtures.Get(name);
        var expected = fixture.Expected;

        var result = AuditEnvelopeNormaliser.Normalise(fixture.Message);

        Assert.True(result.IsAccepted, result.Detail);
        var content = result.Content!;
        Assert.Equal(Company, content.CompanyId);
        Assert.Equal(expected.EventId, content.EventId);
        Assert.Equal(expected.EventType, content.EventType);
        Assert.Equal(expected.AggregateType, content.AggregateType);
        Assert.Equal(expected.AggregateId, content.AggregateId);
        Assert.Equal(expected.OrderReference, content.OrderReference);
        Assert.Equal(expected.ProvinceId, content.ProvinceId);
        Assert.Equal(expected.ActorUserId, content.ActorUserId);
        Assert.Equal(expected.ActorRole, content.ActorRole);
        Assert.Equal(expected.CorrelationId, content.CorrelationId);
        Assert.Equal(expected.OccurredAt, AuditHasher.FormatTimestamp(content.OccurredAt));
        Assert.Equal(fixture.Message.Topic, content.SourceTopic);
        Assert.Equal(fixture.Message.Offset, content.Offset);
    }

    [Fact]
    public void The_fixtures_cover_all_five_producers()
    {
        var producers = ProducerFixtures.All.Select(fixture => fixture.Producer).Distinct().OrderBy(producer => producer).ToList();

        Assert.Equal(
            new[] { "sellora-catalog", "sellora-delivery", "sellora-inventory", "sellora-order", "sellora-organization" },
            producers);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_payload_is_stored_exactly_as_received(string name)
    {
        var fixture = ProducerFixtures.Get(name);

        var content = AuditEnvelopeNormaliser.Normalise(fixture.Message).Content!;

        Assert.Equal(fixture.Payload, content.Payload);
    }

    [Fact]
    public void A_raw_16_byte_event_id_header_is_read_as_a_guid()
    {
        var organization = ProducerFixtures.Get("organization/AgencyRegistered");

        var content = AuditEnvelopeNormaliser.Normalise(organization.Message).Content!;

        // The header (the outbox ID) wins over the payload's own eventId.
        Assert.Equal(Guid.Parse("51000000-0000-0000-0000-000000000001"), content.EventId);
    }

    [Fact]
    public void Catalog_has_no_correlation_header_or_payload_field_so_it_is_null()
    {
        var content = AuditEnvelopeNormaliser.Normalise(ProducerFixtures.Get("catalog/PriceChanged").Message).Content!;

        Assert.Null(content.CorrelationId);
        Assert.Equal("PriceChanged", content.EventType);
    }

    [Fact]
    public void Inventory_without_headers_falls_back_to_the_pascal_case_payload()
    {
        var content = AuditEnvelopeNormaliser.Normalise(ProducerFixtures.Get("inventory/LowStockDetected").Message).Content!;

        Assert.Equal(Guid.Parse("71000000-0000-0000-0000-000000000001"), content.EventId);
        Assert.Equal("LowStockDetected", content.EventType);
        Assert.Equal("1.0", content.SchemaVersion);
    }

    [Fact]
    public void A_delivery_status_change_is_about_the_delivery_not_the_order()
    {
        var content = AuditEnvelopeNormaliser.Normalise(ProducerFixtures.Get("delivery/DeliveryStatusChanged").Message).Content!;

        Assert.Equal("Delivery", content.AggregateType);
        Assert.Equal(Guid.Parse("81000000-0000-0000-0000-000000000001"), content.AggregateId);
        Assert.Equal("ORD-260924-K7P2QM", content.OrderReference);
    }

    [Fact]
    public void A_system_cancellation_is_attributed_to_the_system()
    {
        var content = AuditEnvelopeNormaliser.Normalise(ProducerFixtures.Get("order/OrderCancelled").Message).Content!;

        Assert.Equal(AuditEnvelopeNormaliser.SystemActor, content.ActorUserId);
        Assert.Equal("System", content.ActorRole);
    }

    [Fact]
    public void An_unknown_event_type_is_still_recorded_with_the_topic_default()
    {
        var message = Message("sellora.order.v1", $$"""{"eventType":"SomethingNew","companyId":"{{Company}}","orderId":"{{Guid.Empty}}","entityId":"8c1e3d52-6a1f-4b7e-9d2c-0f4e5a6b7c8d"}""");

        var result = AuditEnvelopeNormaliser.Normalise(message);

        Assert.True(result.IsAccepted);
        Assert.Equal("SomethingNew", result.Content!.EventType);
        Assert.Equal("Order", result.Content.AggregateType);
        Assert.Equal(Guid.Parse("8c1e3d52-6a1f-4b7e-9d2c-0f4e5a6b7c8d"), result.Content.AggregateId);
    }

    [Fact]
    public void A_message_without_any_event_id_gets_a_stable_id_from_its_position()
    {
        var first = AuditEnvelopeNormaliser.Normalise(Message("sellora.catalog.v1", $$"""{"companyId":"{{Company}}"}""", offset: 5)).Content!;
        var redelivered = AuditEnvelopeNormaliser.Normalise(Message("sellora.catalog.v1", $$"""{"companyId":"{{Company}}"}""", offset: 5)).Content!;
        var next = AuditEnvelopeNormaliser.Normalise(Message("sellora.catalog.v1", $$"""{"companyId":"{{Company}}"}""", offset: 6)).Content!;

        Assert.Equal(first.EventId, redelivered.EventId);
        Assert.NotEqual(first.EventId, next.EventId);
        Assert.Equal(AuditEnvelopeNormaliser.UnknownEventType, first.EventType);
    }

    [Fact]
    public void The_company_header_is_used_when_the_payload_has_none()
    {
        var message = Message("sellora.catalog.v1", """{"productId":"45000000-0000-0000-0000-000000000001"}""",
            headers: new[] { Header("company-id", Company.ToString()) });

        Assert.Equal(Company, AuditEnvelopeNormaliser.Normalise(message).Content!.CompanyId);
    }

    [Theory]
    [InlineData("", RejectionReason.EmptyPayload)]
    [InlineData("not-json", RejectionReason.NotJson)]
    [InlineData("[1,2,3]", RejectionReason.NotJsonObject)]
    [InlineData("""{"eventType":"OrderPlaced"}""", RejectionReason.MissingCompanyId)]
    [InlineData("""{"companyId":"not-a-guid"}""", RejectionReason.MissingCompanyId)]
    public void Poison_messages_are_rejected_with_a_reason(string value, RejectionReason reason)
    {
        var result = AuditEnvelopeNormaliser.Normalise(Message("sellora.order.v1", value));

        Assert.False(result.IsAccepted);
        Assert.Equal(reason, result.Rejection);
    }

    [Fact]
    public void Invalid_utf8_is_rejected()
    {
        var message = new IncomingMessage("sellora.order.v1", 0, 1, DateTimeOffset.UtcNow, null,
            new List<KeyValuePair<string, byte[]?>>(), new byte[] { 0x7B, 0xC3, 0x28, 0x7D });

        Assert.Equal(RejectionReason.InvalidUtf8, AuditEnvelopeNormaliser.Normalise(message).Rejection);
    }

    [Fact]
    public void A_nul_character_is_rejected_because_postgresql_text_cannot_store_it()
    {
        var result = AuditEnvelopeNormaliser.Normalise(Message("sellora.order.v1", $"{{\"companyId\":\"{Company}\",\"x\":\"a\u0000b\"}}"));

        Assert.Equal(RejectionReason.ContainsNulCharacter, result.Rejection);
    }

    [Fact]
    public void A_header_and_payload_company_that_disagree_are_rejected()
    {
        var message = Message("sellora.order.v1", $$"""{"companyId":"{{Company}}"}""",
            headers: new[] { Header("company-id", Guid.NewGuid().ToString()) });

        Assert.Equal(RejectionReason.CompanyMismatch, AuditEnvelopeNormaliser.Normalise(message).Rejection);
    }

    [Fact]
    public void Long_derived_values_are_capped_but_the_payload_is_kept_whole()
    {
        var longType = new string('E', 500);
        var payload = $$"""{"companyId":"{{Company}}","eventType":"{{longType}}"}""";

        var content = AuditEnvelopeNormaliser.Normalise(Message("sellora.order.v1", payload)).Content!;

        Assert.Equal(AuditEnvelopeNormaliser.MaxFieldLength, content.EventType.Length);
        Assert.Equal(payload, content.Payload);
    }

    [Fact]
    public void The_occurred_time_is_truncated_to_whole_microseconds()
    {
        var content = AuditEnvelopeNormaliser.Normalise(ProducerFixtures.Get("organization/AgencyRegistered").Message).Content!;

        Assert.Equal(0, content.OccurredAt.UtcTicks % 10);
        Assert.Equal(TimeSpan.Zero, content.OccurredAt.Offset);
    }

    private static IncomingMessage Message(
        string topic,
        string value,
        long offset = 1,
        IEnumerable<KeyValuePair<string, byte[]?>>? headers = null) =>
        new(topic, 0, offset, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), null,
            (headers ?? Array.Empty<KeyValuePair<string, byte[]?>>()).ToList(), Encoding.UTF8.GetBytes(value));

    private static KeyValuePair<string, byte[]?> Header(string key, string value) => new(key, Encoding.UTF8.GetBytes(value));
}
