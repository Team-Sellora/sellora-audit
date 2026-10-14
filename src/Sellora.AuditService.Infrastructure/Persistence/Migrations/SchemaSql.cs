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

    public const string ReportingProjectionsUp = """
        CREATE SCHEMA IF NOT EXISTS reporting;

        CREATE TABLE reporting.order_fact (
            company_id      uuid          NOT NULL,
            order_id        uuid          NOT NULL,
            order_reference text          NOT NULL,
            province_id     uuid          NULL,
            agency_id       uuid          NULL,
            agency_name     text          NULL,
            territory_id    uuid          NULL,
            sales_rep_id    uuid          NULL,
            sales_rep_name  text          NULL,
            shop_id         uuid          NULL,
            shop_name       text          NULL,
            fulfilment_type text          NULL,
            status          text          NOT NULL,
            total           numeric(18,2) NOT NULL,
            currency        text          NOT NULL,
            placed_at       timestamptz   NULL,
            placed_date     date          NULL,
            confirmed_at    timestamptz   NULL,
            sale_date       date          NULL,
            cancelled_at    timestamptz   NULL,
            paid_at         timestamptz   NULL,
            paid_amount     numeric(18,2) NOT NULL,
            updated_at      timestamptz   NOT NULL,
            CONSTRAINT pk_order_fact PRIMARY KEY (company_id, order_id)
        );

        CREATE INDEX ix_order_fact_sale_date ON reporting.order_fact (company_id, status, sale_date);
        CREATE INDEX ix_order_fact_placed_date ON reporting.order_fact (company_id, placed_date);
        CREATE INDEX ix_order_fact_province_sale_date ON reporting.order_fact (company_id, province_id, sale_date);

        CREATE TABLE reporting.order_fact_line (
            company_id   uuid          NOT NULL,
            order_id     uuid          NOT NULL,
            line_number  integer       NOT NULL,
            product_id   uuid          NOT NULL,
            product_name text          NULL,
            quantity     integer       NOT NULL,
            line_total   numeric(18,2) NOT NULL,
            CONSTRAINT pk_order_fact_line PRIMARY KEY (company_id, order_id, line_number),
            CONSTRAINT fk_order_fact_line_order_fact FOREIGN KEY (company_id, order_id)
                REFERENCES reporting.order_fact (company_id, order_id) ON DELETE CASCADE
        );

        CREATE INDEX ix_order_fact_line_product ON reporting.order_fact_line (company_id, product_id);

        CREATE TABLE reporting.rep_dimension (
            company_id   uuid NOT NULL,
            sales_rep_id uuid NOT NULL,
            name         text NULL,
            territory_id uuid NULL,
            agency_id    uuid NULL,
            province_id  uuid NULL,
            CONSTRAINT pk_rep_dimension PRIMARY KEY (company_id, sales_rep_id)
        );

        CREATE TABLE reporting.territory_dimension (
            company_id   uuid NOT NULL,
            territory_id uuid NOT NULL,
            name         text NULL,
            agency_id    uuid NULL,
            province_id  uuid NULL,
            CONSTRAINT pk_territory_dimension PRIMARY KEY (company_id, territory_id)
        );

        CREATE TABLE reporting.agency_dimension (
            company_id  uuid NOT NULL,
            agency_id   uuid NOT NULL,
            name        text NULL,
            province_id uuid NULL,
            CONSTRAINT pk_agency_dimension PRIMARY KEY (company_id, agency_id)
        );
        """;

    public const string ReportingProjectionsDown = """
        DROP TABLE IF EXISTS reporting.order_fact_line;
        DROP TABLE IF EXISTS reporting.order_fact;
        DROP TABLE IF EXISTS reporting.rep_dimension;
        DROP TABLE IF EXISTS reporting.territory_dimension;
        DROP TABLE IF EXISTS reporting.agency_dimension;
        """;

    public const string ReportExportsUp = """
        CREATE TABLE reporting.report_export (
            export_id          uuid         NOT NULL,
            company_id         uuid         NOT NULL,
            requester_sub      text         NOT NULL,
            requester_name     text         NOT NULL,
            requester_role     text         NOT NULL,
            scope_province_ids uuid[]       NULL,
            report_type        text         NOT NULL,
            filters            jsonb        NOT NULL,
            format             text         NOT NULL,
            status             text         NOT NULL,
            created_at         timestamptz  NOT NULL,
            started_at         timestamptz  NULL,
            completed_at       timestamptz  NULL,
            expires_at         timestamptz  NULL,
            file_name          text         NULL,
            content_type       text         NULL,
            content            bytea        NULL,
            error              varchar(200) NULL,
            CONSTRAINT pk_report_export PRIMARY KEY (export_id),
            CONSTRAINT ck_report_export_report_type CHECK (report_type IN ('sales-summary', 'sales-breakdown', 'audit-extract')),
            CONSTRAINT ck_report_export_format CHECK (format IN ('csv', 'pdf')),
            CONSTRAINT ck_report_export_status CHECK (status IN ('Queued', 'Running', 'Ready', 'Failed', 'Expired'))
        );

        CREATE INDEX ix_report_export_status ON reporting.report_export (status, created_at);
        CREATE INDEX ix_report_export_requester ON reporting.report_export (company_id, requester_sub, created_at DESC);
        """;

    public const string ReportExportsDown = """
        DROP TABLE IF EXISTS reporting.report_export;
        """;
}
