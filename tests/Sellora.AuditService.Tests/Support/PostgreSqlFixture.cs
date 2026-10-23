using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.AuditService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Sellora.AuditService.Tests.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL integration tests";
}

/// <summary>
/// A real PostgreSQL with the real permissions: the roles script runs as the
/// superuser, migrations run as audit_owner, and the code under test runs as
/// audit_app, exactly as in CI and on Azure (US-E7-1-D1/D2).
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string OwnerPassword = "audit_owner_test_pw";
    public const string AppPassword = "audit_app_test_pw";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("audit_db")
        .WithUsername("postgres")
        .WithPassword("postgres_test_pw")
        .Build();

    public string OwnerConnectionString { get; private set; } = string.Empty;

    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await RunRolesScriptAsync();

        OwnerConnectionString = As("audit_owner", OwnerPassword);
        AppConnectionString = As("audit_app", AppPassword);

        // MigrateAsync (not EnsureCreated), as the owner, so the real migrations run.
        await using (var owner = CreateContext(OwnerConnectionString, null))
        {
            await owner.Database.MigrateAsync();
        }

        await RunRolesScriptAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A context on the runtime role, as the service uses it.</summary>
    public AuditDbContext CreateAppContext(TenantStub tenant) => CreateContext(AppConnectionString, tenant);

    /// <summary>A context on the owner role: elevated rights, used only to tamper in tests.</summary>
    public AuditDbContext CreateOwnerContext(TenantStub tenant) => CreateContext(OwnerConnectionString, tenant);

    public async Task<int> ExecuteAsOwnerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static AuditDbContext CreateContext(string connectionString, TenantStub? tenant) => new(
        new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString, AuditDatabase.ConfigureNpgsql)
            .EnableSensitiveDataLogging()
            .Options,
        tenant ?? new TenantStub());

    private string As(string username, string password) => new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
    {
        Username = username,
        Password = password,
        IncludeErrorDetail = true
    }.ConnectionString;

    private async Task RunRolesScriptAsync()
    {
        var script = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Database", "create-db-roles.sql"));
        await _container.CopyAsync(script, "/tmp/create-db-roles.sql");

        var result = await _container.ExecAsync(new[]
        {
            "psql", "-U", "postgres", "-d", "audit_db",
            "-v", "ON_ERROR_STOP=1",
            "-v", $"owner_password={OwnerPassword}",
            "-v", $"app_password={AppPassword}",
            "-f", "/tmp/create-db-roles.sql"
        });

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"create-db-roles.sql failed: {result.Stderr}");
        }
    }
}
