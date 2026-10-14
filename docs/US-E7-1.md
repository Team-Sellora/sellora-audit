# US-E7-1 — Immutable append-only audit log (CSP-250)

## What happens to one Kafka record
```
consume (5 topics, group sellora.audit.v1)
  └─ AuditEnvelopeNormaliser (pure)
       ├─ Rejected ─▶ produce to sellora.audit.dead-letter.v1 (awaited) ─▶ commit offset
       └─ Content  ─▶ AuditStore.AppendAsync, one transaction:
                        pg_advisory_xact_lock(hashtext(companyId))
                        (companyId, eventId) already stored? ─▶ Duplicate ─▶ commit offset
                        read last (position, hash) ─▶ insert position+1
                        COMMIT ─▶ commit offset
PostgreSQL data error (SQLSTATE 22/23/54, except unique violations) ─▶ dead-letter ─▶ commit offset
any other exception (database down, broker down) ─▶ NO commit ─▶ rejoin after RetryDelaySeconds ─▶ replay
```
Every event type is stored, including ones Audit does not understand; only records that cannot be stored at all are dead-lettered. A unique-index violation is retried rather than dead-lettered: it can only mean a concurrent append, and the retry sees the duplicate or the new chain head.

## Topics
| Topic | Producer | Envelope it uses |
|---|---|---|
| `sellora.hierarchy.v1` | Organization | headers `event-id` (16 raw bytes), `event-type`, `schema-version`, `company-id`, `correlation-id`; camelCase payload with `entityId`, `effectiveAt`, `operatorId` |
| `sellora.catalog.v1` | Catalog | headers `event-id` (raw bytes), `event-type`, `schema-version`, `company-id`; `PriceChanged` with `changedBy`, `changedAt` |
| `sellora.order.v1` | Order | headers `event-id` (UTF-8 text) etc.; `order-events.v1` payload |
| `sellora.inventory.v1` | Inventory | no headers, null key; PascalCase payload (`LowStockDetected`) |
| `sellora.delivery.v1` | Delivery | headers `x-event-type`, `x-company-id`, `x-correlation-id`, `x-service-source`; PascalCase payload |

The test fixtures in `tests/Sellora.AuditService.Tests/Fixtures` are records in exactly these shapes, taken from each producer's `dev` branch.

## Normalisation (BE3)
- **Headers first, then payload**, for the company, event ID, event type, schema version and correlation ID. Field names are case-insensitive and both `x-` and plain header names are read. A company ID in the header and a different one in the payload is rejected (`CompanyMismatch`).
- **Event ID**: header or payload `eventId`. When a producer sends none, it is derived from `SHA-256(topic|partition|offset)`, so a redelivery of the same record gets the same ID.
- **Occurred at**: `occurredAt`, `effectiveAt`, `changedAt`, `detectedAt`, `acceptedAt`, `createdAt`, else the Kafka timestamp. Stored to the microsecond.
- **Aggregate**: decided from topic and event type first (`AggregateRules`), then the ID field for that type, e.g. `DeliveryStatusChanged` → `Delivery`/`deliveryId` although it also carries `orderId`.
- **Actor**: `actor`, `approvedBy`, `cancelledBy`, `acceptedBy`; system for an `OrderCancelled` without one; the sales rep for `OrderPlaced` and cash-sale `OrderConfirmed`/`PaymentRecorded`; `changedBy`; `operatorId`; otherwise `unknown`.
- **Payload**: stored as the exact text received, never re-serialised.
- Rejected (dead-lettered): empty value, invalid UTF-8, a NUL character (PostgreSQL `text` cannot hold it), not JSON, not a JSON object, no company ID, company mismatch, and any record PostgreSQL refuses with a data error (SQLSTATE class 22/23/54, unique violations excepted).

## Hash chain (BE5)
One chain per company. `hash = lowercase hex SHA-256` of the UTF-8 string

```
chainPosition|companyId|eventId|eventType|aggregateType|aggregateId|occurredAt|correlationId|payload|previousHash
```

- GUIDs lowercase `D` format; a missing aggregate ID or correlation ID is an empty string.
- `occurredAt` is UTC, `yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'` (exactly six fractional digits; the value is truncated to whole microseconds before storing, so it round-trips through `timestamptz`).
- The first entry's `previousHash` is 64 zeros.
- Concurrency: `pg_advisory_xact_lock(hashtext(companyId))` under READ COMMITTED serialises appends per company without blocking other companies. `(company_id, chain_position)` and `(company_id, event_id)` are unique, so a bug cannot fork the chain or store an event twice. Tested with 20 concurrent appends, and with 10 concurrent appends of the same event (one recorded, nine duplicates).

