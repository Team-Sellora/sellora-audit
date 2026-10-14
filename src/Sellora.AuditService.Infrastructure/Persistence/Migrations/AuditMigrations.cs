using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sellora.AuditService.Infrastructure.Persistence.Migrations;

// The app never migrates at startup: its runtime role has no DDL rights.
// CD runs these with the audit_owner connection (scripts/run-migrations.sh).

/// <summary>US-E7-1-BE2: audit.audit_entry and its indexes.</summary>
[DbContext(typeof(AuditDbContext))]
[Migration("20261013090000_InitialAuditSchema")]
public sealed class InitialAuditSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SchemaSql.InitialAuditSchemaUp);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SchemaSql.InitialAuditSchemaDown);
}
