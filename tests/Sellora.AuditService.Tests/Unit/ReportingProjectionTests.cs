using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Domain.Reporting;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Unit;

/// <summary>US-E7-2-BE1/BE2: the projection rules, against the real producer fixtures.</summary>
public sealed class ReportingProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_order_confirmed_at_2330_colombo_time_on_30_september_is_a_september_sale()
    {
        Assert.Equal(new DateOnly(2026, 9, 30), SalesCalendar.ToSalesDate(new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 9, 30), SalesCalendar.ToSalesDate(new DateTimeOffset(2026, 9, 30, 23, 30, 0, SalesCalendar.ColomboOffset)));
    }

    [Fact]
    public void Half_past_midnight_colombo_time_is_the_next_day()
    {
        Assert.Equal(new DateOnly(2026, 10, 1), SalesCalendar.ToSalesDate(new DateTimeOffset(2026, 9, 30, 18, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_order_fixtures_project_to_a_confirmed_and_paid_cash_sale()
    {
        var fact = Project("OrderPlaced", "OrderConfirmed", "PaymentRecorded");

        Assert.Equal(OrderFactStatus.Confirmed, fact.Status);
        Assert.Equal("ORD-260924-K7P2QM", fact.OrderReference);
        Assert.Equal(1255.00m, fact.Total);
        Assert.Equal(1255.00m, fact.PaidAmount);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 5, 12, 41, 200, TimeSpan.Zero), fact.ConfirmedAt);
        Assert.Equal(new DateOnly(2026, 9, 24), fact.SaleDate);
        Assert.Equal(new DateOnly(2026, 9, 24), fact.PlacedDate);
        Assert.Equal(Guid.Parse("40000000-0000-0000-0000-000000000001"), fact.ProvinceId);
        Assert.Equal(Guid.Parse("43000000-0000-0000-0000-000000000001"), fact.SalesRepId);
        Assert.Equal("Nimal Perera", fact.SalesRepName);
        Assert.Equal("Colombo Distribution Agency", fact.AgencyName);
    }

    [Fact]
    public void The_lines_are_taken_once_from_the_order()
    {
        var fact = Project("OrderPlaced", "OrderConfirmed", "PaymentRecorded");

        var line = Assert.Single(fact.Lines);
        Assert.Equal(1, line.LineNumber);
        Assert.Equal(Guid.Parse("45000000-0000-0000-0000-000000000001"), line.ProductId);
        Assert.Equal(10, line.Quantity);
        Assert.Equal(1255.00m, line.LineTotal);
    }

    [Fact]
    public void A_cancellation_is_final_even_when_a_confirmation_arrives_after_it()
    {
        var fact = Project("OrderPlaced", "OrderCancelled", "OrderConfirmed", "PaymentRecorded");

        Assert.Equal(OrderFactStatus.Cancelled, fact.Status);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.Zero), fact.CancelledAt);
    }

    [Fact]
    public void A_placed_event_replayed_late_cannot_move_the_status_back()
    {
        var fact = Project("OrderConfirmed", "OrderApproved", "OrderPlaced");

        Assert.Equal(OrderFactStatus.Confirmed, fact.Status);
    }

    [Fact]
    public void Replaying_every_event_twice_gives_the_same_row()
    {
        var once = Project("OrderPlaced", "OrderConfirmed", "PaymentRecorded", "OrderCancelled");
        var twice = Project("OrderPlaced", "OrderConfirmed", "PaymentRecorded", "OrderCancelled",
            "OrderPlaced", "OrderConfirmed", "PaymentRecorded", "OrderCancelled");

        Assert.Equal(Snapshot(once), Snapshot(twice));
        Assert.Single(twice.Lines);
    }

    [Fact]
    public void Any_order_of_the_same_events_gives_the_same_row()
    {
        var inOrder = Project("OrderPlaced", "OrderConfirmed", "PaymentRecorded");
        var reversed = Project("PaymentRecorded", "OrderConfirmed", "OrderPlaced");

        Assert.Equal(Snapshot(inOrder), Snapshot(reversed));
    }

    [Fact]
    public void A_payment_seen_before_its_confirmation_keeps_the_earlier_confirmation_time()
    {
        var fact = Project("PaymentRecorded", "OrderConfirmed");

        Assert.Equal(new DateTimeOffset(2026, 9, 24, 5, 12, 41, 200, TimeSpan.Zero), fact.ConfirmedAt);
    }

    [Fact]
    public void A_placed_event_that_says_confirmed_does_not_make_a_sale()
    {
        var party = EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep");
        var orderId = Guid.NewGuid();
        var placed = Content(EventBuilder.Order(Guid.NewGuid(), "OrderPlaced", orderId, "ORD-1", party, Now, 100m, "Confirmed"));

        var fact = OrderFact.Start(placed.CompanyId, orderId);
        fact.Apply(ProjectionEventReader.ReadOrderEvent(placed)!, Now);

        Assert.Equal(OrderFactStatus.Placed, fact.Status);
        Assert.Null(fact.SaleDate);
    }

    [Fact]
    public void Hierarchy_events_become_dimension_changes()
    {
        var rep = Assert.Single(ProjectionEventReader.ReadDimensionChanges(Fixture("organization", "SalesRepAssigned")));
        var agency = Assert.Single(ProjectionEventReader.ReadDimensionChanges(Fixture("organization", "AgencyRegistered")));

        Assert.Equal(new DimensionChange(DimensionKind.Rep, Guid.Parse("43000000-0000-0000-0000-000000000001"), "Nimal Perera",
            Guid.Parse("42000000-0000-0000-0000-000000000001"), Guid.Parse("41000000-0000-0000-0000-000000000001"), null), rep);
        Assert.Equal(new DimensionChange(DimensionKind.Agency, Guid.Parse("41000000-0000-0000-0000-000000000001"), "Colombo Distribution Agency",
            null, null, Guid.Parse("40000000-0000-0000-0000-000000000001")), agency);
    }

    [Fact]
    public void A_territory_assignment_becomes_a_territory_dimension_change()
    {
        var territory = Guid.NewGuid();
        var agency = Guid.NewGuid();
        var message = EventBuilder.Raw("sellora.hierarchy.v1",
            $$"""{"eventId":"{{Guid.NewGuid()}}","eventType":"TerritoryAssignedToAgency","companyId":"{{Guid.NewGuid()}}","territoryId":"{{territory}}","agencyId":"{{agency}}"}""");

        var change = Assert.Single(ProjectionEventReader.ReadDimensionChanges(Content(message)));

        Assert.Equal(new DimensionChange(DimensionKind.Territory, territory, null, null, agency, null), change);
    }

    [Fact]
    public void An_order_event_names_its_rep_agency_and_territory()
    {
        var changes = ProjectionEventReader.ReadDimensionChanges(Fixture("order", "OrderPlaced"));

        Assert.Equal(new[] { DimensionKind.Rep, DimensionKind.Agency, DimensionKind.Territory }, changes.Select(change => change.Kind));
        Assert.All(changes, change => Assert.Equal(Guid.Parse("40000000-0000-0000-0000-000000000001"), change.ProvinceId));
    }

    [Theory]
    [InlineData("catalog", "PriceChanged")]
    [InlineData("inventory", "LowStockDetected")]
    [InlineData("delivery", "DeliveryStatusChanged")]
    public void Other_events_only_live_in_the_audit_log(string producer, string name)
    {
        var content = Fixture(producer, name);

        Assert.Null(ProjectionEventReader.ReadOrderEvent(content));
        Assert.Empty(ProjectionEventReader.ReadDimensionChanges(content));
    }

    private static OrderFact Project(params string[] events)
    {
        var contents = events.Select(name => Fixture("order", name)).ToList();
        var fact = OrderFact.Start(contents[0].CompanyId, ProjectionEventReader.ReadOrderEvent(contents[0])!.OrderId);

        foreach (var content in contents)
        {
            fact.Apply(ProjectionEventReader.ReadOrderEvent(content)!, Now);
        }

        return fact;
    }

    private static object Snapshot(OrderFact fact) => new
    {
        fact.Status,
        fact.Total,
        fact.PaidAmount,
        fact.PlacedAt,
        fact.PlacedDate,
        fact.ConfirmedAt,
        fact.SaleDate,
        fact.CancelledAt,
        fact.PaidAt,
        fact.ProvinceId,
        fact.SalesRepId,
        fact.AgencyId,
        fact.TerritoryId,
        Lines = string.Join(";", fact.Lines.Select(line => $"{line.LineNumber}:{line.ProductId}:{line.Quantity}:{line.LineTotal}"))
    };

    private static AuditEventContent Fixture(string producer, string name) =>
        Content(ProducerFixtures.Get($"{producer}/{name}").Message);

    private static AuditEventContent Content(IncomingMessage message)
    {
        var normalised = AuditEnvelopeNormaliser.Normalise(message);
        Assert.True(normalised.IsAccepted, normalised.Detail);
        return normalised.Content!;
    }
}
