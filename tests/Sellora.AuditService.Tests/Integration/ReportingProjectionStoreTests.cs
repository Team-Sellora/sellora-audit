using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Infrastructure.Ingestion;
using Sellora.AuditService.Infrastructure.Reporting;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Integration;

/// <summary>
/// US-E7-2-BE1: the projections are written in the append's transaction, a
/// projection data error never costs the audit log an entry, and audit_app
/// may change the reporting schema but still not the audit log.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class ReportingProjectionStoreTests(PostgreSqlFixture fixture)
{
    private const string InsufficientPrivilege = "42501";

    [Fact]
    public async Task An_append_writes_the_audit_entry_and_its_projections_together()
    {
        var company = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var party = EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep");
        await using var harness = new AuditHarness(fixture, company);

        await harness.IngestAsync(EventBuilder.Order(company, "OrderPlaced", orderId, "ORD-TX-1", party, DateTimeOffset.UtcNow, 250m, "Placed"));

        Assert.Equal(1, await harness.Db.AuditEntries.CountAsync(entry => entry.CompanyId == company));
        var fact = await harness.Db.OrderFacts.Include(order => order.Lines).SingleAsync(order => order.OrderId == orderId);
        Assert.Equal(250m, fact.Total);
        Assert.Single(fact.Lines);
        Assert.Equal(1, await harness.Db.RepDimensions.CountAsync(rep => rep.CompanyId == company));
        Assert.Equal(1, await harness.Db.AgencyDimensions.CountAsync(agency => agency.CompanyId == company));
        Assert.Equal(1, await harness.Db.TerritoryDimensions.CountAsync(territory => territory.CompanyId == company));
    }

    [Fact]
    public async Task When_the_append_fails_after_projecting_neither_the_entry_nor_the_figures_exist()
    {
        var company = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var tenant = new TenantStub(company);
        await using var db = fixture.CreateAppContext(tenant);
        var store = new AuditStore(db, tenant, new FailingAfterProjector(new ReportingProjector(db, TimeProvider.System)),
            TimeProvider.System, new ListLogger<AuditStore>());

        var content = Normalise(EventBuilder.Order(company, "OrderPlaced", orderId, "ORD-TX-2",
            EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep"), DateTimeOffset.UtcNow, 100m, "Placed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(content, CancellationToken.None));

        Assert.Equal(0, await db.AuditEntries.CountAsync(entry => entry.CompanyId == company));
        Assert.Equal(0, await db.OrderFacts.CountAsync(fact => fact.CompanyId == company));
        Assert.Equal(0, await db.RepDimensions.CountAsync(rep => rep.CompanyId == company));
    }

    [Fact]
    public async Task A_projection_data_error_keeps_the_audit_entry_and_logs_the_skip()
    {
        var company = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var tenant = new TenantStub(company);
        var logger = new ListLogger<AuditStore>();
        await using var db = fixture.CreateAppContext(tenant);
        var store = new AuditStore(db, tenant, new ReportingProjector(db, TimeProvider.System), TimeProvider.System, logger);

        // numeric(18,2) cannot hold this total, so PostgreSQL refuses the projection (22003).
        var content = Normalise(EventBuilder.Order(company, "OrderPlaced", orderId, "ORD-TX-3",
            EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep"), DateTimeOffset.UtcNow, 100_000_000_000_000_000m, "Placed"));

        var outcome = await store.AppendAsync(content, CancellationToken.None);

        Assert.Equal(AppendStatus.Recorded, outcome.Status);
        Assert.Equal(1, await db.AuditEntries.CountAsync(entry => entry.CompanyId == company && entry.EventId == content.EventId));
        Assert.Equal(0, await db.OrderFacts.CountAsync(fact => fact.CompanyId == company));
        Assert.Contains(logger.Messages, message => message.StartsWith("ReportingProjectionSkipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rebuild_skips_an_entry_that_cannot_be_projected_and_keeps_the_rest()
    {
        var company = Guid.NewGuid();
        var party = EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep");
        await using var harness = new AuditHarness(fixture, company);
        await harness.IngestAllAsync(new[]
        {
            EventBuilder.Order(company, "OrderPlaced", Guid.NewGuid(), "ORD-RB-1", party, DateTimeOffset.UtcNow, 100_000_000_000_000_000m, "Placed"),
            EventBuilder.Order(company, "OrderPlaced", Guid.NewGuid(), "ORD-RB-2", party, DateTimeOffset.UtcNow, 50m, "Placed")
        });

        var rebuilt = await harness.Rebuild.RebuildAsync(company, CancellationToken.None);

        Assert.Equal(2, rebuilt.EntriesReplayed);
        Assert.Equal(1, rebuilt.OrdersProjected);
    }

    [Theory]
    [InlineData("order_fact")]
    [InlineData("order_fact_line")]
    [InlineData("rep_dimension")]
    [InlineData("territory_dimension")]
    [InlineData("agency_dimension")]
    public async Task The_service_role_holds_select_insert_update_and_delete_on_each_reporting_table(string table)
    {
        var privileges = await PrivilegesAsync("reporting", table);

        Assert.Equal(new[] { "DELETE", "INSERT", "SELECT", "UPDATE" }, privileges);
    }

    [Fact]
    public async Task The_service_role_can_write_the_reporting_tables_but_still_not_change_the_audit_log()
    {
        var company = Guid.NewGuid();
        await using (var harness = new AuditHarness(fixture, company))
        {
            await harness.IngestAsync(EventBuilder.AgencyRegistered(company, EventBuilder.Party(Guid.NewGuid(), "Agency", "Rep")));
        }

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();

        Assert.Equal(1, await ExecuteAsync(connection, $"UPDATE reporting.agency_dimension SET name = 'Renamed' WHERE company_id = '{company}'"));
        Assert.Equal(1, await ExecuteAsync(connection, $"DELETE FROM reporting.agency_dimension WHERE company_id = '{company}'"));

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, $"UPDATE audit.audit_entry SET payload = '{{}}' WHERE company_id = '{company}'"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, $"DELETE FROM audit.audit_entry WHERE company_id = '{company}'"));

        Assert.Equal(InsufficientPrivilege, update.SqlState);
        Assert.Equal(InsufficientPrivilege, delete.SqlState);
        Assert.Equal(new[] { "INSERT", "SELECT" }, await PrivilegesAsync("audit", "audit_entry"));
    }

    private async Task<List<string>> PrivilegesAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT privilege_type FROM information_schema.role_table_grants " +
            "WHERE grantee = 'audit_app' AND table_schema = @schema AND table_name = @table ORDER BY privilege_type",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        var privileges = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            privileges.Add(reader.GetString(0));
        }

        return privileges;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static AuditEventContent Normalise(IncomingMessage message) => AuditEnvelopeNormaliser.Normalise(message).Content!;

    private sealed class FailingAfterProjector(IReportingProjector inner) : IReportingProjector
    {
        public async Task ProjectAsync(AuditEventContent content, CancellationToken cancellationToken)
        {
            await inner.ProjectAsync(content, cancellationToken);
            throw new InvalidOperationException("Simulated failure after projecting.");
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
