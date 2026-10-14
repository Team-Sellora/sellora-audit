using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.AuditService.Domain.Audit;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Integration;

/// <summary>
/// US-E7-1-BE6 / DoD 3: the database and service layers of the three
/// immutability layers, each proven by a failing write. (The API layer is
/// AuditRouteImmutabilityTests.)
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class ImmutabilityTests(PostgreSqlFixture fixture)
{
    private const string InsufficientPrivilege = "42501";

    [Theory]
    [InlineData("UPDATE audit.audit_entry SET payload = '{\"tampered\":true}'")]
    [InlineData("DELETE FROM audit.audit_entry")]
    [InlineData("TRUNCATE audit.audit_entry")]
    [InlineData("SELECT 1 FROM audit.audit_entry FOR UPDATE")]
    [InlineData("CREATE TABLE audit.shadow (x integer)")]
    [InlineData("ALTER TABLE audit.audit_entry DROP COLUMN hash")]
    public async Task The_service_database_role_cannot_change_the_audit_log(string sql)
    {
        await SeedOneEntryAsync();

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task Raw_sql_through_ef_core_is_refused_as_well()
    {
        var companyId = await SeedOneEntryAsync();
        await using var db = fixture.CreateAppContext(new TenantStub(companyId));

        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE audit.audit_entry SET actor_user_id = 'someone-else'"));

        Assert.Equal(InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task The_service_role_can_still_read_and_append()
    {
        var companyId = await SeedOneEntryAsync();
        await using var harness = new AuditHarness(fixture, companyId);

        var count = await harness.Db.AuditEntries.CountAsync(entry => entry.CompanyId == companyId);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task The_service_role_holds_exactly_select_and_insert_on_the_audit_table()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT privilege_type FROM information_schema.role_table_grants " +
            "WHERE grantee = 'audit_app' AND table_schema = 'audit' AND table_name = 'audit_entry' ORDER BY privilege_type",
            connection);

        var privileges = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                privileges.Add(reader.GetString(0));
            }
        }

        Assert.Equal(new[] { "INSERT", "SELECT" }, privileges);
    }

    [Fact]
    public async Task The_owner_role_is_not_inherited_by_the_service_role()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SET ROLE audit_owner", connection);

        var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task The_db_context_refuses_to_save_a_modified_audit_entry()
    {
        var companyId = await SeedOneEntryAsync();
        await using var db = fixture.CreateAppContext(new TenantStub(companyId));
        var entry = await db.AuditEntries.FirstAsync(candidate => candidate.CompanyId == companyId);

        db.Entry(entry).Property(candidate => candidate.Payload).CurrentValue = "{\"tampered\":true}";

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", refused.Message);
    }

    [Fact]
    public async Task The_db_context_refuses_to_delete_an_audit_entry()
    {
        var companyId = await SeedOneEntryAsync();
        await using var db = fixture.CreateAppContext(new TenantStub(companyId));
        var entry = await db.AuditEntries.FirstAsync(candidate => candidate.CompanyId == companyId);

        db.AuditEntries.Remove(entry);

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task The_migrations_history_is_not_visible_to_the_service_role()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM migrations.__ef_migrations_history", connection);

        var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());

        Assert.Equal(InsufficientPrivilege, denied.SqlState);
    }

    private async Task<Guid> SeedOneEntryAsync()
    {
        var companyId = Guid.NewGuid();
        await using var harness = new AuditHarness(fixture);

        var outcome = await harness.Store.AppendAsync(new AuditEventContent(
            companyId, Guid.NewGuid(), "OrderPlaced", "1.0", "sellora.order.v1", 0, 1, "Order", Guid.NewGuid(),
            "ORD-IMMUTABLE", null, "rep-1", "SalesRep", DateTimeOffset.UtcNow, "c-1", "{}"), CancellationToken.None);

        Assert.Equal(1, outcome.ChainPosition);
        return companyId;
    }
}