`AuditChainTests` pins two known vectors (checked independently with Python's `hashlib`) so a change to the formula fails CI.

## Three layers of immutability (BE6)
1. **API** — `AuditController` has only GETs and `/health` answers GET and HEAD only. `AuditRouteImmutabilityTests` reads the running host's endpoint table and fails if any endpoint maps PUT, PATCH or DELETE (or no verb at all), or anything other than GET under `api/audit`.
2. **Code** — `IAuditStore` exposes `AppendAsync` only; `AuditEntry` has no public setters and no public constructor; the `AuditEntries` set is internal; `AuditDbContext.SaveChanges` throws if an `AuditEntry` is ever Modified or Deleted.
3. **Database** — `scripts/create-db-roles.sql`: the runtime role `audit_app` has `SELECT, INSERT` on `audit.audit_entry` and nothing else (no UPDATE, DELETE, TRUNCATE, no `FOR UPDATE`, no DDL, no access to the `migrations` schema, cannot `SET ROLE audit_owner`). `audit_owner` owns the schemas and is used only for migrations. `ImmutabilityTests` runs these statements as `audit_app` against a real PostgreSQL and expects `permission denied` (SQLSTATE 42501).

## Roles and migrations (D2)
| Role | Rights | Used by |
|---|---|---|
| `audit_owner` | owns schemas `audit` and `migrations` | `scripts/run-migrations.sh` / CD |
| `audit_app` | `audit.audit_entry`: SELECT, INSERT | the service |

- EF history lives in `migrations.__ef_migrations_history`, which `audit_app` cannot touch.
- The roles script is idempotent and holds no secrets (passwords come in as `psql -v`). Run it as a database administrator before and again after migrations, so default privileges cover new tables.
- The service never calls `Migrate()`.
- The migration is hand-written raw SQL (`Persistence/Migrations/SchemaSql.cs`) so the exact DDL is reviewable. `AuditDbContextModelSnapshot` matches it (`dotnet ef migrations has-pending-model-changes` reports none), so the next `dotnet ef migrations add` contains only the change.

## API (BE7, BE8)
All routes need a token; the company always comes from the token, never a parameter.

| Route | Roles | Notes |
|---|---|---|
| `GET /api/audit?orderReference&aggregateType&aggregateId&actorUserId&eventType&from&to&page&pageSize` | CompanyAdmin, AreaManager | `pageSize` 1–100 (default 20; values outside are clamped, as in sellora-order). Filtered by order or aggregate → oldest first (a timeline); otherwise newest first. `from > to` or a malformed value → 400 |
| `GET /api/audit/{auditEntryId}` | CompanyAdmin, AreaManager | Full entry with hashes and the payload as received. Another company's entry, or one outside an Area Manager's provinces → 404 |
| `GET /api/audit/verify` | CompanyAdmin | `{ verified, entriesChecked, firstBroken: { auditEntryId, chainPosition, eventType, reason } }`; reason `HashMismatch`, `PreviousHashMismatch` or `MissingPosition`. Reads the chain in batches of 1,000 |

Area Managers see only entries whose `provinceId` is one of their provinces (from Organization `GET /api/me/scope`, cached 60 s); entries without a province are Company Admin only. If Organization is down or refuses the lookup, an Area Manager gets 503 rather than an unscoped answer. Roles are read from the raw `roles` claim (`MapInboundClaims = false`), accepting WSO2's `Internal/` prefix. A token without a company or subject gets 401.

## Dead-letter topic
`sellora.audit.dead-letter.v1` — see [contracts/audit-dead-letter.v1.md](contracts/audit-dead-letter.v1.md). The produce is awaited before the source offset is committed: if the dead-letter topic is unavailable the record is replayed, never lost.

## Confluent Cloud set-up (DevOps)
1. Create `sellora.audit.dead-letter.v1` (same partitions as the others is fine; retention 7–14 days). The five source topics must exist before the service starts.
2. Service account for audit: READ on the five source topics, WRITE on the dead-letter topic, READ on consumer group `sellora.audit.v1`.
3. App settings: `Kafka__BootstrapServers`, `Kafka__SaslUsername`, `Kafka__SaslPassword`.
4. Source topic retention decides how far back the first deployment can back-fill (`AutoOffsetReset=Earliest`).

## Web (FE1–FE4)
Built in `sellora-web`, not in this repository: `/audit` (CompanyAdmin, AreaManager) with filters in the URL, paged table, detail drawer (metadata, previous hash and hash, pretty-printed payload), **Verify integrity** for Company Admins, and an **Audit trail** link on the order page.

## QA notes
- Q1: search by the order reference; `OrderPlaced`, `OrderConfirmed`, `PaymentRecorded` appear oldest first with actor, correlation ID and `checkInLocation`.
- Q3(c): connect as `audit_app` and run `UPDATE audit.audit_entry SET payload = '{}'`, `DELETE FROM audit.audit_entry`, `TRUNCATE audit.audit_entry` → `permission denied`.
- Q4: as `audit_owner` on a copy, `UPDATE audit.audit_entry SET payload = '{"tampered":true}' WHERE chain_position = 5 AND company_id = …` → verify names position 5 (`HashMismatch`); delete position 8 → `MissingPosition` at 8.
- Q5: `kafka-consumer-groups --reset-offsets --to-earliest --group sellora.audit.v1 --all-topics --execute` (service stopped) → count unchanged, verify passes. `KafkaConsumerTests` covers the same with a new consumer group from the earliest offset.
- Q7: seed with `scripts/seed-audit-entries.sql` (100,000 entries). See the README for the measured seed and query times; the p95 through APIM must still be measured with JMeter.

## Known limitations
- The hash covers the fields in the formula above. `actor_user_id`, `actor_role`, `order_reference`, `province_id`, `schema_version`, `source_*` and `recorded_at` are derived or transport columns outside it, so a change to them by an elevated user is not detected by verify; the payload they are derived from is covered.
- Deleting the newest entries of a chain is not detectable from the chain alone (there is no later link to break).
- Delivery's `x-correlation-id` header carries its outbox ID, not the request's correlation ID; audit records it as sent.
