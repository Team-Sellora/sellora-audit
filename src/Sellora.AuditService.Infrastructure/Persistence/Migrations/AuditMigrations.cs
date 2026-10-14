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

/// <summary>US-E7-2-BE1: the reporting projections.</summary>
[DbContext(typeof(AuditDbContext))]
[Migration("20261014090000_ReportingProjections")]
public sealed class ReportingProjections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SchemaSql.ReportingProjectionsUp);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SchemaSql.ReportingProjectionsDown);
}

/// <summary>US-E7-3-BE1: export jobs and their files.</summary>
[DbContext(typeof(AuditDbContext))]
[Migration("20261015090000_ReportExports")]
public sealed class ReportExports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SchemaSql.ReportExportsUp);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SchemaSql.ReportExportsDown);
}
