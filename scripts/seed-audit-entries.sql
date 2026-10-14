-- US-E7-1-Q7: seeds a valid, hash-chained audit log for load testing.
--
-- Run as audit_owner (the runtime role cannot be used for bulk seeding),
-- on a test database only, with the audit service stopped so nothing else
-- appends to the same company's chain meanwhile:
--
--   psql "host=localhost port=5438 dbname=audit_db user=audit_owner" \
--     -v company_id=30000000-0000-0000-0000-000000000001 \
--     -v entries=100000 -v orders=20000 -v days=90 \
--     -f scripts/seed-audit-entries.sql
--
-- Entries continue the company's existing chain, use the same hash as the
-- service (AuditHasher), and spread over `orders` order references and the
-- last `days` days, so GET /api/audit/verify still passes afterwards.
\set ON_ERROR_STOP on

\if :{?company_id}
\else
  -- \quit cannot set an exit code; a failing statement under ON_ERROR_STOP does.
  DO $$ BEGIN RAISE EXCEPTION 'Pass -v company_id=<uuid>'; END $$;
\endif
\if :{?entries}
\else
  \set entries 100000
\endif
\if :{?orders}
\else
  \set orders 20000
\endif
\if :{?days}
\else
  \set days 90
\endif

SELECT set_config('seed.company_id', :'company_id', false),
       set_config('seed.entries', :'entries', false),
       set_config('seed.orders', :'orders', false),
       set_config('seed.days', :'days', false);

DO $$
DECLARE
    v_company   uuid := current_setting('seed.company_id')::uuid;
    v_entries   integer := current_setting('seed.entries')::integer;
    v_orders    integer := greatest(1, current_setting('seed.orders')::integer);
    v_days      integer := greatest(1, current_setting('seed.days')::integer);
    v_types     text[] := ARRAY['OrderPlaced', 'OrderConfirmed', 'PaymentRecorded', 'OrderCancelled'];
    v_position  bigint;
    v_previous  text;
    v_start     timestamptz := date_trunc('second', now()) - make_interval(days => v_days);
    v_step      double precision;
    v_index     integer;
    v_order     integer;
    v_type      text;
    v_event     uuid;
    v_aggregate uuid;
    v_reference text;
    v_occurred  timestamptz;
    v_payload   text;
    v_hash      text;
BEGIN
    IF v_entries < 1 THEN
        RAISE EXCEPTION 'entries must be at least 1';
    END IF;

    -- The same lock the service takes per company, so a running consumer cannot interleave.
    PERFORM pg_advisory_xact_lock(hashtext(v_company::text));

    SELECT chain_position, hash
      INTO v_position, v_previous
      FROM audit.audit_entry
     WHERE company_id = v_company
     ORDER BY chain_position DESC
     LIMIT 1;

    v_position := coalesce(v_position, 0);
    v_previous := coalesce(v_previous, repeat('0', 64));
    v_step := (v_days * 86400.0) / v_entries;

    FOR v_index IN 1..v_entries LOOP
        v_position  := v_position + 1;
        v_order     := ((v_index - 1) % v_orders) + 1;
        v_type      := v_types[((v_index - 1) / v_orders) % 4 + 1];
        v_event     := gen_random_uuid();
        v_aggregate := md5('seed-order-' || v_company || '-' || v_order)::uuid;
        v_reference := 'ORD-SEED-' || lpad(v_order::text, 6, '0');
        -- Whole microseconds, as the service stores them.
        v_occurred  := date_trunc('milliseconds', v_start + make_interval(secs => v_index * v_step));
        v_payload   := json_build_object(
                           'eventId', v_event,
                           'eventType', v_type,
                           'schemaVersion', '1.0',
                           'companyId', v_company,
                           'orderId', v_aggregate,
                           'orderReference', v_reference,
                           'occurredAt', to_char(v_occurred AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                           'seeded', true)::text;

        v_hash := encode(sha256(convert_to(concat_ws('|',
                      v_position::text,
                      v_company::text,
                      v_event::text,
                      v_type,
                      'Order',
                      v_aggregate::text,
                      to_char(v_occurred AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                      '',
                      v_payload,
                      v_previous), 'UTF8')), 'hex');

        INSERT INTO audit.audit_entry (
            audit_entry_id, company_id, chain_position, event_id, event_type, schema_version,
            source_topic, source_partition, source_offset, aggregate_type, aggregate_id,
            order_reference, province_id, actor_user_id, actor_role, occurred_at, recorded_at,
            correlation_id, payload, previous_hash, hash)
        VALUES (
            gen_random_uuid(), v_company, v_position, v_event, v_type, '1.0',
            'seed', 0, v_index, 'Order', v_aggregate,
            v_reference, NULL, 'seed', NULL, v_occurred, now(),
            NULL, v_payload, v_previous, v_hash);

        v_previous := v_hash;
    END LOOP;

    RAISE NOTICE 'Seeded % entries for company %; chain now ends at position %.', v_entries, v_company, v_position;
END
$$;

ANALYZE audit.audit_entry;
