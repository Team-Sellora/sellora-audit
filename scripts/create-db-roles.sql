-- US-E7-1-D2 / backlog 0.8: roles and schemas for audit_db.
--
--   audit_owner  owns the schemas and tables; used only to run migrations.
--   audit_app    the runtime role. SELECT and INSERT on the audit schema,
--                nothing else, so the audit log cannot be altered with the
--                service's own credentials. SELECT, INSERT, UPDATE and
--                DELETE on the reporting schema, whose projections are
--                rebuilt from the audit log (US-E7-2).
--
-- Run as an administrator of audit_db, before and again after migrations.
-- It is idempotent and holds no secrets:
--
--   psql -v ON_ERROR_STOP=1 \
--        -v owner_password="$AUDIT_OWNER_PASSWORD" \
--        -v app_password="$AUDIT_APP_PASSWORD" \
--        -d audit_db -f scripts/create-db-roles.sql

\set ON_ERROR_STOP on

SELECT 'CREATE ROLE audit_owner LOGIN'
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'audit_owner') \gexec

SELECT 'CREATE ROLE audit_app LOGIN'
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'audit_app') \gexec

SELECT format('ALTER ROLE audit_owner WITH LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB PASSWORD %L', :'owner_password') \gexec
SELECT format('ALTER ROLE audit_app WITH LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB NOINHERIT PASSWORD %L', :'app_password') \gexec

-- The runtime role must never inherit the owner's rights.
SELECT 'REVOKE audit_owner FROM audit_app'
WHERE EXISTS (
    SELECT 1 FROM pg_auth_members m
    JOIN pg_roles granted ON granted.oid = m.roleid
    JOIN pg_roles member ON member.oid = m.member
    WHERE granted.rolname = 'audit_owner' AND member.rolname = 'audit_app') \gexec

SELECT format('GRANT CONNECT, CREATE ON DATABASE %I TO audit_owner', current_database()) \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO audit_app', current_database()) \gexec
SELECT format('REVOKE CREATE ON DATABASE %I FROM PUBLIC', current_database()) \gexec

REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA IF NOT EXISTS audit AUTHORIZATION audit_owner;
CREATE SCHEMA IF NOT EXISTS reporting AUTHORIZATION audit_owner;
CREATE SCHEMA IF NOT EXISTS migrations AUTHORIZATION audit_owner;
ALTER SCHEMA audit OWNER TO audit_owner;
ALTER SCHEMA reporting OWNER TO audit_owner;
ALTER SCHEMA migrations OWNER TO audit_owner;

REVOKE ALL ON SCHEMA audit, reporting, migrations FROM PUBLIC;
GRANT USAGE ON SCHEMA audit, reporting TO audit_app;

-- Tables created later by migrations (run as audit_owner) get the same grants.
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner IN SCHEMA audit
    GRANT SELECT, INSERT ON TABLES TO audit_app;
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner IN SCHEMA audit
    GRANT USAGE, SELECT ON SEQUENCES TO audit_app;
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner IN SCHEMA reporting
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO audit_app;

-- Tables that already exist (when the script is re-run after migrations).
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO audit_app;
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA audit FROM audit_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit TO audit_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA reporting TO audit_app;
REVOKE TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA reporting FROM audit_app;
