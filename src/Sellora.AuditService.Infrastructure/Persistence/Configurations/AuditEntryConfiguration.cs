using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.AuditService.Domain.Audit;

namespace Sellora.AuditService.Infrastructure.Persistence.Configurations;

/// <summary>Maps audit.audit_entry, created by SchemaSql.InitialAuditSchemaUp.</summary>
public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public const string CompanyEventUniqueIndex = "uq_audit_entry_company_event";
    public const string CompanyPositionUniqueIndex = "uq_audit_entry_company_position";

    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entry", "audit");
        builder.HasKey(entry => entry.AuditEntryId).HasName("pk_audit_entry");

        builder.Property(entry => entry.AuditEntryId).HasColumnName("audit_entry_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(entry => entry.CompanyId).HasColumnName("company_id").HasColumnType("uuid").IsRequired();
        builder.Property(entry => entry.ChainPosition).HasColumnName("chain_position").HasColumnType("bigint").IsRequired();
        builder.Property(entry => entry.EventId).HasColumnName("event_id").HasColumnType("uuid").IsRequired();
        builder.Property(entry => entry.EventType).HasColumnName("event_type").HasColumnType("text").IsRequired();
        builder.Property(entry => entry.SchemaVersion).HasColumnName("schema_version").HasColumnType("text");
        builder.Property(entry => entry.SourceTopic).HasColumnName("source_topic").HasColumnType("text").IsRequired();
        // "offset" and "partition" read badly as SQL; the columns say where they came from.
        builder.Property(entry => entry.Partition).HasColumnName("source_partition").HasColumnType("integer").IsRequired();
        builder.Property(entry => entry.Offset).HasColumnName("source_offset").HasColumnType("bigint").IsRequired();
        builder.Property(entry => entry.AggregateType).HasColumnName("aggregate_type").HasColumnType("text").IsRequired();
        builder.Property(entry => entry.AggregateId).HasColumnName("aggregate_id").HasColumnType("uuid");
        builder.Property(entry => entry.OrderReference).HasColumnName("order_reference").HasColumnType("text");
        builder.Property(entry => entry.ProvinceId).HasColumnName("province_id").HasColumnType("uuid");
        builder.Property(entry => entry.ActorUserId).HasColumnName("actor_user_id").HasColumnType("text").IsRequired();
        builder.Property(entry => entry.ActorRole).HasColumnName("actor_role").HasColumnType("text");
        builder.Property(entry => entry.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(entry => entry.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(entry => entry.CorrelationId).HasColumnName("correlation_id").HasColumnType("text");
        // text, not jsonb: jsonb reorders keys, and the hash is recomputed from the stored text.
        builder.Property(entry => entry.Payload).HasColumnName("payload").HasColumnType("text").IsRequired();
        builder.Property(entry => entry.PreviousHash).HasColumnName("previous_hash").HasColumnType("character(64)").IsRequired();
        builder.Property(entry => entry.Hash).HasColumnName("hash").HasColumnType("character(64)").IsRequired();

        builder.HasIndex(entry => new { entry.CompanyId, entry.EventId }).IsUnique().HasDatabaseName(CompanyEventUniqueIndex);
        builder.HasIndex(entry => new { entry.CompanyId, entry.ChainPosition }).IsUnique().HasDatabaseName(CompanyPositionUniqueIndex);
        builder.HasIndex(entry => new { entry.CompanyId, entry.OrderReference, entry.OccurredAt, entry.ChainPosition })
            .HasDatabaseName("ix_audit_entry_order_reference").HasFilter("order_reference IS NOT NULL");
        builder.HasIndex(entry => new { entry.CompanyId, entry.AggregateType, entry.AggregateId, entry.OccurredAt, entry.ChainPosition })
            .HasDatabaseName("ix_audit_entry_aggregate");
        builder.HasIndex(entry => new { entry.CompanyId, entry.ActorUserId, entry.OccurredAt }).HasDatabaseName("ix_audit_entry_actor");
        builder.HasIndex(entry => new { entry.CompanyId, entry.EventType, entry.OccurredAt }).HasDatabaseName("ix_audit_entry_event_type");
        builder.HasIndex(entry => new { entry.CompanyId, entry.OccurredAt, entry.ChainPosition })
            .HasDatabaseName("ix_audit_entry_occurred").IsDescending(false, true, true);
        builder.HasIndex(entry => new { entry.CompanyId, entry.ProvinceId, entry.OccurredAt }).HasDatabaseName("ix_audit_entry_province");
    }
}
