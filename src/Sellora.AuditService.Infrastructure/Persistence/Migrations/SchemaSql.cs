namespace Sellora.AuditService.Infrastructure.Persistence.Migrations;

/// <summary>
/// The schema as plain SQL, applied by the EF migrations in this folder.
/// Kept as SQL so the exact statements that run in CD are the ones the
/// grant tests run against, and so the schema and its privileges stay
/// explicit (backlog 0.8).
/// </summary>
public static class SchemaSql
{
    public const string InitialAuditSchemaUp = """
        CREATE SCHEMA IF NOT EXISTS audit;

        CREATE TABLE audit.audit_entry (
            audit_entry_id   uuid        NOT NULL,
            company_id       uuid        NOT NULL,
            chain_position   bigint      NOT NULL,
            event_id         uuid        NOT NULL,
            event_type       text        NOT NULL,
            schema_version   text        NULL,
            source_topic     text        NOT NULL,
            source_partition integer     NOT NULL,
            source_offset    bigint      NOT NULL,
            aggregate_type   text        NOT NULL,
            aggregate_id     uuid        NULL,
            order_reference  text        NULL,
            province_id      uuid        NULL,
            actor_user_id    text        NOT NULL,
            actor_role       text        NULL,
            occurred_at      timestamptz NOT NULL,
            recorded_at      timestamptz NOT NULL,
            correlation_id   text        NULL,
            payload          text        NOT NULL,
            previous_hash    char(64)    NOT NULL,
            hash             char(64)    NOT NULL,
            CONSTRAINT pk_audit_entry PRIMARY KEY (audit_entry_id),
            CONSTRAINT ck_audit_entry_chain_position_positive CHECK (chain_position >= 1)
        );

        CREATE UNIQUE INDEX uq_audit_entry_company_event ON audit.audit_entry (company_id, event_id);
        CREATE UNIQUE INDEX uq_audit_entry_company_position ON audit.audit_entry (company_id, chain_position);
        CREATE INDEX ix_audit_entry_order_reference ON audit.audit_entry (company_id, order_reference, occurred_at, chain_position) WHERE order_reference IS NOT NULL;
        CREATE INDEX ix_audit_entry_aggregate ON audit.audit_entry (company_id, aggregate_type, aggregate_id, occurred_at, chain_position);
        CREATE INDEX ix_audit_entry_actor ON audit.audit_entry (company_id, actor_user_id, occurred_at);
        CREATE INDEX ix_audit_entry_event_type ON audit.audit_entry (company_id, event_type, occurred_at);
        CREATE INDEX ix_audit_entry_occurred ON audit.audit_entry (company_id, occurred_at DESC, chain_position DESC);
        CREATE INDEX ix_audit_entry_province ON audit.audit_entry (company_id, province_id, occurred_at);
        """;

    public const string InitialAuditSchemaDown = """
        DROP TABLE IF EXISTS audit.audit_entry;
        """;
}
