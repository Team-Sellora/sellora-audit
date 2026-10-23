using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Sellora.AuditService.Infrastructure.Persistence;

public static class AuditDatabase
{
    public const string UniqueViolation = "23505";

    /// <summary>The history table lives in its own schema, which the runtime role cannot see.</summary>
    public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(AuditDbContext.MigrationsHistoryTable, AuditDbContext.MigrationsHistorySchema);

    public static bool IsUniqueViolation(Exception exception) =>
        Find(exception) is { SqlState: UniqueViolation };

    public static bool IsUniqueViolation(Exception exception, string constraint) =>
        Find(exception) is { SqlState: UniqueViolation } postgres &&
        string.Equals(postgres.ConstraintName, constraint, StringComparison.Ordinal);

    /// <summary>
    /// Data that PostgreSQL will refuse however often it is retried: data
    /// exceptions (22), integrity violations (23) and limits (54). Anything
    /// else (connection loss, timeouts, deadlocks) is worth retrying.
    /// </summary>
    public static bool IsPermanentDataError(Exception exception) =>
        Find(exception) is { } postgres &&
        (postgres.SqlState.StartsWith("22", StringComparison.Ordinal) ||
         postgres.SqlState.StartsWith("23", StringComparison.Ordinal) ||
         postgres.SqlState.StartsWith("54", StringComparison.Ordinal));

    private static PostgresException? Find(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
