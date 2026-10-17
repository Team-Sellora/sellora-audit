-- US-E7-1-Q3c - the runtime role cannot change the audit log.
-- Run AS THE RUNTIME ROLE (audit_app), against staging or local, and attach the output to CSP-250:
--   psql "host=<server> port=5432 dbname=audit_db user=audit_app sslmode=require" -f audit-db-grants.sql > grants-staging.txt 2>&1
-- Expected: the three SELECTs work, every UPDATE / DELETE / TRUNCATE / ALTER / DROP says "permission denied".
\set ON_ERROR_STOP off
\echo '== who am I'
SELECT current_user, current_database(), now();

\echo '== allowed: SELECT'
SELECT count(*) AS audit_entries FROM audit.audit_entry;
SELECT chain_position, event_type, left(hash, 12) AS hash FROM audit.audit_entry ORDER BY recorded_at DESC LIMIT 3;

\echo '== must fail: UPDATE'
UPDATE audit.audit_entry SET payload = payload WHERE chain_position = 1;
\echo '== must fail: DELETE'
DELETE FROM audit.audit_entry WHERE chain_position = 1;
\echo '== must fail: TRUNCATE'
TRUNCATE audit.audit_entry;
\echo '== must fail: ALTER / DROP (no DDL for the runtime role)'
ALTER TABLE audit.audit_entry ADD COLUMN qa_probe int;
DROP TABLE audit.audit_entry;

\echo '== grants on the audit schema (expect only SELECT and INSERT for audit_app)'
SELECT grantee, table_name, string_agg(privilege_type, ', ' ORDER BY privilege_type) AS privileges
FROM information_schema.role_table_grants
WHERE table_schema = 'audit'
GROUP BY grantee, table_name
ORDER BY grantee, table_name;

\echo '== reporting schema: full DML expected for audit_app (projections are updated)'
SELECT grantee, table_name, string_agg(privilege_type, ', ' ORDER BY privilege_type) AS privileges
FROM information_schema.role_table_grants
WHERE table_schema = 'reporting' AND grantee = current_user
GROUP BY grantee, table_name
ORDER BY table_name;
