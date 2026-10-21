## Selenium UI tests (test branch)

This branch adds a Selenium UI test suite for the Audit log, Reports and Exports pages, and a GitHub Actions pipeline that runs it.
The suite found one product defect, DEF-01: a non-UUID Aggregate ID filter is silently ignored.
See the testing report in [selenium-tests/README.md](selenium-tests/README.md).

# Sellora Audit Service

Epic E7: every business event from the five Sellora topics is stored in an
append-only, hash-chained audit log per company, and sales dashboards are
built from that log.

| Story | What it delivers | Notes |
|---|---|---|
| US-E7-1 (CSP-250) | Immutable audit log: search, entry detail, chain verification | [docs/US-E7-1.md](docs/US-E7-1.md) |
| US-E7-2 (CSP-252) | Reporting projections and sales dashboards (summary, breakdown, rebuild) | [docs/US-E7-2.md](docs/US-E7-2.md) |
| US-E7-3 (CSP-253) | Asynchronous CSV and PDF exports of the reports and audit extracts | [docs/US-E7-3.md](docs/US-E7-3.md) |

Same layout as sellora-order: `Domain` → `Application` → `Infrastructure` → `Api`, plus `tests`. .NET 8.

## Run it locally

Prerequisites: .NET 8 SDK, Docker, and Kafka from `sellora-infra`'s compose file on `localhost:9092`.

```bash
# 1. Database with the real roles (audit_owner, audit_app) on port 5438.
cp .env.example .env            # local-only passwords
docker compose up -d audit-db

# 2. Migrations, as audit_owner (the app never migrates itself).
#    ADMIN_CONNECTION re-applies the grants afterwards; it needs psql on the PATH.
ADMIN_CONNECTION="postgresql://postgres:postgres_dev_pw@localhost:5438/audit_db" \
AUDIT_OWNER_PASSWORD=audit_owner_dev_pw AUDIT_APP_PASSWORD=audit_app_dev_pw \
  ./scripts/run-migrations.sh

# 3. Runtime connection string, as audit_app.
dotnet user-secrets --project src/Sellora.AuditService.Api set "ConnectionStrings:Default" \
  "Host=localhost;Port=5438;Database=audit_db;Username=audit_app;Password=audit_app_dev_pw"

# 4. Run (http://localhost:5208, Swagger at /swagger in Development).
dotnet run --project src/Sellora.AuditService.Api
```

With Docker: `docker build -t sellora-audit .` then `docker run -p 8080:8080 -e ConnectionStrings__Default=... sellora-audit`.
The container listens on 8080; `GET /health` returns `200 {"status":"Healthy"}` and does not depend on the database or Kafka.

## API

| Route | Roles |
|---|---|
| `GET /api/audit?orderReference&aggregateType&aggregateId&actorUserId&eventType&from&to&page&pageSize` | CompanyAdmin, AreaManager |
| `GET /api/audit/{auditEntryId}` | CompanyAdmin, AreaManager |
| `GET /api/audit/verify` | CompanyAdmin |
| `GET /api/reports/sales-summary?from&to[&compareFrom&compareTo]` | CompanyAdmin, AreaManager |
| `GET /api/reports/sales-breakdown?groupBy&from&to[&sort&direction]` | CompanyAdmin, AreaManager |
| `POST /api/reports/projections/rebuild` | CompanyAdmin |
| `POST /api/reports/export` | CompanyAdmin, AreaManager (`audit-extract`: CompanyAdmin) |
| `GET /api/reports/export` | CompanyAdmin, AreaManager (own exports) |
| `GET /api/reports/export/{exportId}` | CompanyAdmin, AreaManager (own exports) |
| `GET /api/reports/export/{exportId}/file` | CompanyAdmin, AreaManager (own exports) |

The company always comes from the token. Area Managers see only their provinces (from Organization). Examples: `src/Sellora.AuditService.Api/Sellora.AuditService.Api.http`.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Default` | `audit_app` on `localhost:5438`, no password | Runtime role: SELECT and INSERT on `audit.audit_entry`; full DML on the `reporting` schema |
| `ConnectionStrings__Owner` | — | Migrations only (`scripts/run-migrations.sh`, `dotnet ef`) |
| `Kafka__BootstrapServers` | `localhost:9092` | |
| `Kafka__SaslUsername` / `Kafka__SaslPassword` | empty | Confluent Cloud API key and secret; SASL_SSL/PLAIN is used when a username is set |
| `Kafka__Topics` | the five `sellora.*.v1` topics | Source topics |
| `Kafka__ConsumerGroupId` | `sellora.audit.v1` | |
| `Kafka__DeadLetterTopic` | `sellora.audit.dead-letter.v1` | [contract](docs/contracts/audit-dead-letter.v1.md) |
| `Kafka__AutoOffsetReset` | `Earliest` | First deploy back-fills what the topics still retain |
| `Kafka__RetryDelaySeconds` | `5` | Wait before replaying after a failure |
| `Dependencies__Organization__BaseUrl` | `http://localhost:8081` | Area Manager provinces (`GET /api/me/scope`) and territory names (`GET /api/hierarchy`) |
| `CallerScope__CacheSeconds` | `60` | Cache for an Area Manager's provinces |
| `Reporting__CacheSeconds` | `60` | In-process cache for report responses (no Redis); `0` turns it off |
| `Exports__PollSeconds` | `5` | How often the export worker looks for queued jobs |
| `Exports__ExpiryHours` | `24` | How long a finished export can be downloaded |
| `Exports__CleanupMinutes` | `60` | How often expired export files are removed |
| `Cors__AllowedOrigins` | `http://localhost:5173` | |
| `Jwt__*` | WSO2 values, as in sellora-order | Raw claim names (`roles`, `companyId`, `sub`) |

## Tests

```bash
dotnet test                                                   # everything; Docker must be running
dotnet test --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~AuditApiTests"   # no Docker needed
```

- `Unit`: envelope normalisation against real producer fixtures (`tests/.../Fixtures`), hash chain with fixed vectors, consumer settings and mapping, role claims, report maths, the Colombo sale date, the projection rules, and export validation, status rules, CSV and PDF writers.
- `Integration`: Testcontainers PostgreSQL 16 with the real roles script and migrations (the runtime role cannot UPDATE/DELETE/TRUNCATE, tamper detection, idempotent and concurrent appends, scoping, reporting figures, projections in the append transaction and rebuild) and Testcontainers Kafka (all five topics, dead-letter, replay after a failure without duplicates, a missing topic).
- `Api`: the real host's routes are read only (the only POSTs are the rebuild and the export submission); 401, 403, 400, 404, 405, 409, 410 and 503 behaviour; Area Manager scoping against PostgreSQL, for the audit, report and export routes.

## Load test data (US-E7-1-Q7)

```bash
psql "host=localhost port=5438 dbname=audit_db user=audit_owner" \
  -v company_id=<company uuid> -v entries=100000 -v orders=20000 -v days=90 \
  -f scripts/seed-audit-entries.sql
```

Continues the company's chain with valid hashes, so verify still passes. Test databases only.
Measured locally (PostgreSQL 16 in Docker, 100,000 entries): seeding 10.7 s, verify 3.1 s, order-reference search 0.3 ms and a 7-day page 0.15 ms at the database.
