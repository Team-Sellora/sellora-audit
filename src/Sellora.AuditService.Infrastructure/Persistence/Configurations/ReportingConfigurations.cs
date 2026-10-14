using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.AuditService.Domain.Exports;
using Sellora.AuditService.Domain.Reporting;

namespace Sellora.AuditService.Infrastructure.Persistence.Configurations;

/// <summary>Maps the reporting schema, created by SchemaSql.ReportingProjectionsUp.</summary>
public sealed class OrderFactConfiguration : IEntityTypeConfiguration<OrderFact>
{
    public void Configure(EntityTypeBuilder<OrderFact> builder)
    {
        builder.ToTable("order_fact", "reporting");
        builder.HasKey(fact => new { fact.CompanyId, fact.OrderId }).HasName("pk_order_fact");

        builder.Property(fact => fact.CompanyId).HasColumnName("company_id").HasColumnType("uuid");
        builder.Property(fact => fact.OrderId).HasColumnName("order_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(fact => fact.OrderReference).HasColumnName("order_reference").HasColumnType("text").IsRequired();
        builder.Property(fact => fact.ProvinceId).HasColumnName("province_id").HasColumnType("uuid");
        builder.Property(fact => fact.AgencyId).HasColumnName("agency_id").HasColumnType("uuid");
        builder.Property(fact => fact.AgencyName).HasColumnName("agency_name").HasColumnType("text");
        builder.Property(fact => fact.TerritoryId).HasColumnName("territory_id").HasColumnType("uuid");
        builder.Property(fact => fact.SalesRepId).HasColumnName("sales_rep_id").HasColumnType("uuid");
        builder.Property(fact => fact.SalesRepName).HasColumnName("sales_rep_name").HasColumnType("text");
        builder.Property(fact => fact.ShopId).HasColumnName("shop_id").HasColumnType("uuid");
        builder.Property(fact => fact.ShopName).HasColumnName("shop_name").HasColumnType("text");
        builder.Property(fact => fact.FulfilmentType).HasColumnName("fulfilment_type").HasColumnType("text");
        builder.Property(fact => fact.Status).HasColumnName("status").HasColumnType("text").IsRequired();
        builder.Property(fact => fact.Total).HasColumnName("total").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(fact => fact.Currency).HasColumnName("currency").HasColumnType("text").IsRequired();
        builder.Property(fact => fact.PlacedAt).HasColumnName("placed_at").HasColumnType("timestamp with time zone");
        builder.Property(fact => fact.PlacedDate).HasColumnName("placed_date").HasColumnType("date");
        builder.Property(fact => fact.ConfirmedAt).HasColumnName("confirmed_at").HasColumnType("timestamp with time zone");
        builder.Property(fact => fact.SaleDate).HasColumnName("sale_date").HasColumnType("date");
        builder.Property(fact => fact.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone");
        builder.Property(fact => fact.PaidAt).HasColumnName("paid_at").HasColumnType("timestamp with time zone");
        builder.Property(fact => fact.PaidAmount).HasColumnName("paid_amount").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(fact => fact.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasMany(fact => fact.Lines)
            .WithOne()
            .HasForeignKey(line => new { line.CompanyId, line.OrderId })
            .HasConstraintName("fk_order_fact_line_order_fact")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(fact => fact.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(fact => new { fact.CompanyId, fact.Status, fact.SaleDate }).HasDatabaseName("ix_order_fact_sale_date");
        builder.HasIndex(fact => new { fact.CompanyId, fact.PlacedDate }).HasDatabaseName("ix_order_fact_placed_date");
        builder.HasIndex(fact => new { fact.CompanyId, fact.ProvinceId, fact.SaleDate }).HasDatabaseName("ix_order_fact_province_sale_date");
    }
}

public sealed class OrderFactLineConfiguration : IEntityTypeConfiguration<OrderFactLine>
{
    public void Configure(EntityTypeBuilder<OrderFactLine> builder)
    {
        builder.ToTable("order_fact_line", "reporting");
        builder.HasKey(line => new { line.CompanyId, line.OrderId, line.LineNumber }).HasName("pk_order_fact_line");

        builder.Property(line => line.CompanyId).HasColumnName("company_id").HasColumnType("uuid");
        builder.Property(line => line.OrderId).HasColumnName("order_id").HasColumnType("uuid");
        builder.Property(line => line.LineNumber).HasColumnName("line_number").HasColumnType("integer").ValueGeneratedNever();
        builder.Property(line => line.ProductId).HasColumnName("product_id").HasColumnType("uuid").IsRequired();
        builder.Property(line => line.ProductName).HasColumnName("product_name").HasColumnType("text");
        builder.Property(line => line.Quantity).HasColumnName("quantity").HasColumnType("integer").IsRequired();
        builder.Property(line => line.LineTotal).HasColumnName("line_total").HasColumnType("numeric(18,2)").IsRequired();

        builder.HasIndex(line => new { line.CompanyId, line.ProductId }).HasDatabaseName("ix_order_fact_line_product");
    }
}

public sealed class RepDimensionConfiguration : IEntityTypeConfiguration<RepDimension>
{
    public void Configure(EntityTypeBuilder<RepDimension> builder)
    {
        builder.ToTable("rep_dimension", "reporting");
        builder.HasKey(rep => new { rep.CompanyId, rep.SalesRepId }).HasName("pk_rep_dimension");

        builder.Property(rep => rep.CompanyId).HasColumnName("company_id").HasColumnType("uuid");
        builder.Property(rep => rep.SalesRepId).HasColumnName("sales_rep_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(rep => rep.Name).HasColumnName("name").HasColumnType("text");
        builder.Property(rep => rep.TerritoryId).HasColumnName("territory_id").HasColumnType("uuid");
        builder.Property(rep => rep.AgencyId).HasColumnName("agency_id").HasColumnType("uuid");
        builder.Property(rep => rep.ProvinceId).HasColumnName("province_id").HasColumnType("uuid");
    }
}

public sealed class TerritoryDimensionConfiguration : IEntityTypeConfiguration<TerritoryDimension>
{
    public void Configure(EntityTypeBuilder<TerritoryDimension> builder)
    {
        builder.ToTable("territory_dimension", "reporting");
        builder.HasKey(territory => new { territory.CompanyId, territory.TerritoryId }).HasName("pk_territory_dimension");

        builder.Property(territory => territory.CompanyId).HasColumnName("company_id").HasColumnType("uuid");
        builder.Property(territory => territory.TerritoryId).HasColumnName("territory_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(territory => territory.Name).HasColumnName("name").HasColumnType("text");
        builder.Property(territory => territory.AgencyId).HasColumnName("agency_id").HasColumnType("uuid");
        builder.Property(territory => territory.ProvinceId).HasColumnName("province_id").HasColumnType("uuid");
    }
}

public sealed class AgencyDimensionConfiguration : IEntityTypeConfiguration<AgencyDimension>
{
    public void Configure(EntityTypeBuilder<AgencyDimension> builder)
    {
        builder.ToTable("agency_dimension", "reporting");
        builder.HasKey(agency => new { agency.CompanyId, agency.AgencyId }).HasName("pk_agency_dimension");

        builder.Property(agency => agency.CompanyId).HasColumnName("company_id").HasColumnType("uuid");
        builder.Property(agency => agency.AgencyId).HasColumnName("agency_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(agency => agency.Name).HasColumnName("name").HasColumnType("text");
        builder.Property(agency => agency.ProvinceId).HasColumnName("province_id").HasColumnType("uuid");
    }
}

public sealed class ReportExportConfiguration : IEntityTypeConfiguration<ReportExport>
{
    public void Configure(EntityTypeBuilder<ReportExport> builder)
    {
        builder.ToTable("report_export", "reporting");
        builder.HasKey(export => export.ExportId).HasName("pk_report_export");

        builder.Property(export => export.ExportId).HasColumnName("export_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(export => export.CompanyId).HasColumnName("company_id").HasColumnType("uuid").IsRequired();
        builder.Property(export => export.RequesterSub).HasColumnName("requester_sub").HasColumnType("text").IsRequired();
        builder.Property(export => export.RequesterName).HasColumnName("requester_name").HasColumnType("text").IsRequired();
        builder.Property(export => export.RequesterRole).HasColumnName("requester_role").HasColumnType("text").IsRequired();
        builder.Property(export => export.ScopeProvinceIds).HasColumnName("scope_province_ids").HasColumnType("uuid[]");
        builder.Property(export => export.ReportType).HasColumnName("report_type").HasColumnType("text").IsRequired();
        builder.Property(export => export.FiltersJson).HasColumnName("filters").HasColumnType("jsonb").IsRequired();
        builder.Property(export => export.Format).HasColumnName("format").HasColumnType("text").IsRequired();
        builder.Property(export => export.Status).HasColumnName("status").HasColumnType("text").HasConversion<string>().IsRequired();
        builder.Property(export => export.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(export => export.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone");
        builder.Property(export => export.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        builder.Property(export => export.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
        builder.Property(export => export.FileName).HasColumnName("file_name").HasColumnType("text");
        builder.Property(export => export.ContentType).HasColumnName("content_type").HasColumnType("text");
        builder.Property(export => export.Content).HasColumnName("content").HasColumnType("bytea");
        builder.Property(export => export.Error).HasColumnName("error").HasColumnType("character varying(200)").HasMaxLength(ReportExport.MaxErrorLength);

        builder.HasIndex(export => new { export.Status, export.CreatedAt }).HasDatabaseName("ix_report_export_status");
        builder.HasIndex(export => new { export.CompanyId, export.RequesterSub, export.CreatedAt })
            .HasDatabaseName("ix_report_export_requester")
            .IsDescending(false, false, true);
    }
}
